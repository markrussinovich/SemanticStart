using System.Collections.Concurrent;
using System.Diagnostics;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Collectors;
using SemanticStart.Core.Model;
using SemanticStart.Core.Query;

namespace SemanticStart.Core.Indexing;

/// <summary>
/// Drives a full index build: discover, diff, enrich, synthesize, embed, persist.
///
/// The expensive stages (documentation gathering and profile synthesis) are gated behind a content
/// hash comparison, so a rebuild on an unchanged machine costs almost nothing. This is what makes
/// it acceptable to reindex on a schedule rather than only on explicit user request.
/// </summary>
public sealed class IndexBuilder
{
    private readonly IReadOnlyList<IEntityCollector> _collectors;
    private readonly IEntityProfiler _profiler;
    private readonly IEmbeddingModel _embeddings;
    private readonly IIndexStore _store;

    public IndexBuilder(
        IReadOnlyList<IEntityCollector> collectors,
        IEntityProfiler profiler,
        IEmbeddingModel embeddings,
        IIndexStore store)
    {
        _collectors = collectors ?? throw new ArgumentNullException(nameof(collectors));
        _profiler = profiler ?? throw new ArgumentNullException(nameof(profiler));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<IndexResult> BuildAsync(
        IndexOptions? options = null,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= IndexOptions.Default;
        var stopwatch = Stopwatch.StartNew();

        await _store.InitializeAsync(_embeddings.ModelId, _embeddings.Dimensions, cancellationToken)
            .ConfigureAwait(false);

        // Discovery always runs every collector, even when the build is scoped to one of them.
        // Deduplication is decided across sources and in registration order, so a partial
        // discovery would change its outcome: with only the PATH collector running, a tool that
        // normally loses to its AppsFolder entry has nothing to lose to, and would be indexed a
        // second time under its bare file name. Discovery is also the cheap phase - the cost this
        // option saves is enrichment and embedding, not collection.
        var discovered = await DiscoverAsync(progress, cancellationToken).ConfigureAwait(false);

        // Always read the stored hashes, even for a forced rebuild. They serve two distinct
        // purposes: deciding what can be skipped, and identifying rows that are no longer
        // discovered. Only the first is disabled by ForceFullRebuild — dropping the second would
        // strand entities that a collector or dedupe change stopped producing.
        var storedHashes = await _store.GetContentHashesAsync(cancellationToken).ConfigureAwait(false);

        // A refresh pass targets stale enrichment, not stale discovery: the content hash of an app
        // whose menus we now read differently has not changed, so hash skipping would skip every
        // entity and the pass would do nothing. Every discovered entity is reprocessed, but almost
        // all of the work is reused.
        var refreshing = options.ReuseStoredDocuments || options.RefreshProviders.Count > 0;

        // Scoping is likewise a request to rebuild, not a request to check for changes. Asking for
        // one source and being told nothing happened because its hashes matched would make the
        // option useless for its main purpose, which is picking up a change to the collector
        // itself - the code moved, so the entity it produces did, but no hash records that.
        var scoped = options.Sources.Count > 0;

        var existingHashes = options.ForceFullRebuild || refreshing || scoped
            ? new Dictionary<string, string>()
            : storedHashes;

        var reuse = refreshing
            ? await LoadReusableAsync(cancellationToken).ConfigureAwait(false)
            : ReusableIndex.Empty;

        var added = 0;
        var updated = 0;
        var unchanged = 0;

        var toProcess = new List<Entity>(discovered.Count);
        foreach (var entity in discovered.Values)
        {
            if (scoped && !options.Sources.Contains(entity.Source))
                continue;

            if (existingHashes.TryGetValue(entity.Id, out var storedHash)
                && storedHash == entity.ContentHash
                && entity.ContentHash is not null)
            {
                unchanged++;
                continue;
            }

            // Whether this is an addition or an update is a question about what is stored, not
            // about which hashes this pass chose to honour. Asking the filtered map meant every
            // forced, refreshed, or scoped rebuild reported its entire index as newly added.
            if (storedHashes.ContainsKey(entity.Id))
                updated++;
            else
                added++;

            toProcess.Add(entity);
        }

        var (failed, firstFailure) = await ProcessAsync(toProcess, options, reuse, progress, cancellationToken).ConfigureAwait(false);

        // Anything previously indexed but no longer discovered has been uninstalled, removed, or
        // collapsed into another entity by deduplication.
        //
        // A scoped build may only retire rows belonging to the sources it rebuilt. Discovery did
        // run everything, so the full set would be accurate here - but acting on it would make a
        // scoped pass capable of deleting from sources the caller did not ask it to touch, and a
        // collector that silently failed mid-enumeration would take its whole source with it.
        var staleIds = storedHashes.Keys.Except(discovered.Keys, StringComparer.Ordinal);
        if (scoped)
            staleIds = staleIds.Where(id => EntityId.SourceOf(id) is { } source && options.Sources.Contains(source));

        var stale = staleIds.ToArray();
        if (stale.Length > 0)
            await _store.RemoveAsync(stale, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        return new IndexResult
        {
            Discovered = scoped
                ? discovered.Values.Count(e => options.Sources.Contains(e.Source))
                : discovered.Count,
            Added = added,
            Updated = updated,
            Unchanged = unchanged,
            Removed = stale.Length,
            Failed = failed,
            FirstFailure = firstFailure,
            Duration = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// Runs every supported collector. Later collectors never displace an entity already claimed
    /// by an earlier one, so registration order acts as dedupe precedence: AppsFolder entries beat
    /// Start Menu shortcuts, which beat bare uninstall-registry rows.
    /// </summary>
    private async Task<Dictionary<string, Entity>> DiscoverAsync(
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        var discovered = new Dictionary<string, Entity>(StringComparer.Ordinal);
        var seenTargets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var collector in _collectors)
        {
            if (!collector.IsSupported)
                continue;

            progress?.Report(new IndexProgress { Phase = "Discovering", CurrentItem = collector.Source });

            try
            {
                await foreach (var collected in collector.CollectAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var entity = await WithResolvedExecutableAsync(collected, cancellationToken).ConfigureAwait(false);

                    if (discovered.ContainsKey(entity.Id))
                        continue;

                    // An entity is a duplicate if *any* of its keys has been claimed. Matching on
                    // several keys rather than one is what catches the same app arriving under
                    // different names: one installed copy of VS Code appears as "Visual Studio
                    // Code" from the AppsFolder and "Microsoft Visual Studio Code (User)" from the
                    // uninstall registry, which no name comparison will ever reconcile, but both
                    // resolve to the same Code.exe.
                    var keys = DedupeKeys(entity);
                    var survivorId = keys
                        .Select(k => seenTargets.TryGetValue(k, out var id) ? id : null)
                        .FirstOrDefault(id => id is not null);

                    if (survivorId is not null)
                    {
                        // The duplicate is suppressed from the results, but its metadata is not
                        // thrown away: the losing record is frequently the richer one. Microsoft
                        // Edge arrives from the AppsFolder carrying nothing but an AppUserModelId
                        // and from the Start Menu carrying the msedge.exe path, and it is that
                        // path the local enrichers need to read a real product description.
                        if (discovered.TryGetValue(survivorId, out var survivor))
                            discovered[survivorId] = Absorb(survivor, entity);

                        // The loser's remaining keys are claimed for the survivor so that a third
                        // record matching either of them collapses in too.
                        foreach (var key in keys)
                            seenTargets.TryAdd(key, survivorId);

                        continue;
                    }

                    foreach (var key in keys)
                        seenTargets[key] = entity.Id;

                    discovered[entity.Id] = entity;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One broken collector must not prevent the rest of the index from building.
                Debug.WriteLine($"Collector '{collector.Source}' failed: {ex}");
            }
        }

        return FoldCommandAliases(discovered);
    }

    /// <summary>
    /// Folds a command alias into the program it launches.
    ///
    /// App Paths registrations arrive from their own collector and cannot be matched on the
    /// executable: Copilot registers "copilotapp.exe" directly under its install directory while
    /// the entry the user sees points at a versioned copy of the same binary one level down, so
    /// the two paths never compare equal and the same program was listed twice.
    ///
    /// Containment alone is not enough to conclude they are the same thing - the Sysinternals
    /// tools all register aliases inside one install directory and are genuinely separate programs,
    /// and folding them would undo the work that made AccessChk reachable at all. The alias is
    /// therefore only folded when it also names the same program, which is what distinguishes
    /// "copilotapp" describing "Copilot" from "accesschk" describing itself.
    /// </summary>
    private static Dictionary<string, Entity> FoldCommandAliases(Dictionary<string, Entity> discovered)
    {
        var hosts = discovered.Values
            .Where(e => !string.Equals(e.Source, "command", StringComparison.Ordinal))
            .Select(e => (Entity: e, Location: e.RawMetadata.GetValueOrDefault("installLocation")?.Trim().Trim('"')))
            .Where(h => !string.IsNullOrWhiteSpace(h.Location))
            .ToArray();

        if (hosts.Length == 0)
            return discovered;

        foreach (var alias in discovered.Values.Where(e => string.Equals(e.Source, "command", StringComparison.Ordinal)).ToArray())
        {
            if (ExecutablePath(alias) is not { } executable)
                continue;

            var claimed = alias.RawMetadata.GetValueOrDefault("description") ?? alias.DisplayName;

            foreach (var (host, location) in hosts)
            {
                if (!discovered.ContainsKey(host.Id))
                    continue;

                var prefix = location!.TrimEnd('\\') + "\\";
                if (!executable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!NameMatcher.Normalize(claimed).Equals(NameMatcher.Normalize(host.DisplayName), StringComparison.Ordinal))
                    continue;

                discovered[host.Id] = Absorb(discovered[host.Id], alias);
                discovered.Remove(alias.Id);
                break;
            }
        }

        return discovered;
    }

    /// <summary>
    /// Fills in the executable behind an AppUserModelId, which the AppsFolder does not supply.
    ///
    /// Without this an AppsFolder entry is nothing but an opaque identifier, with three
    /// consequences: the dedupe key on the resolved binary never fires, so the same tool appears
    /// once per collector that found it; the local file enrichers have no file to read; and the
    /// details panel has no path to show, because an AppUserModelId is not something to put in
    /// front of a user. Resolution is cached per package, so this costs one manifest read per
    /// installed package rather than one per application.
    /// </summary>
    private static async Task<Entity> WithResolvedExecutableAsync(Entity entity, CancellationToken cancellationToken)
    {
        if (entity.LaunchKind != LaunchKind.AppsFolder || entity.RawMetadata.ContainsKey("targetPath"))
            return entity;

        var identifier = entity.RawMetadata.GetValueOrDefault("appUserModelId") ?? entity.LaunchTarget;

        string? executable;
        try
        {
            executable = await PackageCatalog.ResolveExecutableAsync(identifier, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An unreadable package must not cost the entity its place in the index.
            Debug.WriteLine($"Could not resolve executable for '{identifier}': {ex}");
            return entity;
        }

        if (executable is null)
            return entity;

        var metadata = new Dictionary<string, string>(entity.RawMetadata, StringComparer.Ordinal)
        {
            ["targetPath"] = executable,
        };

        return CollectorEntity.WithContentHash(entity with { RawMetadata = metadata });
    }

    /// <summary>
    /// Suppresses the same thing arriving from two collectors, which happens constantly because
    /// most installed apps appear in both the AppsFolder and the Start Menu.
    ///
    /// Applications are deduplicated on normalized display name alone. Keying on the launch
    /// target instead fails here: the AppsFolder yields an AppUserModelId while the Start Menu
    /// yields a .lnk path for the identical app, so the two never collide and the user sees the
    /// entry twice. Registration order in the registry decides the winner, so the collector list
    /// is ordered with the highest fidelity source first.
    ///
    /// Everything else keeps the display name in the key rather than being keyed on target,
    /// because a shared target does not imply a duplicate: all Windows optional features
    /// legitimately deep-link to the same ms-settings:optionalfeatures page.
    /// </summary>
    private static IReadOnlyList<string> DedupeKeys(Entity entity)
    {
        var keys = new List<string>(2) { DedupeKey(entity) };

        // Runnable programs additionally collapse on the executable they launch. The name key
        // cannot catch a vendor-prefixed, scope-suffixed uninstall entry against a bare AppsFolder
        // one, but the resolved binary is identical and is the thing the user actually runs.
        //
        // Arguments are part of the key because a shared executable does not imply a shared app:
        // shortcuts that launch rundll32.exe, control.exe, or msiexec.exe differ only in what they
        // are told to run, and collapsing those would erase genuinely distinct entries.
        if (IsRunnableProgram(entity) && ExecutablePath(entity) is { } executable)
        {
            keys.Add("exe|" + executable.ToLowerInvariant() + "|" + (entity.LaunchArguments ?? string.Empty).ToLowerInvariant());
        }

        return keys;
    }

    /// <summary>
    /// The executable an entity ultimately runs, if it is knowable. AppsFolder entries launch
    /// through an AppUserModelId but record the resolved target alongside it, which is what makes
    /// them comparable with registry and shortcut records at all.
    /// </summary>
    private static string? ExecutablePath(Entity entity)
    {
        if (entity.RawMetadata.TryGetValue("targetPath", out var target) && IsExecutable(target))
            return target;

        if (entity.LaunchKind is LaunchKind.Executable or LaunchKind.Mmc or LaunchKind.Shortcut
            && IsExecutable(entity.LaunchTarget))
            return entity.LaunchTarget;

        // The AppsFolder addresses some items by a shell parsing name rather than by path -
        // "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\MdSched.exe" for the Administrative Tools
        // folder. The trailing segment is a real program in the system directory, and resolving it
        // is what lets that record meet the Start Menu shortcut for the same tool, which carries
        // the full path. Windows Memory Diagnostic and Memory Diagnostics Tool are one program
        // under two names, and nothing but the binary they share can establish that.
        if (entity.LaunchKind == LaunchKind.AppsFolder && ResolveSystemProgram(entity.LaunchTarget) is { } resolved)
            return resolved;

        return null;

        static bool IsExecutable(string? path) =>
            !string.IsNullOrWhiteSpace(path)
            && ProgramExtensions.Contains(Path.GetExtension(path))
            && Path.IsPathFullyQualified(path);
    }

    private static readonly HashSet<string> ProgramExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msc", ".cpl" };

    /// <summary>
    /// Resolves the trailing segment of a shell parsing name to a program in the system
    /// directory, or null when there is no such program. Existence on disk is the whole test: it
    /// is what keeps a bare file name from colliding two unrelated apps that both ship a
    /// "setup.exe", because neither of those is in System32.
    /// </summary>
    private static string? ResolveSystemProgram(string? parsingName)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
            return null;

        var separator = parsingName.LastIndexOf('\\');
        var leaf = separator >= 0 ? parsingName[(separator + 1)..] : parsingName;

        if (leaf.Length == 0
            || !ProgramExtensions.Contains(Path.GetExtension(leaf))
            || leaf.AsSpan().ContainsAny(Path.GetInvalidFileNameChars()))
            return null;

        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            leaf);

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Whether an entity is a program the user runs, as opposed to a place the shell navigates to.
    ///
    /// Every kind on this list is something that starts and does a job, which is what makes two
    /// records for one of them a duplicate rather than two ways in. Settings pages and shell
    /// locations are excluded because they legitimately share both names and targets: every
    /// optional feature deep-links to the same ms-settings:optionalfeatures page.
    /// </summary>
    private static bool IsRunnableProgram(Entity entity) =>
        entity.Kind is EntityKind.Application
            or EntityKind.PackagedApp
            or EntityKind.SystemTool
            or EntityKind.ControlPanelApplet
            or EntityKind.ManagementConsole;

    private static string DedupeKey(Entity entity) => IsRunnableProgram(entity)
        // Runnable programs are deduplicated across kinds as well as across collectors, because
        // the kind records how a program was found rather than what it is. Disk Cleanup is
        // reported both as an AppsFolder application and as a System32 tool; Performance Monitor
        // arrives as an AppsFolder application with an auto-generated id and as a Control Panel
        // snap-in pointing at perfmon.msc, so it was listed twice with two different descriptions
        // and no key in common. They are one thing to the user, and showing either twice in a row
        // looks broken.
        ? "app|" + Normalize(entity.DisplayName)
        : entity.LaunchKind + "|" + entity.LaunchTarget + "|" + Normalize(entity.DisplayName);

    /// <summary>
    /// Folds a suppressed duplicate's metadata into the entity that beat it, filling only keys the
    /// survivor does not already have so dedupe precedence still decides every contested value.
    /// The launch target is deliberately never overwritten: the winning collector's launch path is
    /// the higher-fidelity one, and only the descriptive payload is being salvaged here.
    ///
    /// The content hash is recomputed, otherwise an entity whose text changed would keep its old
    /// hash and the incremental rebuild would skip re-enriching it.
    /// </summary>
    private static Entity Absorb(Entity survivor, Entity duplicate)
    {
        var merged = new Dictionary<string, string>(survivor.RawMetadata, StringComparer.Ordinal);
        var added = false;

        foreach (var (key, value) in duplicate.RawMetadata)
        {
            if (string.IsNullOrWhiteSpace(value) || merged.ContainsKey(key))
                continue;

            merged[key] = value;
            added = true;
        }

        var iconSource = survivor.IconSource ?? duplicate.IconSource;
        var publisher = survivor.Publisher ?? duplicate.Publisher;

        if (!added && ReferenceEquals(iconSource, survivor.IconSource) && ReferenceEquals(publisher, survivor.Publisher))
            return survivor;

        return CollectorEntity.WithContentHash(survivor with
        {
            RawMetadata = merged,
            IconSource = iconSource,
            Publisher = publisher,
        });
    }

    /// <summary>Lowercases and strips non-alphanumerics so "Git Bash" and "Git  Bash" collide.</summary>
    private static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[length++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// Enriches and synthesizes concurrently, then embeds in batches. Embedding is deliberately
    /// serialized into batches after profiling rather than done per entity: the ONNX per-call
    /// overhead dominates the actual matrix multiply at this text length.
    /// </summary>
    /// <summary>
    /// What a refresh pass can carry forward: the documents already gathered for each entity, and
    /// the exact text that produced each stored vector.
    ///
    /// The second half is what makes a refresh cheap in the arm that a document reuse does not
    /// help with. An entity whose embedding text comes out byte-identical would embed to the same
    /// vector, so there is nothing to gain from computing it again - and <c>UpsertAsync</c>
    /// already leaves the stored vector alone when it is handed none.
    /// </summary>
    private sealed record ReusableIndex
    {
        public static readonly ReusableIndex Empty = new();

        public IReadOnlyDictionary<string, IReadOnlyList<EnrichmentDocument>> Documents { get; init; }
            = new Dictionary<string, IReadOnlyList<EnrichmentDocument>>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> EmbeddingTexts { get; init; }
            = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task<ReusableIndex> LoadReusableAsync(CancellationToken cancellationToken)
    {
        var documents = await _store.GetDocumentsAsync(cancellationToken).ConfigureAwait(false);
        var indexed = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in indexed)
        {
            if (item.Profile is null || item.VectorOrdinal is null)
                continue;

            texts[item.Entity.Id] = item.Profile.ToEmbeddingText(item.Entity);
        }

        return new ReusableIndex { Documents = documents, EmbeddingTexts = texts };
    }

    private async Task<(int Failed, string? FirstFailure)> ProcessAsync(
        IReadOnlyList<Entity> entities,
        IndexOptions options,
        ReusableIndex reuse,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
            return (0, null);

        var profiled = new ConcurrentBag<(Entity Entity, IReadOnlyList<EnrichmentDocument> Docs, SynthesizedProfile Profile)>();
        var failed = 0;
        var completed = 0;
        string? firstFailure = null;

        await Parallel.ForEachAsync(
            entities,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.EnrichmentConcurrency,
                CancellationToken = cancellationToken,
            },
            async (entity, ct) =>
            {
                try
                {
                    var stored = reuse.Documents.TryGetValue(entity.Id, out var cached) ? cached : [];

                    var (docs, profile) = await _profiler
                        .ProfileAsync(
                            entity,
                            options.AllowNetwork,
                            stored,
                            options.RefreshProviders,
                            options.ReuseStoredDocuments,
                            ct)
                        .ConfigureAwait(false);

                    profiled.Add((entity, docs, profile));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Interlocked.CompareExchange(ref firstFailure, $"profiling {entity.Id}: {ex.Message}", null);
                    Debug.WriteLine($"Profiling failed for {entity.Id}: {ex}");
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new IndexProgress
                    {
                        Phase = "Enriching",
                        Completed = done,
                        Total = entities.Count,
                        CurrentItem = entity.DisplayName,
                    });
                }
            }).ConfigureAwait(false);

        var items = profiled
            .Select(p => (p.Entity, p.Docs, p.Profile, Text: p.Profile.ToEmbeddingText(p.Entity)))
            .ToArray();

        var persisted = 0;

        // An entity whose embedding text is unchanged already has the right vector on disk.
        // Handing UpsertAsync no embedding leaves it in place, so these skip the model entirely.
        var unchangedIds = items
            .Where(i => reuse.EmbeddingTexts.TryGetValue(i.Entity.Id, out var previous)
                && string.Equals(previous, i.Text, StringComparison.Ordinal))
            .Select(i => i.Entity.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in items.Where(i => unchangedIds.Contains(i.Entity.Id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PersistAsync(item.Entity, item.Docs, item.Profile, null).ConfigureAwait(false);
            persisted++;
        }

        var needsEmbedding = items.Where(i => !unchangedIds.Contains(i.Entity.Id)).ToArray();

        foreach (var batch in needsEmbedding.Chunk(options.EmbeddingBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var texts = batch.Select(b => b.Text).ToArray();

            IReadOnlyList<float[]> vectors;
            try
            {
                vectors = await _embeddings.EmbedAsync(texts, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Persist without vectors rather than losing the batch: the lexical arm still works.
                Debug.WriteLine($"Embedding batch failed: {ex.Message}");
                vectors = [];
            }

            for (var i = 0; i < batch.Length; i++)
            {
                await PersistAsync(batch[i].Entity, batch[i].Docs, batch[i].Profile, i < vectors.Count ? vectors[i] : null)
                    .ConfigureAwait(false);
            }

            persisted += batch.Length;
            progress?.Report(new IndexProgress
            {
                Phase = "Embedding",
                Completed = persisted,
                Total = items.Length,
            });
        }

        return (failed, firstFailure);

        async Task PersistAsync(Entity entity, IReadOnlyList<EnrichmentDocument> docs, SynthesizedProfile profile, float[]? vector)
        {
            try
            {
                await _store.UpsertAsync(entity, docs, profile, vector, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                firstFailure ??= $"persisting {entity.Id}: {ex.Message}";
                Debug.WriteLine($"Persist failed for {entity.Id}: {ex}");
            }
        }
    }
}

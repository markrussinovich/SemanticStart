using System.Collections.Concurrent;
using System.Diagnostics;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Collectors;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Indexing;

/// <summary>
/// Drives a full index build: discover, diff, enrich, synthesize, embed, persist.
///
/// The expensive stages (documentation gathering and LLM synthesis) are gated behind a content
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

        var discovered = await DiscoverAsync(progress, cancellationToken).ConfigureAwait(false);

        // Always read the stored hashes, even for a forced rebuild. They serve two distinct
        // purposes: deciding what can be skipped, and identifying rows that are no longer
        // discovered. Only the first is disabled by ForceFullRebuild — dropping the second would
        // strand entities that a collector or dedupe change stopped producing.
        var storedHashes = await _store.GetContentHashesAsync(cancellationToken).ConfigureAwait(false);

        var existingHashes = options.ForceFullRebuild
            ? new Dictionary<string, string>()
            : storedHashes;

        var added = 0;
        var updated = 0;
        var unchanged = 0;

        var toProcess = new List<Entity>(discovered.Count);
        foreach (var entity in discovered.Values)
        {
            if (existingHashes.TryGetValue(entity.Id, out var storedHash)
                && storedHash == entity.ContentHash
                && entity.ContentHash is not null)
            {
                unchanged++;
                continue;
            }

            if (existingHashes.ContainsKey(entity.Id))
                updated++;
            else
                added++;

            toProcess.Add(entity);
        }

        var failed = await ProcessAsync(toProcess, options, progress, cancellationToken).ConfigureAwait(false);

        // Anything previously indexed but no longer discovered has been uninstalled, removed, or
        // collapsed into another entity by deduplication.
        var stale = storedHashes.Keys.Except(discovered.Keys, StringComparer.Ordinal).ToArray();
        if (stale.Length > 0)
            await _store.RemoveAsync(stale, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();

        return new IndexResult
        {
            Discovered = discovered.Count,
            Added = added,
            Updated = updated,
            Unchanged = unchanged,
            Removed = stale.Length,
            Failed = failed,
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
                await foreach (var entity in collector.CollectAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

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

        return discovered;
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
        if (entity.Kind is EntityKind.Application or EntityKind.PackagedApp or EntityKind.SystemTool
            && ExecutablePath(entity) is { } executable)
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

        if (entity.LaunchKind == LaunchKind.Executable && IsExecutable(entity.LaunchTarget))
            return entity.LaunchTarget;

        return null;

        static bool IsExecutable(string? path) =>
            !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && Path.IsPathFullyQualified(path);
    }

    private static string DedupeKey(Entity entity) => entity.Kind switch
    {
        // Runnable programs are deduplicated across kinds as well as across collectors. Disk
        // Cleanup is reported both as an AppsFolder application and as a System32 tool; they
        // are the same thing to the user, and showing both twice in a row looks broken.
        EntityKind.Application or EntityKind.PackagedApp or EntityKind.SystemTool =>
            "app|" + Normalize(entity.DisplayName),
        _ => entity.LaunchKind + "|" + entity.LaunchTarget + "|" + Normalize(entity.DisplayName),
    };

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
    private async Task<int> ProcessAsync(
        IReadOnlyList<Entity> entities,
        IndexOptions options,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
            return 0;

        var profiled = new ConcurrentBag<(Entity Entity, IReadOnlyList<EnrichmentDocument> Docs, SynthesizedProfile Profile)>();
        var failed = 0;
        var completed = 0;

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
                    var (docs, profile) = await _profiler
                        .ProfileAsync(entity, options.AllowNetwork, ct)
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
                    Debug.WriteLine($"Profiling failed for {entity.Id}: {ex.Message}");
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

        var items = profiled.ToArray();
        var persisted = 0;

        foreach (var batch in items.Chunk(options.EmbeddingBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var texts = batch.Select(b => b.Profile.ToEmbeddingText(b.Entity)).ToArray();

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
                var (entity, docs, profile) = batch[i];
                var vector = i < vectors.Count ? vectors[i] : null;

                try
                {
                    await _store.UpsertAsync(entity, docs, profile, vector, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    Debug.WriteLine($"Persist failed for {entity.Id}: {ex.Message}");
                }
            }

            persisted += batch.Length;
            progress?.Report(new IndexProgress
            {
                Phase = "Embedding",
                Completed = persisted,
                Total = items.Length,
            });
        }

        return failed;
    }
}

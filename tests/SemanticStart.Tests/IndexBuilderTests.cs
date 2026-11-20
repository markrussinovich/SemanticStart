using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Model;
using SemanticStart.Core.Query;
using SemanticStart.Core.Storage;

namespace SemanticStart.Tests;

/// <summary>
/// Regression tests for two defects that both produced a silently wrong index rather than an
/// error, which is the failure mode most likely to survive to release.
/// </summary>
public sealed class IndexBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-idx-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "index.sqlite");
    private string VectorPath => Path.Combine(_dir, "vectors.bin");

    public IndexBuilderTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// The same application is reported by both the AppsFolder and the Start Menu, with a
    /// different launch target from each. Keying deduplication on the target let both through and
    /// the user saw every installed app listed twice.
    /// </summary>
    [Fact]
    public async Task Build_DeduplicatesSameAppArrivingFromDifferentCollectors()
    {
        var appsFolder = new FakeCollector("appsfolder",
        [
            Make("appsfolder", "rdc-aumid", "Remote Desktop Connection", EntityKind.Application, LaunchKind.AppsFolder),
        ]);

        var startMenu = new FakeCollector("startmenu",
        [
            Make("startmenu", @"c:\rdc.lnk", "Remote Desktop Connection", EntityKind.Application, LaunchKind.Shortcut),
            Make("startmenu", @"c:\other.lnk", "Something Else", EntityKind.Application, LaunchKind.Shortcut),
        ]);

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([appsFolder, startMenu], new FakeProfiler(), new FakeEmbeddings(), store);

        var result = await builder.BuildAsync(IndexOptions.Default);

        Assert.Equal(2, result.Discovered);

        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);

        // The first collector registered wins, so the higher fidelity AppsFolder entry survives.
        var rdc = Assert.Single(all, e => e.Entity.DisplayName == "Remote Desktop Connection");
        Assert.Equal("appsfolder", rdc.Entity.Source);
    }

    /// <summary>
    /// Optional features all deep-link to the same ms-settings page. Deduplicating on the launch
    /// target collapsed all of them into a single entity, silently dropping 58 Windows features.
    /// </summary>
    [Fact]
    public async Task Build_KeepsDistinctEntitiesThatShareOneLaunchTarget()
    {
        var features = new FakeCollector("optionalfeature",
        [
            Make("optionalfeature", "wsl", "Windows Subsystem for Linux", EntityKind.OptionalFeature, LaunchKind.Uri, "ms-settings:optionalfeatures"),
            Make("optionalfeature", "sandbox", "Windows Sandbox", EntityKind.OptionalFeature, LaunchKind.Uri, "ms-settings:optionalfeatures"),
            Make("optionalfeature", "hyperv", "Hyper-V", EntityKind.OptionalFeature, LaunchKind.Uri, "ms-settings:optionalfeatures"),
        ]);

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([features], new FakeProfiler(), new FakeEmbeddings(), store);

        var result = await builder.BuildAsync(IndexOptions.Default);

        Assert.Equal(3, result.Discovered);
        Assert.Equal(3, (await store.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Build_KeepsSuiteUtilitiesThatShareOneExecutableAndSettingsPage()
    {
        const string runner = @"C:\PowerToys\PowerToys.exe";
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sharedLaunchTarget"] = "true",
            ["targetPath"] = runner,
        };
        var utilities = new FakeCollector("powertoys",
        [
            Make("powertoys", "find-my-mouse", "Find My Mouse", EntityKind.Application, LaunchKind.Executable, runner) with
            {
                LaunchArguments = "--open-settings=MouseUtils",
                RawMetadata = metadata,
            },
            Make("powertoys", "mouse-highlighter", "Mouse Highlighter", EntityKind.Application, LaunchKind.Executable, runner) with
            {
                LaunchArguments = "--open-settings=MouseUtils",
                RawMetadata = metadata,
            },
        ]);

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([utilities], new FakeProfiler(), new FakeEmbeddings(), store);

        var result = await builder.BuildAsync(IndexOptions.Default);

        Assert.Equal(2, result.Discovered);
        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Build_KeepsSameNamedProgramsFromDifferentDedupeScopes()
    {
        var appsFolder = new FakeCollector("appsfolder",
        [
            Make("appsfolder", "sysinternals-zoomit", "ZoomIt", EntityKind.Application, LaunchKind.AppsFolder),
        ]);
        var powerToys = new FakeCollector("powertoys",
        [
            Make("powertoys", "zoomit", "ZoomIt", EntityKind.Application, LaunchKind.Executable, @"C:\PowerToys\PowerToys.exe") with
            {
                LaunchArguments = "--open-settings=ZoomIt",
                RawMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["dedupeScope"] = "Microsoft PowerToys",
                },
            },
        ]);

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([appsFolder, powerToys], new FakeProfiler(), new FakeEmbeddings(), store);

        var result = await builder.BuildAsync(IndexOptions.Default);

        Assert.Equal(2, result.Discovered);
        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    /// <summary>
    /// The kind of an entity records how it was found, not what it is, so it cannot be part of the
    /// question "are these the same program". Performance Monitor arrives from the AppsFolder as
    /// an Application under an auto-generated id, and from the Control Panel as a ManagementConsole
    /// pointing at perfmon.msc. Nothing about the two records matches except the name, and the
    /// name key used to be reserved for three kinds, so the user saw it listed twice with two
    /// different descriptions.
    /// </summary>
    [Fact]
    public async Task Build_DeduplicatesOneProgramReportedUnderDifferentKinds()
    {
        var appsFolder = new FakeCollector("appsfolder",
        [
            Make("appsfolder", "perfmon-aumid", "Performance Monitor", EntityKind.Application, LaunchKind.AppsFolder),
        ]);

        var controlPanel = new FakeCollector("controlpanel",
        [
            Make("controlpanel", @"c:\windows\system32\perfmon.msc", "Performance Monitor",
                EntityKind.ManagementConsole, LaunchKind.Mmc, @"C:\Windows\system32\perfmon.msc"),
            Make("controlpanel", @"c:\windows\system32\comexp.msc", "Component Services",
                EntityKind.ManagementConsole, LaunchKind.Mmc, @"C:\Windows\system32\comexp.msc"),
        ]);

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([appsFolder, controlPanel], new FakeProfiler(), new FakeEmbeddings(), store);

        await builder.BuildAsync(IndexOptions.Default);

        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, e => e.Entity.DisplayName == "Performance Monitor");
        Assert.Single(all, e => e.Entity.DisplayName == "Component Services");
    }

    /// <summary>
    /// Two records for one program, under two different names, with only the binary in common.
    /// The AppsFolder addresses it by shell parsing name - a folder CLSID followed by the program
    /// - which is not a path, so it never met the Start Menu shortcut that carries the full one.
    /// "Windows Memory Diagnostic" and "Memory Diagnostics Tool" are both MdSched.exe.
    /// </summary>
    [Fact]
    public async Task Build_DeduplicatesOneProgramReachedByShellParsingNameAndByPath()
    {
        // Any real program in the system directory demonstrates the rule; existence on disk is
        // what the resolution turns on, so a fabricated name would prove nothing.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var program = Path.Combine(system, "charmap.exe");
        Assert.True(File.Exists(program), $"expected {program} to exist on any Windows install");

        var appsFolder = new FakeCollector("appsfolder",
        [
            Make("appsfolder", "charmap-shell", "Character Map", EntityKind.Application, LaunchKind.AppsFolder,
                @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\charmap.exe"),
        ]);

        var startMenu = new FakeCollector("startmenu",
        [
            Make("startmenu", @"c:\charmap.lnk", "Character Map Tool", EntityKind.Application, LaunchKind.Shortcut, program),
        ]);

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([appsFolder, startMenu], new FakeProfiler(), new FakeEmbeddings(), store);

        await builder.BuildAsync(IndexOptions.Default);

        var all = await store.GetAllAsync();
        var survivor = Assert.Single(all);
        Assert.Equal("Character Map", survivor.Entity.DisplayName);
    }

    /// <summary>
    /// The parsing-name rule resolves against the system directory and nowhere else. Two unrelated
    /// installers both shipping a "setup.exe" must not collapse into one entry.
    /// </summary>
    [Fact]
    public async Task Build_DoesNotCollapseUnrelatedProgramsSharingAFileName()
    {
        var collector = new FakeCollector("appsfolder",
        [
            Make("appsfolder", "a", "Contoso Setup", EntityKind.Application, LaunchKind.AppsFolder, @"{GUID-A}\setup.exe"),
            Make("appsfolder", "b", "Fabrikam Setup", EntityKind.Application, LaunchKind.AppsFolder, @"{GUID-B}\setup.exe"),
        ]);

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), store);

        await builder.BuildAsync(IndexOptions.Default);

        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    /// <summary>
    /// A forced rebuild clears the hash map used for skip decisions. Reusing that same cleared map
    /// to compute removals meant nothing was ever considered stale, so entities a collector had
    /// stopped producing lingered in the index indefinitely.
    /// </summary>
    [Fact]
    public async Task ForcedRebuild_StillRemovesEntitiesNoLongerDiscovered()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);

        var before = new FakeCollector("test",
        [
            Make("test", "keep", "Keep Me", EntityKind.Application, LaunchKind.Executable, @"c:\keep.exe"),
            Make("test", "drop", "Drop Me", EntityKind.Application, LaunchKind.Executable, @"c:\drop.exe"),
        ]);

        await new IndexBuilder([before], new FakeProfiler(), new FakeEmbeddings(), store)
            .BuildAsync(IndexOptions.Default);

        Assert.Equal(2, (await store.GetAllAsync()).Count);

        var after = new FakeCollector("test",
        [
            Make("test", "keep", "Keep Me", EntityKind.Application, LaunchKind.Executable, @"c:\keep.exe"),
        ]);

        var result = await new IndexBuilder([after], new FakeProfiler(), new FakeEmbeddings(), store)
            .BuildAsync(new IndexOptions { ForceFullRebuild = true });

        Assert.Equal(1, result.Removed);

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Keep Me", all[0].Entity.DisplayName);
    }

    [Fact]
    public async Task Build_SkipsUnchangedEntitiesOnSecondPass()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);

        var collector = new FakeCollector("test",
        [
            Make("test", "a", "App A", EntityKind.Application, LaunchKind.Executable, @"c:\a.exe"),
            Make("test", "b", "App B", EntityKind.Application, LaunchKind.Executable, @"c:\b.exe"),
        ]);

        await new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), store)
            .BuildAsync(IndexOptions.Default);

        var second = await new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), store)
            .BuildAsync(IndexOptions.Default);

        Assert.Equal(2, second.Unchanged);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
    }

    [Fact]
    public async Task Build_DiscoveryRowsAreSearchableBeforeEnrichmentCompletes()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);
        using var embeddings = new FakeEmbeddings();
        var engine = new HybridSearchEngine(store, embeddings);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entity = Make("test", "widget", "Widget", EntityKind.Application, LaunchKind.Executable);
        var profiler = new DelegateProfiler(async (_, ct) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        });
        var build = new IndexBuilder([new FakeCollector("test", [entity])], profiler, embeddings, store)
            .BuildAsync(onBatchCommitted: engine.LoadAsync);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(build.IsCompleted);
            var hit = Assert.Single(await engine.SearchAsync("Widget"));
            Assert.Equal(entity.Id, hit.Entity.Id);
            Assert.Empty(await store.GetContentHashesAsync());
            Assert.Null(Assert.Single(await store.GetAllAsync()).VectorOrdinal);
        }
        finally
        {
            release.TrySetResult();
            await build.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.NotNull(Assert.Single(await store.GetAllAsync()).VectorOrdinal);
    }

    [Fact]
    public async Task Build_PublishesEmbeddedBatchesWhileAnotherEntityIsBlocked()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);
        using var embeddings = new FakeEmbeddings();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Make("test", "first", "First Widget", EntityKind.Application, LaunchKind.Executable);
        var last = Make("test", "last", "Last Widget", EntityKind.Application, LaunchKind.Executable);
        var profiler = new DelegateProfiler(async (entity, ct) =>
        {
            if (entity.Id == last.Id)
                await release.Task.WaitAsync(ct);
        });
        var build = new IndexBuilder([new FakeCollector("test", [first, last])], profiler, embeddings, store)
            .BuildAsync(new IndexOptions { EmbeddingBatchSize = 1, EnrichmentConcurrency = 1 },
                onBatchCommitted: async ct =>
                {
                    if ((await store.GetByIdAsync(first.Id, ct))?.VectorOrdinal is not null)
                        committed.TrySetResult();
                });
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(build.IsCompleted);
            Assert.NotNull((await store.GetByIdAsync(first.Id))!.Profile);
            Assert.Null((await store.GetByIdAsync(last.Id))!.Entity.ContentHash);
        }
        finally
        {
            release.TrySetResult();
            await build.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Build_CancellationKeepsCompletedRowsAndResumesOnlyUnfinishedRows()
    {
        var first = Make("test", "first", "First Widget", EntityKind.Application, LaunchKind.Executable);
        var last = Make("test", "last", "Last Widget", EntityKind.Application, LaunchKind.Executable);
        var collector = new FakeCollector("test", [first, last]);
        using (var store = new SqliteIndexStore(DbPath, VectorPath))
        using (var cancellation = new CancellationTokenSource())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), store)
                    .BuildAsync(new IndexOptions { EmbeddingBatchSize = 1, EnrichmentConcurrency = 1 },
                        cancellationToken: cancellation.Token, onBatchCommitted: async ct =>
                        {
                            if ((await store.GetContentHashesAsync(ct)).Count == 1)
                                cancellation.Cancel();
                        }).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(await store.GetContentHashesAsync());
            Assert.Equal(2, await store.CountAsync());
        }

        using var resumed = new SqliteIndexStore(DbPath, VectorPath);
        var result = await new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), resumed).BuildAsync();
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(1, result.Updated);
        Assert.All(await resumed.GetAllAsync(), item => Assert.NotNull(item.VectorOrdinal));
    }

    [Fact]
    public async Task Build_RetiresUndiscoveredIncompleteRows()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);
        await store.InitializeAsync("test-model", 4);
        await store.UpsertAsync(Make("test", "gone", "Gone Widget", EntityKind.Application, LaunchKind.Executable)
            with
        { ContentHash = null }, [], null, null);
        var result = await new IndexBuilder([], new FakeProfiler(), new FakeEmbeddings(), store).BuildAsync();
        Assert.Equal(1, result.Removed);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Build_EmbeddingFailureKeepsLexicalResultsAndRetriesNextBuild()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var entity = Make("test", "widget", "Widget", EntityKind.Application, LaunchKind.Executable);
        var collector = new FakeCollector("test", [entity]);
        using var failing = new FailingEmbeddings();
        var result = await new IndexBuilder([collector], new FakeProfiler(), failing, store).BuildAsync();
        Assert.Equal(1, result.Failed);
        Assert.Empty(await store.GetContentHashesAsync());
        Assert.Equal(entity.Id, Assert.Single(await new HybridSearchEngine(store, failing).SearchAsync("Widget")).Entity.Id);

        var retry = await new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), store).BuildAsync();
        Assert.Equal(0, retry.Unchanged);
        Assert.Equal(0, retry.Failed);
        Assert.NotNull(Assert.Single(await store.GetAllAsync()).VectorOrdinal);
    }

    private static Entity Make(
        string source, string key, string name, EntityKind kind, LaunchKind launchKind, string? target = null) =>
        new()
        {
            Id = EntityId.Create(source, key),
            Kind = kind,
            DisplayName = name,
            LaunchKind = launchKind,
            LaunchTarget = target ?? key,
            Source = source,
            ContentHash = key + "|" + name,
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeCollector(string source, IReadOnlyList<Entity> entities) : IEntityCollector
    {
        public string Source => source;
        public bool IsSupported => true;

        public async IAsyncEnumerable<Entity> CollectAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var entity in entities)
                yield return entity;
        }
    }

    private sealed class FakeProfiler : IEntityProfiler
    {
        public Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
            Entity entity, bool allowNetwork, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<EnrichmentDocument>, SynthesizedProfile)>((
                [],
                new SynthesizedProfile
                {
                    EntityId = entity.Id,
                    Summary = entity.DisplayName,
                    Generator = "test",
                }));
    }

    private sealed class DelegateProfiler(Func<Entity, CancellationToken, Task> beforeProfile) : IEntityProfiler
    {
        public async Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
            Entity entity, bool allowNetwork, CancellationToken cancellationToken = default)
        {
            await beforeProfile(entity, cancellationToken);
            return await new FakeProfiler().ProfileAsync(entity, allowNetwork, cancellationToken);
        }
    }

    private sealed class FailingEmbeddings : TestEmbeddingGenerator
    {
        public FailingEmbeddings() : base(_ => throw new InvalidOperationException("model failure")) { }
    }

    private sealed class FakeEmbeddings : TestEmbeddingGenerator
    {
        public FakeEmbeddings()
            : base(_ => [1f, 0f, 0f, 0f])
        {
        }
    }
}

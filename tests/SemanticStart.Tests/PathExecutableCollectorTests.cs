using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Collectors;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Model;
using SemanticStart.Core.Storage;

namespace SemanticStart.Tests;

/// <summary>
/// Covers the PATH collector and the source-scoped build that exists to rescan it cheaply.
///
/// Both halves have the same hazard: they touch machine state that tests must not read or write.
/// The collector is therefore driven through its PATH seam with directories this fixture creates,
/// and every executable it is asked about is a real file on disk, because the decisions being
/// tested - is it a zero-byte alias stub, what does its version resource say - are decisions about
/// files.
/// </summary>
public sealed class PathExecutableCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-path-" + Guid.NewGuid().ToString("N"));

    public PathExecutableCollectorTests() => Directory.CreateDirectory(_root);

    private string DbPath => Path.Combine(_root, "index.sqlite");
    private string VectorPath => Path.Combine(_root, "vectors.bin");

    [Fact]
    public async Task Collect_FindsExecutablesInPathDirectories()
    {
        var bin = CreateDirectory("bin");
        CopyRealExecutable(bin, "mytool.exe");

        var entities = await CollectAsync(bin);

        var tool = Assert.Single(entities);
        Assert.Equal("path", tool.Source);
        Assert.Equal(LaunchKind.Executable, tool.LaunchKind);
        Assert.Equal(Path.Combine(bin, "mytool.exe"), tool.LaunchTarget);
        Assert.Equal(bin, tool.RawMetadata["pathDirectory"]);
        Assert.Equal("mytool", tool.RawMetadata["command"]);
    }

    /// <summary>
    /// PATH is first-match-wins. A second copy of a name is shadowed - typing it would never reach
    /// that file - so indexing it would offer the user something the machine will not do.
    /// </summary>
    [Fact]
    public async Task Collect_KeepsOnlyTheFirstCopyOfAShadowedCommand()
    {
        var first = CreateDirectory("first");
        var second = CreateDirectory("second");
        CopyRealExecutable(first, "dupe.exe");
        CopyRealExecutable(second, "dupe.exe");

        var entities = await CollectAsync(first, second);

        var tool = Assert.Single(entities);
        Assert.Equal(first, tool.RawMetadata["pathDirectory"]);
    }

    /// <summary>
    /// Every executable in WindowsApps is a zero-byte MSIX execution alias. There is nothing in
    /// one to index but a file name, and the packaged app behind it is already collected from the
    /// AppsFolder under its real name.
    /// </summary>
    [Fact]
    public async Task Collect_SkipsZeroByteExecutionAliases()
    {
        var bin = CreateDirectory("aliases");
        File.WriteAllBytes(Path.Combine(bin, "alias.exe"), []);
        CopyRealExecutable(bin, "real.exe");

        var entities = await CollectAsync(bin);

        var tool = Assert.Single(entities);
        Assert.Equal("real", tool.RawMetadata["command"]);
    }

    /// <summary>
    /// The Windows tree is on every machine's PATH and holds several hundred executables that
    /// other collectors already cover, with descriptions written for the ones worth finding.
    /// </summary>
    [Fact]
    public async Task Collect_SkipsTheWindowsDirectory()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        Assert.True(Directory.Exists(system));

        var entities = await CollectAsync(system);

        Assert.Empty(entities);
    }

    [Fact]
    public async Task Collect_IgnoresDirectoriesThatDoNotExist()
    {
        var bin = CreateDirectory("present");
        CopyRealExecutable(bin, "present.exe");

        var entities = await CollectAsync(Path.Combine(_root, "no-such-directory"), bin);

        Assert.Single(entities);
    }

    /// <summary>
    /// Ids must be stable across runs, because that is what lets the incremental path recognise an
    /// entity it has already processed rather than re-embedding the whole index every build.
    /// </summary>
    [Fact]
    public async Task Collect_ProducesStableIdsAndHashes()
    {
        var bin = CreateDirectory("stable");
        CopyRealExecutable(bin, "stable.exe");

        var first = Assert.Single(await CollectAsync(bin));
        var second = Assert.Single(await CollectAsync(bin));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    /// <summary>
    /// A PATH entry is a tool directory; one holding more executables than the cap is a system or
    /// install root that reached PATH by accident, and walking it would bury the index.
    /// </summary>
    [Fact]
    public async Task Collect_SkipsImplausiblyLargeDirectories()
    {
        var bin = CreateDirectory("huge");
        for (var i = 0; i < 513; i++)
            File.WriteAllText(Path.Combine(bin, $"tool{i}.exe"), "x");

        Assert.Empty(await CollectAsync(bin));
    }

    /// <summary>
    /// The whole point of the source-scoped build: rebuild one collector without disturbing, or
    /// paying for, any of the others. An unscoped pass would have deleted the appsfolder row,
    /// because a scoped discovery cannot confirm anything about sources it was not asked about.
    /// </summary>
    [Fact]
    public async Task ScopedBuild_LeavesOtherSourcesUntouched()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);

        var apps = new FakeCollector("appsfolder", [Make("appsfolder", "word", "Microsoft Word")]);
        var path = new FakeCollector("path", [Make("path", @"c:\bin\rg.exe", "ripgrep")]);

        await Build(store, [apps, path]).BuildAsync(IndexOptions.Default);
        Assert.Equal(2, (await store.GetAllAsync()).Count);

        // The PATH source changed; nothing else did. Discovery still runs every collector, so the
        // builder knows the appsfolder entry is alive - it simply may not act on it.
        var changedPath = new FakeCollector("path", [Make("path", @"c:\bin\fd.exe", "fd")]);

        var result = await Build(store, [apps, changedPath])
            .BuildAsync(new IndexOptions { Sources = Sources("path") });

        Assert.Equal(1, result.Discovered);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);

        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, e => e.Entity.DisplayName == "Microsoft Word");
        Assert.Single(all, e => e.Entity.DisplayName == "fd");
    }

    /// <summary>
    /// A scoped pass may only retire rows from the sources it rebuilt. Were it to use the full
    /// stale set, a collector that failed halfway through enumeration would take every entity of
    /// an unrelated source with it.
    /// </summary>
    [Fact]
    public async Task ScopedBuild_DoesNotRemoveRowsBelongingToOtherSources()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);

        var apps = new FakeCollector("appsfolder", [Make("appsfolder", "word", "Microsoft Word")]);
        var path = new FakeCollector("path", [Make("path", @"c:\bin\rg.exe", "ripgrep")]);

        await Build(store, [apps, path]).BuildAsync(IndexOptions.Default);

        // The appsfolder collector now reports nothing at all, as a broken one would.
        var silent = new FakeCollector("appsfolder", []);

        var result = await Build(store, [silent, path])
            .BuildAsync(new IndexOptions { Sources = Sources("path") });

        Assert.Equal(0, result.Removed);
        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    /// <summary>
    /// Scoping is a request to rebuild, not to check for changes. A change to collector code moves
    /// the entity it produces without moving any hash, so honouring hashes here would make the
    /// option silently do nothing in the case it exists for.
    /// </summary>
    [Fact]
    public async Task ScopedBuild_ReprocessesEvenWhenNothingChanged()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);

        var apps = new FakeCollector("appsfolder", [Make("appsfolder", "word", "Microsoft Word")]);
        var path = new FakeCollector("path", [Make("path", @"c:\bin\rg.exe", "ripgrep")]);

        await Build(store, [apps, path]).BuildAsync(IndexOptions.Default);

        var result = await Build(store, [apps, path])
            .BuildAsync(new IndexOptions { Sources = Sources("path") });

        Assert.Equal(0, result.Unchanged);
        Assert.Equal(1, result.Updated);
    }

    /// <summary>
    /// Deduplication is decided across sources in registration order, so a scoped build must still
    /// discover everything. Were discovery scoped too, the PATH copy of an app that normally loses
    /// to its AppsFolder entry would have nothing to lose to and would be indexed a second time.
    /// </summary>
    [Fact]
    public async Task ScopedBuild_StillAppliesCrossSourceDeduplication()
    {
        using var store = new SqliteIndexStore(DbPath, VectorPath);

        var apps = new FakeCollector("appsfolder",
        [
            Make("appsfolder", "code", "Visual Studio Code", @"c:\tools\code.exe"),
        ]);

        var path = new FakeCollector("path",
        [
            Make("path", @"c:\tools\code.exe", "code", @"c:\tools\code.exe"),
        ]);

        var result = await Build(store, [apps, path])
            .BuildAsync(new IndexOptions { Sources = Sources("path") });

        // The PATH record lost deduplication to the richer AppsFolder one, so the scoped pass has
        // nothing of its own to process - and must not resurrect it.
        Assert.Equal(0, result.Discovered);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public void SourceOf_RecoversTheCollectorFromAnId()
    {
        Assert.Equal("path", EntityId.SourceOf(EntityId.Create("path", @"c:\bin\rg.exe")));
        Assert.Equal("settings", EntityId.SourceOf("settings:ms-settings:display"));
        Assert.Null(EntityId.SourceOf("no-separator"));
    }

    private async Task<List<Entity>> CollectAsync(params string[] directories)
    {
        var collector = new PathExecutableCollector(() => string.Join(Path.PathSeparator, directories));
        var entities = new List<Entity>();
        await foreach (var entity in collector.CollectAsync())
            entities.Add(entity);
        return entities;
    }

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A real PE image, not a fabricated one: the collector reads the version resource and the PE
    /// subsystem, and a text file pretending to be an executable would exercise neither.
    /// </summary>
    private static void CopyRealExecutable(string directory, string name)
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "where.exe");
        Assert.True(File.Exists(source), $"expected {source} to exist on any Windows install");
        File.Copy(source, Path.Combine(directory, name), overwrite: true);
    }

    private static IndexBuilder Build(SqliteIndexStore store, IReadOnlyList<IEntityCollector> collectors) =>
        new(collectors, new FakeProfiler(), new FakeEmbeddings(), store);

    private static IReadOnlySet<string> Sources(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    private static Entity Make(string source, string key, string name, string? target = null) =>
        new()
        {
            Id = EntityId.Create(source, key),
            Kind = EntityKind.Application,
            DisplayName = name,
            LaunchKind = LaunchKind.Executable,
            LaunchTarget = target ?? key,
            Source = source,
            ContentHash = key + "|" + name,
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
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

    private sealed class FakeEmbeddings : IEmbeddingModel
    {
        public int Dimensions => 4;
        public string ModelId => "test-model";

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ => new[] { 1f, 0f, 0f, 0f })]);

        public void Dispose()
        {
        }
    }
}

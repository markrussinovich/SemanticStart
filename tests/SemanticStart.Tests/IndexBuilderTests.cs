using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Model;
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

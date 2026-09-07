using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Model;
using SemanticStart.Core.Storage;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.Tests;

/// <summary>
/// A change to one enricher used to cost a full rebuild: every other enricher re-run, most of them
/// over the network, to arrive at exactly the text they produced last time. These tests pin the
/// two properties that make a targeted rebuild trustworthy - that the providers not named are
/// carried forward untouched, and that the one named is genuinely run again.
/// </summary>
public sealed class IncrementalRefreshTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-refresh-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "index.sqlite");
    private string VectorPath => Path.Combine(_dir, "vectors.bin");

    public IncrementalRefreshTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Refresh_RerunsOnlyTheNamedProviderAndKeepsTheRest()
    {
        var stable = new CountingEnricher("stable", "stable text");
        var target = new CountingEnricher("target", "first text");

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([Collector()], Pipeline(stable, target), new FakeEmbeddings(), store);

        await builder.BuildAsync(IndexOptions.Default);
        Assert.Equal(1, stable.Calls);
        Assert.Equal(1, target.Calls);

        target.Text = "second text";
        await builder.BuildAsync(new IndexOptions { RefreshProviders = Set("target"), ReuseStoredDocuments = true });

        // The point of the feature: the provider that did not change was not asked again.
        Assert.Equal(1, stable.Calls);
        Assert.Equal(2, target.Calls);

        var documents = await store.GetDocumentsAsync();
        var stored = documents["fake:one"];
        Assert.Equal("stable text", Assert.Single(stored, d => d.Provider == "stable").Text);
        Assert.Equal("second text", Assert.Single(stored, d => d.Provider == "target").Text);
    }

    /// <summary>
    /// A refresh has to survive the ordinary skip path. Nothing about an app changes when we
    /// change how we read it, so its content hash still matches and a normal build would skip it.
    /// </summary>
    [Fact]
    public async Task Refresh_ProcessesEntitiesWhoseContentHashIsUnchanged()
    {
        var target = new CountingEnricher("target", "first text");

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([Collector()], Pipeline(target), new FakeEmbeddings(), store);

        await builder.BuildAsync(IndexOptions.Default);

        var plain = await builder.BuildAsync(IndexOptions.Default);
        Assert.Equal(1, plain.Unchanged);
        Assert.Equal(1, target.Calls);

        var refreshed = await builder.BuildAsync(
            new IndexOptions { RefreshProviders = Set("target"), ReuseStoredDocuments = true });

        Assert.Equal(0, refreshed.Unchanged);
        Assert.Equal(2, target.Calls);
    }

    /// <summary>
    /// A newly added enricher has nothing stored, so it must run even though it was not named.
    /// Otherwise adding a source would still require the full rebuild the feature exists to avoid.
    /// </summary>
    [Fact]
    public async Task Refresh_RunsAProviderThatHasNothingStoredYet()
    {
        var original = new CountingEnricher("original", "original text");
        var added = new CountingEnricher("added", "added text");

        using var store = new SqliteIndexStore(DbPath, VectorPath);

        var before = new IndexBuilder([Collector()], Pipeline(original), new FakeEmbeddings(), store);
        await before.BuildAsync(IndexOptions.Default);

        var after = new IndexBuilder([Collector()], Pipeline(original, added), new FakeEmbeddings(), store);
        await after.BuildAsync(new IndexOptions { RefreshProviders = Set("added") });

        Assert.Equal(1, original.Calls);
        Assert.Equal(1, added.Calls);

        var stored = (await store.GetDocumentsAsync())["fake:one"];
        Assert.Equal(2, stored.Count);
    }

    /// <summary>
    /// Embedding is the other half of the cost. An entity whose embedding text comes out identical
    /// would embed to the identical vector, so the model must not be asked for it again - and the
    /// vector already on disk must survive being handed none.
    /// </summary>
    [Fact]
    public async Task Refresh_SkipsEmbeddingWhenTheTextIsUnchangedButKeepsTheVector()
    {
        var stable = new CountingEnricher("stable", "stable text");
        var embeddings = new FakeEmbeddings();

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var builder = new IndexBuilder([Collector()], Pipeline(stable), embeddings, store);

        await builder.BuildAsync(IndexOptions.Default);
        Assert.Equal(1, embeddings.Batches);

        var ordinalBefore = Assert.Single(await store.GetAllAsync()).VectorOrdinal;
        var matrixBefore = await store.GetVectorMatrixAsync();

        await builder.BuildAsync(new IndexOptions { ReuseStoredDocuments = true });

        Assert.Equal(1, embeddings.Batches);
        Assert.Equal(ordinalBefore, Assert.Single(await store.GetAllAsync()).VectorOrdinal);
        Assert.Equal(matrixBefore, await store.GetVectorMatrixAsync());
    }

    private static IReadOnlySet<string> Set(params string[] providers) =>
        new HashSet<string>(providers, StringComparer.OrdinalIgnoreCase);

    private static EnrichmentPipeline Pipeline(params IEnricher[] enrichers) =>
        new(enrichers, new HeuristicProfileSynthesizer());

    private static IEntityCollector Collector() => new FakeCollector(
    [
        new Entity
        {
            Id = "fake:one",
            Kind = EntityKind.Application,
            DisplayName = "Widget",
            LaunchKind = LaunchKind.Executable,
            LaunchTarget = @"c:\widget.exe",
            Source = "fake",
            ContentHash = "hash-1",
        }
    ]);

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

    private sealed class CountingEnricher(string provider, string text) : IEnricher
    {
        public string Text { get; set; } = text;
        public int Calls { get; private set; }

        public string Provider => provider;
        public bool RequiresNetwork => false;
        public bool CanEnrich(Entity entity) => true;

        public Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(
            Entity entity, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<EnrichmentDocument>>(
            [
                new EnrichmentDocument
                {
                    EntityId = entity.Id,
                    Provider = Provider,
                    IsOnline = false,
                    Text = Text,
                }
            ]);
        }
    }

    private sealed class FakeCollector(IReadOnlyList<Entity> entities) : IEntityCollector
    {
        public string Source => "fake";
        public bool IsSupported => true;

        public async IAsyncEnumerable<Entity> CollectAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var entity in entities)
                yield return entity;
        }
    }

    private sealed class FakeEmbeddings : IEmbeddingModel
    {
        public int Batches { get; private set; }
        public int Dimensions => 4;
        public string ModelId => "test-model";

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Batches++;
            return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(t => new[] { t.Length, 1f, 0f, 0f })]);
        }

        public void Dispose()
        {
        }
    }
}

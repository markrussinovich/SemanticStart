using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Collectors;
using SemanticStart.Core.Embeddings;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Launching;
using SemanticStart.Core.Model;
using SemanticStart.Core.Query;
using SemanticStart.Core.Storage;
using SemanticStart.Core.Synthesis;

namespace SemanticStart.App;

public sealed class SemanticSearchService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SqliteIndexStore _store = new();
    private readonly AppSettings _settings;
    private MiniLmEmbeddingModel? _embeddingModel;
    private HybridSearchEngine? _engine;
    private ShellEntityLauncher? _launcher;
    private bool _initialized;

    public SemanticSearchService(AppSettings settings) => _settings = settings;

    public int Count => _engine?.Count ?? 0;

    public async Task<IReadOnlyDictionary<string, int>> GetGeneratorBreakdownAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var all = await _store.GetAllAsync(cancellationToken);
        return all
            .GroupBy(e => e.Profile?.Generator ?? "none", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            var files = await new EmbeddingModelBootstrapper().EnsureAsync(null, cancellationToken);
            _embeddingModel = new MiniLmEmbeddingModel(files.ModelPath, files.VocabPath);
            await _store.InitializeAsync(_embeddingModel.ModelId, _embeddingModel.Dimensions, cancellationToken);
            _engine = new HybridSearchEngine(_store, _embeddingModel);
            await _engine.LoadAsync(cancellationToken);
            _launcher = new ShellEntityLauncher(_store);
            _initialized = true;
            Log.Info($"Search engine loaded with {_engine.Count} entities.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (_engine is null)
            return [];
        return await _engine.SearchAsync(query, Math.Clamp(limit, 1, 50), cancellationToken);
    }

    public async Task LaunchAsync(Entity entity, LaunchOptions options, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (_launcher is not null)
            await _launcher.LaunchAsync(entity, options, cancellationToken);
    }

    public async Task RebuildIndexAsync(AppSettings settings, bool force, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (_embeddingModel is null || _engine is null)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var synthesizer = new CompositeProfileSynthesizer(
                new LocalLlmProfileSynthesizer(settings.ToLocalLlmOptions()),
                new HeuristicProfileSynthesizer());
            var profiler = new EnrichmentPipeline(EnricherRegistry.CreateAll(), synthesizer);
            var builder = new IndexBuilder(CollectorRegistry.CreateAll(), profiler, _embeddingModel, _store);
            await builder.BuildAsync(new IndexOptions { AllowNetwork = settings.AllowOnlineEnrichment, ForceFullRebuild = force }, progress, cancellationToken);
            _engine.Invalidate();
            await _engine.LoadAsync(cancellationToken);
            Log.Info($"Index rebuilt; {_engine.Count} entities loaded.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _embeddingModel?.Dispose();
        _store.Dispose();
        _gate.Dispose();
    }
}

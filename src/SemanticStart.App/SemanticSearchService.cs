using System.IO;
using SemanticStart.Core;
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

/// <summary>
/// What the index currently contains, grouped the way a user thinks about it rather than by the
/// internal entity kinds: installed programs, the built-in tools and consoles, and the Windows
/// settings surface.
/// </summary>
public sealed record IndexStats(
    int Total,
    int Apps,
    int SystemTools,
    int WindowsSettings,
    int Other,
    long SizeBytes)
{
    public string SizeDisplay => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / (1024.0 * 1024 * 1024):F1} GB"
        : SizeBytes >= 1024 * 1024
            ? $"{SizeBytes / (1024.0 * 1024):F0} MB"
            : $"{SizeBytes / 1024.0:F0} KB";
}

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

    public async Task<IndexStats> GetIndexStatsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var all = await _store.GetAllAsync(cancellationToken);

        var apps = all.Count(e => e.Entity.Kind is EntityKind.Application or EntityKind.PackagedApp);
        var systemTools = all.Count(e => e.Entity.Kind is EntityKind.SystemTool or EntityKind.ManagementConsole);
        var windowsSettings = all.Count(e => e.Entity.Kind is EntityKind.SettingsPage or EntityKind.ControlPanelApplet or EntityKind.OptionalFeature);

        return new IndexStats(
            Total: all.Count,
            Apps: apps,
            SystemTools: systemTools,
            WindowsSettings: windowsSettings,
            Other: all.Count - apps - systemTools - windowsSettings,
            SizeBytes: IndexSizeBytes());
    }

    /// <summary>
    /// Size of everything the index actually occupies on disk. The vectors live outside the
    /// database in a side file, and SQLite's write-ahead log can be a large share of the total
    /// between checkpoints, so reporting index.sqlite alone would understate it.
    /// </summary>
    private static long IndexSizeBytes()
    {
        long total = 0;

        foreach (var path in new[] { AppPaths.IndexDatabase, AppPaths.VectorFile })
        {
            foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
            {
                var info = new FileInfo(candidate);
                if (info.Exists)
                    total += info.Length;
            }
        }

        return total;
    }

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
            var profiler = new EnrichmentPipeline(EnricherRegistry.CreateAll(), new HeuristicProfileSynthesizer());
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

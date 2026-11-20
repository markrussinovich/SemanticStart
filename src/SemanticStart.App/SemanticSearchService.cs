using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.AI;
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
    private IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private HybridSearchEngine? _engine;
    private ShellEntityLauncher? _launcher;
    private bool _initialized;
    private volatile bool _isIndexing;

    public SemanticSearchService(AppSettings settings) => _settings = settings;

    public int Count => _engine?.Count ?? 0;
    public bool IsIndexing => _isIndexing;

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
            _embeddingGenerator = new OnnxEmbeddingGenerator(files.ModelPath, files.VocabPath);
            var metadata = _embeddingGenerator.GetRequiredMetadata();
            await _store.InitializeAsync(
                metadata.DefaultModelId!,
                metadata.DefaultModelDimensions!.Value,
                cancellationToken);
            _engine = new HybridSearchEngine(_store, _embeddingGenerator);
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
        if (_embeddingGenerator is null || _engine is null)
            return;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            _isIndexing = true;
            Log.Info($"Index build started: online={settings.AllowOnlineEnrichment}, force={force}, processors={Environment.ProcessorCount}.");
            var result = await Task.Run(async () =>
            {
                var files = new EmbeddingModelBootstrapper();
                // Interactive queries have their own session; a build cannot monopolize its workers.
                using var indexingEmbeddings = new OnnxEmbeddingGenerator(files.ModelPath, files.VocabPath, intraOpNumThreads: 1);
                var profiler = new EnrichmentPipeline(EnricherRegistry.CreateAll(), new HeuristicProfileSynthesizer(), diagnostic: Log.Info);
                var builder = new IndexBuilder(CollectorRegistry.CreateAll(), profiler, indexingEmbeddings, _store);
                var refreshTimer = Stopwatch.StartNew();
                return await builder.BuildAsync(
                    new IndexOptions { AllowNetwork = settings.AllowOnlineEnrichment, ForceFullRebuild = force },
                    progress, cancellationToken, async ct =>
                    {
                        if (_engine.Count > 0 && refreshTimer.Elapsed < TimeSpan.FromSeconds(1))
                            return;
                        await _engine.LoadAsync(ct).ConfigureAwait(false);
                        refreshTimer.Restart();
                    }).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            Log.Info($"Index build: {result}");
            if (result.Failed > 0)
                throw new InvalidOperationException($"Index build had {result.Failed} failed entities: {result.FirstFailure}");
        }
        finally
        {
            try
            {
                // Cancellation keeps everything committed so far searchable and resumable.
                await Task.Run(() => _engine.LoadAsync(CancellationToken.None)).ConfigureAwait(false);
                Log.Info($"Search snapshot refreshed; {_engine.Count} entities loaded.");
            }
            finally
            {
                _isIndexing = false;
                _gate.Release();
            }
        }
    }

    public void Dispose()
    {
        _embeddingGenerator?.Dispose();
        _store.Dispose();
        _gate.Dispose();
    }
}

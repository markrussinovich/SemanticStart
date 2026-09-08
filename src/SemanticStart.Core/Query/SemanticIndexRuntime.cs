using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Embeddings;
using SemanticStart.Core.Model;
using SemanticStart.Core.Storage;

namespace SemanticStart.Core.Query;

/// <summary>
/// Read-only runtime for processes that query an existing SemanticStart index without owning its
/// creation or lifecycle.
/// </summary>
public sealed class SemanticIndexRuntime : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteIndexStore? _store;
    private MiniLmEmbeddingModel? _embeddings;
    private HybridSearchEngine? _engine;
    private IReadOnlyList<IndexedEntity> _entities = [];
    private bool _disposed;

    public int Count => _entities.Count;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine is not null)
            return;

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_engine is not null)
            {
                await _engine.LoadAsync(cancellationToken).ConfigureAwait(false);
                _entities = await Store.GetAllAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            var files = new EmbeddingModelBootstrapper();
            if (!File.Exists(files.ModelPath) || !File.Exists(files.VocabPath))
            {
                throw new FileNotFoundException(
                    "The local embedding model is missing. Start SemanticStart once to download and initialize it.");
            }

            var embeddings = new MiniLmEmbeddingModel(files.ModelPath, files.VocabPath);
            var store = new SqliteIndexStore();
            try
            {
                await store.OpenReadOnlyAsync(embeddings.ModelId, embeddings.Dimensions, cancellationToken)
                    .ConfigureAwait(false);
                var engine = new HybridSearchEngine(store, embeddings);
                await engine.LoadAsync(cancellationToken).ConfigureAwait(false);
                var entities = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);

                _store = store;
                _embeddings = embeddings;
                _engine = engine;
                _entities = entities;
            }
            catch
            {
                store.Dispose();
                embeddings.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var requested = Math.Clamp(limit, 1, 50);
        var engine = _engine ?? throw new InvalidOperationException("The index runtime is not initialized.");
        return await engine.SearchAsync(query, requested, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IndexedEntity?> GetEntityAsync(string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Store.GetByIdAsync(entityId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexedEntity>> ListEntitiesAsync(
        string? nameContains,
        EntityKind? kind,
        string? publisher,
        string? source,
        string? category,
        string? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<IndexedEntity> query = _entities;
        if (!string.IsNullOrWhiteSpace(afterId))
            query = query.Where(item => string.Compare(item.Entity.Id, afterId, StringComparison.Ordinal) > 0);
        if (!string.IsNullOrWhiteSpace(nameContains))
            query = query.Where(item => item.Entity.DisplayName.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        if (kind is not null)
            query = query.Where(item => item.Entity.Kind == kind);
        if (!string.IsNullOrWhiteSpace(publisher))
            query = query.Where(item => item.Entity.Publisher?.Contains(publisher, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrWhiteSpace(source))
            query = query.Where(item => item.Entity.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(item => item.Profile?.Category?.Contains(category, StringComparison.OrdinalIgnoreCase) == true);

        return [.. query.OrderBy(item => item.Entity.Id, StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 100))];
    }

    public async Task<IReadOnlyList<EnrichmentDocument>> GetDocumentsAsync(
        string entityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await Store.GetDocumentsForEntityAsync(entityId, cancellationToken).ConfigureAwait(false);
    }

    private SqliteIndexStore Store =>
        _store ?? throw new InvalidOperationException("The index runtime is not initialized.");

    public void Dispose()
    {
        if (_disposed)
            return;

        _store?.Dispose();
        _embeddings?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }
}

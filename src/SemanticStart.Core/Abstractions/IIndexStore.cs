using SemanticStart.Core.Model;

namespace SemanticStart.Core.Abstractions;

/// <summary>
/// An entity together with everything derived from it. This is the unit the indexing pipeline
/// writes and the unit the query engine reads back.
/// </summary>
public sealed record IndexedEntity
{
    public required Entity Entity { get; init; }
    public SynthesizedProfile? Profile { get; init; }

    /// <summary>Row index into the vector file, or null if not yet embedded.</summary>
    public int? VectorOrdinal { get; init; }
}

/// <summary>
/// Persistence for the index. Implementations own the SQLite database, the FTS5 mirror, and the
/// vector file, and must keep all three consistent.
/// </summary>
public interface IIndexStore : IDisposable
{
    /// <summary>Opens or creates the index, rebuilding from scratch if incompatible with this build.</summary>
    Task InitializeAsync(string embeddingModelId, int dimensions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Content hashes of everything currently stored, keyed by entity id. The pipeline diffs
    /// freshly collected entities against this to decide what actually needs re-enriching.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes an entity and all of its derived data, replacing any previous version.</summary>
    Task UpsertAsync(
        Entity entity,
        IReadOnlyList<EnrichmentDocument> documents,
        SynthesizedProfile? profile,
        float[]? embedding,
        CancellationToken cancellationToken = default);

    /// <summary>Removes entities that no longer exist on the machine, e.g. uninstalled apps.</summary>
    Task RemoveAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default);

    /// <summary>Loads every entity and profile into memory. The index is small enough that this is cheap.</summary>
    Task<IReadOnlyList<IndexedEntity>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads one entity and profile by its stable id.</summary>
    Task<IndexedEntity?> GetByIdAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every stored enrichment document, grouped by entity. Lets a rebuild carry forward the
    /// providers it is not refreshing instead of collecting their text a second time.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<EnrichmentDocument>>> GetDocumentsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Loads the enrichment documents for one entity.</summary>
    Task<IReadOnlyList<EnrichmentDocument>> GetDocumentsForEntityAsync(
        string entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The full embedding matrix, laid out row-major as [count x dimensions], with each row
    /// L2-normalized. Row i corresponds to the entity whose VectorOrdinal is i.
    /// </summary>
    Task<float[]> GetVectorMatrixAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the FTS5 arm, returning entity ids with their BM25 scores, best first.</summary>
    Task<IReadOnlyList<(string EntityId, double Score)>> SearchLexicalAsync(
        string query, int limit, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, UsageStats>> GetUsageStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Records a launch, incrementing the count and stamping the time.</summary>
    Task RecordLaunchAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>Deletes all documents obtained from the network. Backs the privacy control in Settings.</summary>
    Task PurgeOnlineContentAsync(CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}

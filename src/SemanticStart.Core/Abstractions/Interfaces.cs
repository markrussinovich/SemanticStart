using SemanticStart.Core.Model;

namespace SemanticStart.Core.Abstractions;

/// <summary>
/// Discovers entities from one source. Implementations must be side-effect free, must never
/// require elevation, and must not throw for individual bad items: a single unreadable shortcut
/// or registry key must not abort a whole collection pass.
/// </summary>
public interface IEntityCollector
{
    /// <summary>Short stable name used as the entity id prefix, e.g. "appsfolder".</summary>
    string Source { get; }

    /// <summary>True when this collector can run on the current machine.</summary>
    bool IsSupported { get; }

    IAsyncEnumerable<Entity> CollectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Gathers documentation text about an entity. Local enrichers are always run; enrichers
/// reporting <see cref="RequiresNetwork"/> are only run when the user has opted in.
/// </summary>
public interface IEnricher
{
    string Provider { get; }

    bool RequiresNetwork { get; }

    /// <summary>Cheap pre-filter so we do not pay for enrichers that cannot handle a given entity.</summary>
    bool CanEnrich(Entity entity);

    Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(
        Entity entity,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns raw documentation into a normalized profile. Backed by a local generative model when
/// one is available; implementations must degrade gracefully rather than fail the index build.
/// </summary>
public interface IProfileSynthesizer
{
    string Generator { get; }

    bool IsAvailable { get; }

    Task<SynthesizedProfile> SynthesizeAsync(
        Entity entity,
        IReadOnlyList<EnrichmentDocument> documents,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Produces L2-normalized sentence embeddings. Normalization is the implementation's
/// responsibility so that the query engine can treat a dot product as cosine similarity.
/// </summary>
public interface IEmbeddingModel : IDisposable
{
    /// <summary>Dimensionality of the produced vectors (384 for all-MiniLM-L6-v2).</summary>
    int Dimensions { get; }

    /// <summary>Identifier of the model, persisted with the index so a model change forces a rebuild.</summary>
    string ModelId { get; }

    /// <summary>Embeds a batch. Batching matters: per-call ONNX overhead dominates for single strings.</summary>
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}

/// <summary>Executes a user query against the built index.</summary>
public interface ISearchEngine
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default);
}

/// <summary>Activates an entity and records the launch for ranking purposes.</summary>
public interface IEntityLauncher
{
    Task LaunchAsync(Entity entity, LaunchOptions options = default, CancellationToken cancellationToken = default);
}

/// <summary>Modifiers applied to a launch, driven by the modifier keys held in the overlay.</summary>
public readonly record struct LaunchOptions
{
    /// <summary>Request elevation (Ctrl+Enter).</summary>
    public bool RunAsAdministrator { get; init; }

    /// <summary>Reveal in Explorer instead of launching (Ctrl+Shift+Enter).</summary>
    public bool OpenContainingFolder { get; init; }
}

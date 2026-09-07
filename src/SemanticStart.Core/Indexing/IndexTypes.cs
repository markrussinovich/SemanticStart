using SemanticStart.Core.Model;

namespace SemanticStart.Core.Indexing;

/// <summary>Progress report emitted while the index is being built.</summary>
public sealed record IndexProgress
{
    public required string Phase { get; init; }
    public int Completed { get; init; }
    public int Total { get; init; }
    public string? CurrentItem { get; init; }

    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total, 0, 1);
}

/// <summary>Summary of what a build actually did. Used by the CLI and the settings UI.</summary>
public sealed record IndexResult
{
    public int Discovered { get; init; }
    public int Added { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Removed { get; init; }
    public int Failed { get; init; }

    /// <summary>
    /// Why the first failure happened. Failures were previously written only to the debugger, so
    /// a run in which every single entity failed to persist reported success and a plausible
    /// count; the cause was only found by querying the database by hand.
    /// </summary>
    public string? FirstFailure { get; init; }

    public TimeSpan Duration { get; init; }

    public override string ToString() =>
        $"discovered {Discovered}, added {Added}, updated {Updated}, unchanged {Unchanged}, " +
        $"removed {Removed}, failed {Failed} in {Duration.TotalSeconds:F1}s" +
        (Failed > 0 && FirstFailure is { Length: > 0 } reason ? $"{Environment.NewLine}first failure: {reason}" : string.Empty);
}

/// <summary>Knobs for a build pass.</summary>
public sealed record IndexOptions
{
    /// <summary>When false, no enricher that requires the network is run. This is the default.</summary>
    public bool AllowNetwork { get; init; }

    /// <summary>Ignore stored content hashes and reprocess everything.</summary>
    public bool ForceFullRebuild { get; init; }

    /// <summary>
    /// Providers whose documents are stale and must be gathered again. Everything else is read
    /// back from the index instead of being collected a second time.
    ///
    /// A change to one enricher previously cost a full rebuild - six and a half minutes of
    /// running every other enricher, most of them over the network, to arrive at the same text
    /// they produced last time. What actually went stale is the output of one provider, and the
    /// index already stores documents keyed by provider, so that is the unit that can be thrown
    /// away and remade. Empty means the ordinary content-hash pass.
    /// </summary>
    public IReadOnlySet<string> RefreshProviders { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rebuild profiles and lexical rows from documents already stored, running no enricher at
    /// all. This is the pass for a change to synthesis or to the shape of the indexed text.
    /// </summary>
    public bool ReuseStoredDocuments { get; init; }

    /// <summary>Concurrency for the enrich/synthesize stage, which is I/O and process bound.</summary>
    public int EnrichmentConcurrency { get; init; } = Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>Texts per embedding call. Batching amortizes ONNX per-call overhead.</summary>
    public int EmbeddingBatchSize { get; init; } = 32;

    public static IndexOptions Default { get; } = new();
}

/// <summary>
/// Produces the documents and profile for a single entity. Implemented by the enrichment
/// pipeline; expressed here as a narrow interface so the indexer does not depend on the
/// enrichment implementation.
/// </summary>
public interface IEntityProfiler
{
    Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
        Entity entity,
        bool allowNetwork,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Profiles an entity while collecting as little as possible: documents already held are
    /// reused, and an enricher only runs when its provider is named in
    /// <paramref name="refreshProviders"/> or when nothing is held for it.
    ///
    /// Defaulted so that a profiler with nothing to reuse - a test fake, or one that does its own
    /// caching - is unaffected.
    /// </summary>
    Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
        Entity entity,
        bool allowNetwork,
        IReadOnlyList<EnrichmentDocument> storedDocuments,
        IReadOnlySet<string> refreshProviders,
        bool reuseAll,
        CancellationToken cancellationToken = default)
        => ProfileAsync(entity, allowNetwork, cancellationToken);
}

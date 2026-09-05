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
    public TimeSpan Duration { get; init; }

    public override string ToString() =>
        $"discovered {Discovered}, added {Added}, updated {Updated}, unchanged {Unchanged}, " +
        $"removed {Removed}, failed {Failed} in {Duration.TotalSeconds:F1}s";
}

/// <summary>Knobs for a build pass.</summary>
public sealed record IndexOptions
{
    /// <summary>When false, no enricher that requires the network is run. This is the default.</summary>
    public bool AllowNetwork { get; init; }

    /// <summary>Ignore stored content hashes and reprocess everything.</summary>
    public bool ForceFullRebuild { get; init; }

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
}

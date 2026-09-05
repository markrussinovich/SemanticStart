namespace SemanticStart.Core.Model;

/// <summary>
/// A single piece of documentation gathered about an entity. Kept separate from the entity
/// so that provenance survives into the index and online content can be invalidated or
/// purged independently of local content.
/// </summary>
public sealed record EnrichmentDocument
{
    public required string EntityId { get; init; }

    /// <summary>Where this text came from, e.g. "pe-version", "msix-manifest", "cli-help", "learn".</summary>
    public required string Provider { get; init; }

    /// <summary>True when retrieving this required a network call. Lets us honour the opt-in setting.</summary>
    public required bool IsOnline { get; init; }

    public required string Text { get; init; }

    /// <summary>Original location, for attribution and cache invalidation.</summary>
    public string? SourceUri { get; init; }

    public DateTimeOffset RetrievedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// The normalized, LLM-authored description of an entity. This is the layer that closes the
/// vocabulary gap between how a vendor names a product and how a user describes their intent,
/// and it is what actually gets embedded.
/// </summary>
public sealed record SynthesizedProfile
{
    public required string EntityId { get; init; }

    /// <summary>One sentence describing what the thing does, in plain language.</summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Concrete tasks a user might want to accomplish, phrased the way a user would say them,
    /// e.g. "free up disk space", "see which process has a file open".
    /// </summary>
    public IReadOnlyList<string> Tasks { get; init; } = [];

    /// <summary>Alternative names, abbreviations, and colloquialisms.</summary>
    public IReadOnlyList<string> Synonyms { get; init; } = [];

    /// <summary>Broad category, e.g. "Developer Tools", "Networking", "Accessibility".</summary>
    public string? Category { get; init; }

    /// <summary>
    /// Which generator produced this. "llm:{model}" when synthesized, or "fallback" when the
    /// generative model was unavailable and we degraded to raw documentation.
    /// </summary>
    public required string Generator { get; init; }

    /// <summary>
    /// Builds the text that is handed to the embedding model. Ordering matters: the display
    /// name leads so that name similarity still dominates, followed by intent vocabulary.
    /// </summary>
    public string ToEmbeddingText(Entity entity)
    {
        var parts = new List<string>(8) { entity.DisplayName };

        if (!string.IsNullOrWhiteSpace(Category))
            parts.Add(Category!);

        parts.Add(Summary);

        if (Tasks.Count > 0)
            parts.Add(string.Join(". ", Tasks));

        if (Synonyms.Count > 0)
            parts.Add(string.Join(", ", Synonyms));

        if (!string.IsNullOrWhiteSpace(entity.Publisher))
            parts.Add(entity.Publisher!);

        return string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }
}

/// <summary>Usage telemetry, stored locally only, used to bias ranking toward what the user actually launches.</summary>
public sealed record UsageStats
{
    public required string EntityId { get; init; }
    public int LaunchCount { get; init; }
    public DateTimeOffset? LastLaunchedAt { get; init; }
}

/// <summary>A ranked hit returned from the query engine.</summary>
public sealed record SearchHit
{
    public required Entity Entity { get; init; }

    /// <summary>Final fused-and-boosted score. Only meaningful relative to other hits in the same query.</summary>
    public required double Score { get; init; }

    /// <summary>Cosine similarity from the vector arm, when the vector arm retrieved it.</summary>
    public double? VectorScore { get; init; }

    /// <summary>BM25 relevance from the lexical arm, when the lexical arm retrieved it.</summary>
    public double? LexicalScore { get; init; }

    /// <summary>The one-line description shown under the result. Comes from the synthesized profile.</summary>
    public string? Summary { get; init; }

    /// <summary>Why this matched, for debugging and for the relevance harness.</summary>
    public string? MatchReason { get; init; }
}

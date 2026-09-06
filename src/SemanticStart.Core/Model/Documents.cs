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
/// The normalized description of an entity, derived from its own metadata and the documentation
/// harvested for it. This is the layer that closes the vocabulary gap between how a vendor names a
/// product and how a user describes their intent, and it is what actually gets embedded.
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
    /// Bounded prose from the best document harvested for this entity, describing what it can
    /// actually do. The summary is deliberately one sentence, which is right for display but drops
    /// almost all of the capability vocabulary the enrichers worked to find; this keeps a little of
    /// it for retrieval.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Which generator produced this, recorded so an index can be attributed after the fact.
    /// Currently always "heuristic"; older indexes may carry other values.
    /// </summary>
    public required string Generator { get; init; }

    /// <summary>
    /// Builds the text that is handed to the embedding model. Ordering matters: the display
    /// name leads so that name similarity still dominates, followed by intent vocabulary.
    ///
    /// Text that only restates the entity's own name is dropped. A third of indexed entities have
    /// no real description and fall back to "Open {name}." with a task of "open {name}", so the
    /// name was being repeated three times over; the resulting vector encodes nothing but the
    /// name, which is what let entities merely *called* "... Editor" answer the query "edit"
    /// ahead of every tool that edits something.
    /// </summary>
    public string ToEmbeddingText(Entity entity)
    {
        var parts = new List<string>(8) { entity.DisplayName };

        if (!string.IsNullOrWhiteSpace(Category))
            parts.Add(Category!);

        if (IndexableSummary(entity.DisplayName) is { } summary)
            parts.Add(summary);

        var tasks = IndexableTasks(entity.DisplayName);
        if (tasks.Count > 0)
            parts.Add(string.Join(". ", tasks));

        if (Synonyms.Count > 0)
            parts.Add(string.Join(", ", Synonyms));

        if (!string.IsNullOrWhiteSpace(Details))
            parts.Add(Details!);

        if (!string.IsNullOrWhiteSpace(entity.Publisher))
            parts.Add(entity.Publisher!);

        return string.Join(". ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>
    /// The summary as it should be indexed, or null when it carries nothing beyond the name.
    /// The stored <see cref="Summary"/> is left untouched: "Open Registry Editor." is a poor
    /// search signal but a perfectly good subtitle, so it is suppressed for retrieval only.
    /// </summary>
    public string? IndexableSummary(string displayName) =>
        RestatesName(Summary, displayName) ? null : Summary;

    /// <summary>Tasks with name-restating placeholders removed. See <see cref="IndexableSummary"/>.</summary>
    public IReadOnlyList<string> IndexableTasks(string displayName) =>
        [.. Tasks.Where(t => !RestatesName(t, displayName))];

    /// <summary>
    /// True when every word of <paramref name="text"/> is either part of the entity's own name or
    /// a generic framing word, so the text asserts nothing the name did not already say.
    ///
    /// This matters more than it looks. Such text is not merely useless: it is actively harmful,
    /// because BM25 divides term frequency by field length and these placeholders are the shortest
    /// fields in the index. "Open Registry Editor." in the summary field, weighted three times the
    /// display name, outscored real descriptions of tools that genuinely edit things - and because
    /// the resulting top score also sets the confidence floor for pruning, it pushed Notepad,
    /// Paint and Clipchamp out of the results for "edit" altogether.
    /// </summary>
    private static bool RestatesName(string? text, string displayName)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var nameWords = Tokenize(displayName);
        var informative = Tokenize(text).Where(w => !nameWords.Contains(w) && !FramingWords.Contains(w));

        return !informative.Any();
    }

    /// <summary>
    /// Words that describe the act of opening something rather than what it does. Kept short on
    /// purpose: every addition risks blanking a summary that was doing real work, and the point is
    /// only to see through the fallback phrasing "Open the {name} management console."
    /// </summary>
    private static readonly HashSet<string> FramingWords = new(StringComparer.Ordinal)
    {
        "open", "opens", "launch", "launches", "start", "starts", "run", "runs", "go", "to",
        "the", "a", "an", "and", "or", "for", "of", "in", "on", "with", "your", "this", "it",
        "console", "management", "settings", "setting", "page", "app", "application", "tool",
        "program", "window", "utility", "snap", "applet",
    };

    private static HashSet<string> Tokenize(string value)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder(value.Length);

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
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

    /// <summary>
    /// The intent phrases from the synthesized profile. Surfaced in the expanded result detail so
    /// the user can see what a tool is actually for, which for an unfamiliar name is the whole
    /// point of the index.
    /// </summary>
    public IReadOnlyList<string> Tasks { get; init; } = [];

    /// <summary>Broad category from the synthesized profile.</summary>
    public string? Category { get; init; }
}

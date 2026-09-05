namespace SemanticStart.Core.Query;

/// <summary>
/// Tunables for the hybrid retrieval pipeline. Defaults were chosen so that literal name
/// matching still dominates (users expect "wor" to find Word instantly) while semantic recall
/// handles intent phrases ("free up disk space").
/// </summary>
public sealed record RankingOptions
{
    /// <summary>Candidates pulled from each arm before fusion. Larger costs almost nothing at this index size.</summary>
    public int CandidatesPerArm { get; init; } = 100;

    /// <summary>
    /// RRF damping constant. The standard value from the original reciprocal rank fusion paper;
    /// it keeps any single arm's top hit from completely dominating the fused ordering.
    /// </summary>
    public double RrfK { get; init; } = 60.0;

    public double VectorArmWeight { get; init; } = 1.0;

    public double LexicalArmWeight { get; init; } = 1.0;

    /// <summary>
    /// Cosine floor for the vector arm. Below this a hit is semantic noise; MiniLM assigns
    /// non-trivial similarity to almost any pair of strings, so an explicit floor is required.
    /// </summary>
    public double MinVectorScore { get; init; } = 0.20;

    /// <summary>Score added when the display name starts with the query. This is the "wor" -> "Word" rule.</summary>
    public double PrefixBoost { get; init; } = 1.5;

    /// <summary>Score added when the display name equals the query outright.</summary>
    public double ExactMatchBoost { get; init; } = 3.0;

    /// <summary>Score added when the query matches an acronym of the display name, e.g. "vsc".</summary>
    public double AcronymBoost { get; init; } = 0.9;

    /// <summary>Score added when a whole word of the display name starts with the query.</summary>
    public double WordPrefixBoost { get; init; } = 0.6;

    /// <summary>Ceiling of the frequency component, so a heavily used app cannot permanently bury better matches.</summary>
    public double MaxFrequencyBoost { get; init; } = 0.8;

    /// <summary>Ceiling of the recency component.</summary>
    public double MaxRecencyBoost { get; init; } = 0.4;

    /// <summary>Half-life used to decay the recency boost.</summary>
    public TimeSpan RecencyHalfLife { get; init; } = TimeSpan.FromDays(7);

    public static RankingOptions Default { get; } = new();
}

namespace SemanticStart.Core.Query;

/// <summary>
/// Tunables for the hybrid retrieval pipeline. Defaults were chosen so that literal name
/// matching remains a first-class retrieval arm (users expect "wor" to find Word instantly)
/// while semantic recall handles intent phrases ("free up disk space").
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
    /// Weight for the literal display-name arm. It is intentionally stronger than either
    /// semantic arm so exact and prefix app-name queries win, but it is still paid through RRF
    /// rather than as a raw +1.5/+3.0 score that would dwarf retrieval evidence.
    /// </summary>
    public double LiteralArmWeight { get; init; } = 4.0;

    /// <summary>
    /// Cosine floor for the vector arm. Below this a hit is semantic noise; MiniLM assigns
    /// non-trivial similarity to almost any pair of strings, so an explicit floor is required.
    /// </summary>
    public double MinVectorScore { get; init; } = 0.20;

    /// <summary>Relative literal strength when the display name starts with the query. This is the "wor" -> "Word" rule.</summary>
    public double PrefixBoost { get; init; } = 1.5;

    /// <summary>Relative literal strength when the display name equals the query outright.</summary>
    public double ExactMatchBoost { get; init; } = 3.0;

    /// <summary>Relative literal strength when the query matches an acronym of the display name, e.g. "vsc".</summary>
    public double AcronymBoost { get; init; } = 0.9;

    /// <summary>Relative literal strength when a whole word of the display name starts with the query.</summary>
    public double WordPrefixBoost { get; init; } = 0.6;

    /// <summary>
    /// Ceiling of the frequency component. Kept below one rank-1 RRF arm contribution
    /// (1 / (60 + 1) = 0.0164) so usage can tilt close calls without burying a better match.
    /// </summary>
    public double MaxFrequencyBoost { get; init; } = 0.008;

    /// <summary>Ceiling of the recency component, also kept on the RRF scale.</summary>
    public double MaxRecencyBoost { get; init; } = 0.006;

    /// <summary>Half-life used to decay the recency boost.</summary>
    public TimeSpan RecencyHalfLife { get; init; } = TimeSpan.FromDays(7);

    public static RankingOptions Default { get; } = new();
}

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

    /// <summary>
    /// Minimum cosine for a result that has only vector evidence. In this index, unrelated short
    /// strings routinely cluster around 0.23-0.28, so vector-only hits below this are treated as
    /// embedding noise rather than padded UI results.
    /// </summary>
    public double MinVectorOnlySurfaceScore { get; init; } = 0.35;

    /// <summary>
    /// Minimum cosine for a result that has both vector and lexical evidence. This is lower than
    /// the vector-only floor because two independent arms agreeing is meaningful, but it still
    /// filters generic OR-FTS matches such as a document merely containing "create" or "text".
    /// </summary>
    public double MinHybridSurfaceVectorScore { get; init; } = 0.25;

    /// <summary>
    /// Hybrid hits with weak BM25 must stay within this fraction of the best vector similarity
    /// for the query. RRF scores are not query-comparable, so this compares the underlying cosine
    /// signal and cuts the flat MiniLM noise band beneath a clearly separated leader.
    /// </summary>
    public double MinHybridVectorLeaderRatio { get; init; } = 0.60;

    /// <summary>
    /// Vector-only hits must stay within this fraction of the best vector similarity for the query.
    /// The bar is deliberately stricter than <see cref="MinHybridVectorLeaderRatio"/>: a hit with no
    /// lexical corroboration is resting on a single signal, so it has to be close to the leader to
    /// be worth showing. Searching for a presentation tool surfaced the right app at cosine 0.63 and
    /// then padded the list with a wireless-projection settings page at 0.38, which clears the
    /// absolute floor while being visibly unrelated to what was asked.
    /// </summary>
    public double MinVectorOnlyLeaderRatio { get; init; } = 0.75;

    /// <summary>
    /// Minimum share of the query's best BM25 that a lexical hit must reach before it contributes
    /// to fusion. Reciprocal-rank fusion is rank-based, so without this a row matching one generic
    /// token earns nearly the same arm weight as a row matching the whole query.
    /// </summary>
    public double MinLexicalContributionRatio { get; init; } = 0.45;

    /// <summary>
    /// Minimum BM25 that is strong enough to surface without cosine support. FTS5 now uses OR
    /// semantics, so moderate BM25 can mean only one generic token matched; values around eight
    /// in the current corpus correspond to distinctive names or terms.
    /// </summary>
    public double StrongLexicalScore { get; init; } = 8.0;

    /// <summary>
    /// Fraction of the query's best lexical score at which a hit may surface on lexical evidence
    /// alone, mirroring <see cref="MinVectorOnlyLeaderRatio"/> for the other arm. Deliberately
    /// near-tie: measured at 0.70 this readmitted the weak single-token matches the evidence floors
    /// exist to remove and cost three relevance cases to recover one, so it is set to admit only a
    /// hit that is effectively level with the leader.
    /// </summary>
    public double MinLexicalOnlyLeaderRatio { get; init; } = 0.95;

    /// <summary>
    /// Minimum literal-name strength that counts as independent evidence for surfacing. Exact,
    /// name-prefix, word-prefix, and acronym matches meet this; loose subsequence matches do not
    /// and are only allowed to break ties among candidates with other retrieval evidence.
    /// </summary>
    public double MinLiteralSurfaceStrength { get; init; } = 0.6;

    /// <summary>
    /// Results lacking name evidence or strong BM25 must retain at least this fraction of the
    /// leader's fused score. This removes tail padding after an exact/prefix hit while still
    /// allowing several close semantic answers for broad intent queries.
    /// </summary>
    public double MinRelativeScoreWithoutIndependentEvidence { get; init; } = 0.35;

    /// <summary>
    /// Weight applied to the raw cosine as a fused-score term, in addition to the arm's
    /// reciprocal-rank contribution. RRF is rank-based, and with K = 60 the top dozen ranks differ
    /// by well under a thousandth, so realistic queries routinely produce exact ties that are then
    /// broken arbitrarily. Asking to annotate the screen scored ZoomIt highest of anything in the
    /// index at cosine 0.427 and still ranked it behind the Lock Screen settings page at 0.356.
    /// Kept small: this is a tie-breaker over similarly ranked candidates, not a second ranking.
    /// </summary>
    public double VectorMagnitudeWeight { get; init; } = 0.02;

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

    /// <summary>
    /// Multiplier applied to entities that Windows itself does not list anywhere a user browses -
    /// command-line tools reachable only by typing their name, which their own packages mark as
    /// hidden from the app list.
    ///
    /// They belong in the index: they are frequently the exact tool for the job, and PsSuspend
    /// answers "suspend a process" better than anything on the Start menu does. But their only
    /// documentation is usually a one-line version resource, and a very short field is precisely
    /// what BM25 rewards most, so a tool called "Local and remote password changer" outscored the
    /// Windows sign-in settings for "change my password". The multiplier makes them win on being
    /// clearly right rather than on being tersely described.
    /// </summary>
    public double UnlistedCommandPenalty { get; init; } = 0.85;

    /// <summary>
    /// Share of entities that must use a word before a partial-name match on it is fully
    /// discounted. At 5% of a 553-entity index that is roughly 28 entities - well above an
    /// incidental coincidence, well below a genuinely common verb like "edit".
    /// </summary>
    public double CommonWordShare { get; init; } = 0.05;

    /// <summary>Floor for the partial-name discount, so a common word still breaks exact ties.</summary>
    public double MinPartialNameCredibility { get; init; } = 0.15;

    public static RankingOptions Default { get; } = new();
}

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

    /// <summary>
    /// How close two scores within one arm have to be before that arm is treated as expressing no
    /// preference between them and they share a rank. Fusion reads position as preference, so
    /// without this a hairline margin buys a whole rank of credit while a decisive margin in the
    /// other arm is flattened away.
    /// </summary>
    public double RankTierTolerance { get; init; } = 0.05;

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
    ///
    /// Dropping a row here decides only what this arm will *rank*. The cosine is still recorded
    /// against any candidate another arm found, because being unfit to move a ranking and being
    /// unscored are different facts - see Fuse.
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
    ///
    /// Lowered from 0.25 to make results stop changing while a word is being typed. An absolute
    /// cosine floor is the one part of this pipeline that a partially-typed query moves: the
    /// lexical arm folds "process", "processe" and "processes" to a byte-identical result list,
    /// while MiniLM scores Task Manager at 0.248, 0.298 and 0.367 for them - straddling 0.25, so
    /// an answer appeared, vanished and reappeared as the user typed. Swept at 0.25, 0.22 and
    /// 0.20; 0.22 leaves the corpus untouched and 0.20 costs a case.
    ///
    /// Lowering it was not enough on its own - see
    /// <see cref="CosineFloorLeaderFraction"/>, which is what makes it hold for a
    /// query that is still being typed.
    /// </summary>
    public double MinHybridSurfaceVectorScore { get; init; } = 0.22;

    /// <summary>
    /// Ceiling on every absolute cosine floor in the pipeline, expressed as a fraction of the best
    /// cosine the query actually found. Each floor's effective value is the lower of the two.
    ///
    /// A fixed cosine floor assumes cosine is comparable between queries, and it is not. A query
    /// that is half typed depresses every cosine at once, so a fixed bar stops measuring "is this
    /// related" and starts measuring "is this query finished". "edit do" retrieved 167 entities
    /// and surfaced exactly one: the best cosine in the entire query was 0.220 against a 0.20
    /// retrieval floor, so nearly every candidate was stripped of its vector evidence and arrived
    /// at surfacing looking lexical-only, where the 0.95 lexical-only bar admits the leader and
    /// nothing else. Clipchamp sat at 75% of the vector leader and 72% of the lexical leader -
    /// better relative evidence than the 77%/71% that surfaces it once "edit doc" lifts the leader
    /// to 0.343 - and was dropped anyway. A result list collapsing to one entry mid-word is the
    /// same defect as an answer blinking in and out, one level up.
    ///
    /// Scaling states each floor as a comparison the query can answer for itself: nothing is
    /// required to beat a bar its own best answer barely clears. It only ever relaxes a floor, and
    /// only when the leader is weak - at a healthy leader of 0.343 the product is 0.24, so the
    /// fixed floors still bind and well-formed queries are untouched.
    ///
    /// Applies to <see cref="MinHybridSurfaceVectorScore"/> only. <see cref="MinVectorScore"/> is
    /// excluded because relaxing retrieval widens the candidate pool with the noise that floor
    /// exists to remove: swept from 0.55 to 0.85, it cost two corpus cases and 0.02 MRR at every
    /// value. <see cref="MinVectorOnlySurfaceScore"/> is excluded because with no second arm to
    /// corroborate it, an absolute floor is the only evidence there is.
    ///
    /// Swept at 0.70, 0.75, 0.80, 0.90 and 1.00 (1.00 being the fixed floor). 0.80 is the best
    /// the corpus has measured - 56/61 and MRR 0.864 against 55/61 and 0.860 - because the
    /// scaling has to cut both ways. A low leader does not only mean "the query is unfinished";
    /// it can also mean "nothing in the index matches", which is when a strict bar is most
    /// wanted. "search the web" tops out at 0.277 for that second reason, and below 0.80 the
    /// relaxed floor admits Get Started at 0.217 and pushes Microsoft Edge out of the window.
    /// </summary>
    public double CosineFloorLeaderFraction { get; init; } = 0.80;

    /// <summary>
    /// Hybrid hits with weak BM25 must stay within this fraction of the best vector similarity
    /// for the query. RRF scores are not query-comparable, so this compares the underlying cosine
    /// signal and cuts the flat MiniLM noise band beneath a clearly separated leader.
    /// </summary>
    public double MinHybridVectorLeaderRatio { get; init; } = 0.60;

    /// <summary>
    /// Product of a candidate's two leader ratios - cosine against the best cosine, BM25 against
    /// the best BM25 - at which agreement between the arms substitutes for clearing either arm's
    /// own floor. Set from measurement, not intuition: see the corroboration clause in
    /// HybridSearchEngine.ShouldSurface. Read it as "half the leader in one arm needs about half
    /// the leader in the other"; a candidate carried by a single-token match cannot reach it.
    ///
    /// Swept at 0.15, 0.17, 0.18, 0.19, 0.20, 0.25 and 0.30. The corpus is flat from 0.18 to 0.20
    /// and loses a case outside that band, and 0.18 is taken because it is what the shortest form
    /// of a typed query needs: "list process" puts Task Manager at 0.188, the fuller spellings at
    /// 0.21 and 0.23. Choosing the bottom of a flat band rather than its middle is deliberate -
    /// the cost is nothing measurable and the gain is that a result stops depending on whether a
    /// word has been finished.
    /// </summary>
    public double MinCorroboratedEvidenceProduct { get; init; } = 0.18;

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
    ///
    /// Swept at 0.45, 0.40, 0.35 and 0.30 against the corpus. 0.35 scores better in aggregate -
    /// same cases passing, MRR 0.851 to 0.874, correct answer first 80% of the time to 83% - and
    /// is not used, because what it trades is the reported case. Admitting more weak lexical rows
    /// to fusion gives them rank credit, and they push Task Manager from tenth to twelfth for
    /// "list processes", out of the window entirely. An aggregate gain paid for with the specific
    /// thing a user said was broken is not an improvement.
    /// </summary>
    public double MinLexicalContributionRatio { get; init; } = 0.45;

    /// <summary>
    /// Minimum BM25 that is strong enough to surface without cosine support. FTS5 now uses OR
    /// semantics, so moderate BM25 can mean only one generic token matched; values around eight
    /// in the current corpus correspond to distinctive names or terms.
    /// </summary>
    public double StrongLexicalScore { get; init; } = 8.0;

    /// <summary>
    /// IDF-weighted fraction of a multi-term query that a hit must match before
    /// <see cref="StrongLexicalScore"/> alone may surface it.
    ///
    /// That threshold encodes "a score this high means a distinctive term matched", which is only
    /// true where there is nowhere else for the score to come from. Across several words the same
    /// total is reachable by stacking ordinary ones: "create a todo list" scores Sysinternals
    /// Junction at 8.5 on "Creates and lists directory links" while never matching "todo".
    ///
    /// Near one on purpose. This path is the one that carries a hit whose only evidence is words,
    /// so the words have to be the whole query: Microsoft Edge answers "search the web" on a
    /// cosine of 0.078, 28% of that query's leader, and 100% coverage.
    /// </summary>
    public double MinStrongLexicalCoverage { get; init; } = 0.85;

    /// <summary>
    /// Fraction of the query's best lexical score at which a hit may surface on lexical evidence
    /// alone, mirroring <see cref="MinVectorOnlyLeaderRatio"/> for the other arm. Deliberately
    /// near-tie: measured at 0.70 this readmitted the weak single-token matches the evidence floors
    /// exist to remove and cost three relevance cases to recover one, so it is set to admit only a
    /// hit that is effectively level with the leader.
    /// </summary>
    public double MinLexicalOnlyLeaderRatio { get; init; } = 0.95;

    /// <summary>
    /// Fraction of the query's best cosine below which a recorded cosine is read as the vector arm
    /// actively disagreeing, rather than as the arm having no opinion.
    ///
    /// Cosines this far down are the noise band MiniLM assigns to unrelated text, so a candidate
    /// sitting here has been *scored* and found unrelated. "save a note" ties DxDiag with the
    /// lexical leader at 96% - its harvested text says the tool "can save text files with the scan
    /// results" - on a cosine of 0.015, three per cent of a 0.546 leader.
    ///
    /// Deliberately far below <see cref="MinVectorScore"/> rather than level with it. Treating
    /// every below-floor cosine as disagreement was measured and cost two cases: a cosine under
    /// the retrieval floor is routinely a real but unranked reading, and only the noise band is
    /// evidence of the opposite.
    /// </summary>
    public double SemanticContradictionLeaderRatio { get; init; } = 0.15;

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
    ///
    /// Strengthened from 0.85 after "list processes" returned nine console tools before Task
    /// Manager, most of them claiming only the word "list" - PipeList, ListDLLs, LogonSessions.
    /// That is the failure this multiplier already existed to correct, set too weakly to correct
    /// it. Swept at 0.85, 0.75, 0.65 and 0.55: everything from 0.75 down passes the same cases,
    /// 0.65 is where MRR and top-1 peak, and 0.55 does no better while pushing console tools
    /// further down, which eventually costs the queries they are the right answer to.
    /// </summary>
    public double UnlistedCommandPenalty { get; init; } = 0.65;

    /// <summary>
    /// Multiplier applied to entities that only the uninstall registry knows about.
    ///
    /// That collector is the index's fallback: it exists to catch software that installed itself
    /// without leaving a Start entry, and it reads Add/Remove Programs, which is a list of things
    /// that can be *uninstalled* rather than a list of things that can be run. An entry reaching
    /// the index only through it is one that neither the AppsFolder nor the Start menu considers a
    /// launchable app, so Windows itself would not offer it in response to any search.
    ///
    /// The entries are kept because the fallback earns its place - some genuinely installed
    /// programs have no Start entry - but they are the weakest evidence in the index in a second
    /// way too. With no shortcut and no package manifest, enrichment has nothing to read but the
    /// install directory, so their description is whatever prose happened to sit in a README. That
    /// is how "NVIDIA FrameView SDK", a benchmarking SDK whose summary is a fragment of a CSV
    /// column list, came sixth for "view memory usage", ahead of Process Explorer.
    ///
    /// Swept at 1.0, 0.9, 0.85, 0.75, 0.65 and 0.5 for 56, 56, 56, 57, 57 and 57 cases. Everything
    /// from 0.75 down passes the same set, so 0.65 is taken rather than the edge of the plateau,
    /// and it is not pushed further: these entries are still real installed software and a harder
    /// penalty would eventually cost the queries they are the right answer to.
    /// </summary>
    public double UninstallRecordPenalty { get; init; } = 0.65;

    /// <summary>
    /// Width of a score band, as a fraction of the best score in the result set. Candidates
    /// landing in the same band are treated as having scored equally, and among them one that
    /// Windows ships is listed first.
    ///
    /// The index draws from one machine's installed software, so for any given intent the
    /// competition is uneven: an ordinary request is answered by one inbox app and by however many
    /// third-party programs happen to be installed and to mention the same words. "edit doc"
    /// answered with Python 3.12 Manuals, CMake Documentation and Documentation for Desktop
    /// Apps - none of which edit anything - because "documentation" stems to "document" and there
    /// are simply more of them. Being the answer that exists on every Windows machine is a
    /// property of the entity rather than of the text it matched, so it belongs here rather than
    /// in either arm, and it is only allowed to speak where the arms did not.
    ///
    /// Stated as a tiebreaker because the obvious form does not work. A score multiplier on the
    /// same structural test was swept at 1.05, 1.10, 1.15, 1.20, 1.30 and 1.50 and lost ground at
    /// every value, monotonically: 56/61 down to 54, 53, 53, 52, 50 and 48. A third-party app is
    /// frequently the correct answer - Word for "edit doc", a browser for "search the web" - and
    /// scaling every score by category overturns those clear wins along with the ties it was meant
    /// to settle. Banding can only act where the arms were undecided.
    ///
    /// Swept twice, and the second sweep is the one to trust. The first ran against an index
    /// built before duplicate entries were collapsed, and chose 0.05. Re-swept against the
    /// rebuilt index at 0.0, 0.01, 0.02, 0.03, 0.05 and 0.08, the width turns out to buy nothing:
    /// 56/61 at 0.0 and 0.01, then 56, 55, 55 and 54 as it widens. A band wide enough to cover
    /// real score differences demotes correct third-party answers - at 0.05 "search the web" puts
    /// Internet Information Services and Internet Explorer above Microsoft Edge, which is not
    /// under the Windows directory and so does not read as an inbox app.
    ///
    /// Held at 0.01, which is the widest setting that costs nothing: it lets the preference
    /// settle candidates the fusion scored to within a percent of each other - genuine ties,
    /// where the arms expressed no opinion - and stays silent everywhere else.
    ///
    /// Zero disables the tiebreaker. It has to be handled as a special case rather than falling
    /// out of the arithmetic: a band of zero width would put every candidate in one band and hand
    /// the entire ordering to the tiebreaker, which measures 45/61.
    ///
    /// Decided structurally by HybridSearchEngine.IsWindowsComponent; publisher is not consulted,
    /// because Word, Edge and Clipchamp all say Microsoft and none of them ship with Windows.
    /// </summary>
    public double WindowsComponentTieBand { get; init; } = 0.01;

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

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Query;

/// <summary>
/// Hybrid retrieval over the built index.
///
/// Two arms run against every query. The vector arm supplies semantic recall so that a phrase
/// describing an intent finds a tool whose name shares no words with it. The lexical arm supplies
/// precision on literal names. Neither is sufficient alone: pure vector search embarrassingly
/// fails on short prefixes like "wor", and pure lexical search cannot answer "free up disk space".
/// Results are combined with reciprocal rank fusion. Literal-name matches are modeled as a third
/// ranked arm rather than raw additive scores, and usage boosts are capped to the same order of
/// magnitude as one RRF contribution. Keeping every signal on one scale prevents a frequently
/// launched but weak semantic hit from overwhelming a result retrieved by both semantic arms.
///
/// The whole index is held in memory. At a few thousand entities the vector matrix is only a few
/// megabytes and a brute-force scan beats the complexity of an approximate index.
/// </summary>
public sealed class HybridSearchEngine : ISearchEngine
{
    private readonly IIndexStore _store;
    private readonly IEmbeddingModel _embeddings;
    private readonly RankingOptions _options;

    private readonly SemaphoreSlim _loadLock = new(1, 1);

    /// <summary>
    /// The entity array, the vector matrix, and the usage table must be replaced together. A
    /// background reindex swaps this reference, and a search in flight keeps reading the snapshot
    /// it started with. Holding them in separate fields would let a search pair a new entity list
    /// with an old vector matrix and read off the end of it.
    /// </summary>
    private volatile Snapshot? _snapshot;

    public HybridSearchEngine(IIndexStore store, IEmbeddingModel embeddings, RankingOptions? options = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _options = options ?? RankingOptions.Default;
    }

    /// <summary>Number of entities currently searchable.</summary>
    public int Count => _snapshot?.Entities.Length ?? 0;

    /// <summary>
    /// Pulls the index into memory. Called implicitly by the first search, but the app should
    /// call it at startup so the first keystroke does not pay for it.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entities = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var vectors = await _store.GetVectorMatrixAsync(cancellationToken).ConfigureAwait(false);
            var usage = await _store.GetUsageStatsAsync(cancellationToken).ConfigureAwait(false);

            var array = entities.ToArray();
            var byId = new Dictionary<string, int>(array.Length, StringComparer.Ordinal);
            for (var i = 0; i < array.Length; i++)
                byId[array[i].Entity.Id] = i;

            var byOrdinal = new Dictionary<int, int>(array.Length);
            for (var i = 0; i < array.Length; i++)
            {
                if (array[i].VectorOrdinal is { } ordinal)
                    byOrdinal[ordinal] = i;
            }

            _snapshot = new Snapshot(array, byId, byOrdinal, vectors, _embeddings.Dimensions, usage);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Discards the in-memory snapshot so the next search picks up a rebuilt index.</summary>
    public void Invalidate() => _snapshot = null;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(false);
            snapshot = _snapshot ?? Snapshot.Empty;
        }

        if (string.IsNullOrWhiteSpace(query))
            return MostUsed(snapshot, limit);

        query = query.Trim();

        // The two arms are independent; run them together so the query cost is dominated by
        // whichever is slower rather than by their sum.
        var vectorTask = RunVectorArmAsync(snapshot, query, cancellationToken);
        var lexicalTask = _store.SearchLexicalAsync(query, _options.CandidatesPerArm, cancellationToken);

        await Task.WhenAll(vectorTask, lexicalTask).ConfigureAwait(false);

        var vectorArm = await vectorTask.ConfigureAwait(false);
        var lexicalHits = await lexicalTask.ConfigureAwait(false);

        var fused = Fuse(snapshot, query, vectorArm, lexicalHits);
        var surviving = PruneLowConfidence(fused, ContentTerms(query).Count);

        // Ordered by score band rather than by score, so that a preference for what Windows ships
        // can break ties without ever overturning a decision the arms made clearly. See
        // RankingOptions.WindowsComponentTieBand.
        var best = surviving.Count > 0 ? surviving.Max(c => c.Score) : 0;
        var tiebreak = _options.WindowsComponentTieBand > 0;

        return [.. surviving
            .OrderByDescending(c => ScoreBand(c.Score, best))
            .ThenByDescending(c => tiebreak && IsWindowsComponent(c.Entity.Entity))
            .ThenByDescending(c => c.Score)
            .ThenBy(c => c.Entity.Entity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(c => ToHit(snapshot, c))];
    }

    /// <summary>
    /// Quantizes a score into bands of <see cref="RankingOptions.WindowsComponentTieBand"/> of the
    /// best score in the result set. Two candidates in the same band are treated as having scored
    /// the same, which is what lets a tiebreaker apply without producing an inconsistent ordering:
    /// comparing scores pairwise "within a tolerance" is not transitive and cannot be sorted with.
    /// </summary>
    private int ScoreBand(double score, double best)
    {
        if (best <= 0 || _options.WindowsComponentTieBand <= 0)
            return 0;

        return (int)Math.Floor(score / (best * _options.WindowsComponentTieBand));
    }

    /// <summary>
    /// The vector arm's output: the rows it ranks, and the cosine it assigned to every row in the
    /// index. The second is not a superset used for retrieval - it is evidence, consulted only for
    /// candidates some other arm already found.
    /// </summary>
    private readonly record struct VectorArmResult(
        List<(int Ordinal, double Score)> Ranked,
        double[] ScoreByOrdinal);

    /// <summary>Embeds the query and scans the vector matrix. Returns ordinals paired with cosine similarity.</summary>
    private async Task<VectorArmResult> RunVectorArmAsync(
        Snapshot snapshot, string query, CancellationToken cancellationToken)
    {
        var results = new List<(int, double)>();

        if (snapshot.Vectors.Length == 0 || snapshot.Dimensions == 0)
            return new VectorArmResult(results, []);

        var embedded = await _embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);
        if (embedded.Count == 0)
            return new VectorArmResult(results, []);

        var q = embedded[0];
        var rows = snapshot.Vectors.Length / snapshot.Dimensions;

        // Both sides are L2-normalized by contract, so the dot product is the cosine similarity.
        // Every row is scored and kept, because the floor below decides which rows the arm will
        // *rank*, not which rows it has an opinion about. See Fuse, where the scores of rows below
        // the floor are still recorded as evidence.
        var scores = new double[rows];

        for (var row = 0; row < rows; row++)
        {
            var span = snapshot.Vectors.AsSpan(row * snapshot.Dimensions, snapshot.Dimensions);
            scores[row] = VectorMath.Dot(q, span);

            if (scores[row] >= _options.MinVectorScore)
                results.Add((row, scores[row]));
        }

        results.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));

        if (results.Count > _options.CandidatesPerArm)
            results.RemoveRange(_options.CandidatesPerArm, results.Count - _options.CandidatesPerArm);

        return new VectorArmResult(results, scores);
    }

    /// <summary>
    /// Reciprocal-rank fusion reads a position as a preference, which is only true where the arm
    /// actually preferred one row to another. Registry Editor beat Notepad for "file edit" on a two
    /// per cent lexical margin - a full rank of credit - while giving up a twenty-eight per cent
    /// semantic margin that rank threw away. Rows whose scores sit within a tolerance of each other
    /// share a position, so an arm that barely distinguishes two candidates stops casting a vote
    /// between them and the arm that does distinguish them decides.
    /// </summary>
    private int[] TieredRanks(double[] scoresInRankOrder)
    {
        var ranks = new int[scoresInRankOrder.Length];
        var tier = 0;
        var leader = scoresInRankOrder.Length > 0 ? scoresInRankOrder[0] : 0;

        for (var i = 0; i < scoresInRankOrder.Length; i++)
        {
            if (i > 0 && scoresInRankOrder[i] < leader * (1.0 - _options.RankTierTolerance))
            {
                tier = i;
                leader = scoresInRankOrder[i];
            }

            ranks[i] = tier;
        }

        return ranks;
    }

    private List<Candidate> Fuse(
        Snapshot snapshot,
        string query,
        VectorArmResult vectorArm,
        IReadOnlyList<(string EntityId, double Score)> lexicalHits)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var vectorHits = vectorArm.Ranked;
        var vectorRanks = TieredRanks([.. vectorHits.Select(h => h.Score)]);

        for (var rank = 0; rank < vectorHits.Count; rank++)
        {
            var (ordinal, score) = vectorHits[rank];
            if (!snapshot.IndexByVectorOrdinal.TryGetValue(ordinal, out var index))
                continue;

            var candidate = GetOrAdd(snapshot, candidates, index);
            candidate.VectorScore = score;
            candidate.VectorRanked = true;
            candidate.VectorContribution = _options.VectorArmWeight / (_options.RrfK + vectorRanks[rank] + 1)
                + _options.VectorMagnitudeWeight * score;
            candidate.Score += candidate.VectorContribution;
        }

        // The lexical arm is admitted only where its evidence is competitive with its own best hit.
        // Reciprocal-rank fusion looks at rank and discards magnitude, so a row that matched a
        // single low-information token still enters near the top of the lexical list and collects
        // almost the full arm weight. Asking what is slowing a computer down retrieved the two best
        // semantic matches in the whole index, then buried them under an applet for adding hardware
        // whose sole claim was the word "computer". Weak lexical rows can still surface through the
        // vector or literal arms on their own merit.
        var lexicalLeader = lexicalHits.Count > 0 ? lexicalHits.Max(h => h.Score) : 0;
        var queryTerms = ContentTerms(query);
        var lexicalRanks = TieredRanks([.. lexicalHits.Select(h => h.Score)]);

        for (var rank = 0; rank < lexicalHits.Count; rank++)
        {
            var (entityId, score) = lexicalHits[rank];
            if (!snapshot.IndexById.TryGetValue(entityId, out var index))
                continue;

            var candidate = GetOrAdd(snapshot, candidates, index);

            // The score is recorded even when it is too weak to be paid for, because being unfit
            // to move a ranking and being absent are not the same fact. Discarding it made the
            // engine forget that the lexical arm had found the entity at all, so a candidate the
            // two arms agreed on arrived at the surfacing floors looking like a vector-only guess
            // and was judged by the stricter bar meant for exactly that.
            candidate.LexicalScore = score;

            if (lexicalLeader > 0 && score < lexicalLeader * _options.MinLexicalContributionRatio)
                continue;

            candidate.LexicalContribution = _options.LexicalArmWeight * LexicalCoverageWeight(snapshot, candidate.Entity, queryTerms)
                / (_options.RrfK + lexicalRanks[rank] + 1);
            candidate.Score += candidate.LexicalContribution;
        }

        // The vector arm's opinion of a candidate another arm found is recorded, even where that
        // opinion was too weak to earn the candidate a place in the vector ranking. This is the
        // mirror of the lexical rule above, and it exists for the same reason: not being ranked
        // and not being scored are different facts, and only the surfacing floors need the second.
        //
        // The distinction matters because a fixed cosine floor is not comparable between queries.
        // "edit do" retrieved 167 entities and surfaced one: the best cosine in the entire query
        // was 0.220 against a 0.20 floor, so all but a handful of rows were stripped of their
        // vector score and arrived here looking lexical-only, judged by the 0.95 bar that admits
        // the lexical leader and nothing else. Clipchamp held 75% of the vector leader and 72% of
        // the lexical leader - better relative evidence than the 77%/71% that surfaces it once
        // "edit doc" lifts the leader to 0.343 - and vanished. A list collapsing to one entry
        // mid-word is an answer blinking in and out, one level up.
        //
        // Relaxing the retrieval floor instead was measured and rejected: at every fraction from
        // 0.55 to 0.85 it admits rows the floor exists to remove, and cost two corpus cases and
        // 0.02 MRR. Nothing is retrieved here that was not already retrieved, and no score changes;
        // only what the floors are allowed to know does. Applied in the final pass below, so it
        // covers candidates from every arm.
        var vectorEvidence = vectorArm.ScoreByOrdinal;

        // Literal-name signals are applied to every entity, not just to those an arm retrieved.
        // Without this a very short prefix could miss entirely: it is too short to embed
        // meaningfully and may fall outside the lexical arm's candidate cut. The match strength
        // only orders the literal arm; the amount added is still an RRF reciprocal-rank term.
        var literalHits = new List<(int Index, double Strength, string Reason)>();
        var partialNameCredibility = PartialNameCredibility(snapshot, query);
        for (var i = 0; i < snapshot.Entities.Length; i++)
        {
            var entity = snapshot.Entities[i].Entity;
            var (strength, reason) = NameMatcher.Score(query, entity.DisplayName, _options);

            // People also address a program by the name of the thing that runs it. "msinfo32" and
            // "devenv" are not in any display name, so before this the first returned WOW64 and the
            // second returned nothing at all. The command name is matched with the same rules and
            // the better of the two readings is kept, so typing either what a program is called or
            // what it is named on disk works.
            if (ImageName(entity) is { } imageName)
            {
                var (imageStrength, imageReason) = NameMatcher.Score(query, imageName, _options);
                if (imageStrength > strength)
                    (strength, reason) = (imageStrength, imageReason);
            }

            if (strength <= 0 || reason is null)
                continue;

            // A prefix or subsequence hit is a bet that the user is part-way through typing a
            // name. Damp it when the query is an ordinary word of the corpus, because then the
            // bet is weak: "edit" prefixes "Editor" by morphological accident, and letting that
            // outrank tools that actually edit things is what buried Notepad under Registry
            // Editor, Local Group Policy Editor and Boot Configuration Data Editor. "notep" and
            // "wor" are not words anyone wrote, so they keep the full boost and Notepad and Word
            // still lead the instant they are typed.
            if (reason is "word prefix" or "subsequence")
                strength *= partialNameCredibility;

            if (strength <= 0)
                continue;

            literalHits.Add((i, strength, reason));
        }

        literalHits.Sort((a, b) =>
        {
            var byStrength = b.Strength.CompareTo(a.Strength);
            return byStrength != 0
                ? byStrength
                : string.Compare(
                    snapshot.Entities[a.Index].Entity.DisplayName,
                    snapshot.Entities[b.Index].Entity.DisplayName,
                    StringComparison.OrdinalIgnoreCase);
        });

        for (var rank = 0; rank < literalHits.Count; rank++)
        {
            var (index, strength, reason) = literalHits[rank];
            var candidate = GetOrAdd(snapshot, candidates, index);
            var normalizedStrength = _options.ExactMatchBoost <= 0
                ? 1.0
                : Math.Clamp(strength / _options.ExactMatchBoost, 0.0, 1.0);
            candidate.LiteralContribution =
                _options.LiteralArmWeight * normalizedStrength / (_options.RrfK + rank + 1);
            candidate.Score += candidate.LiteralContribution;
            candidate.LiteralStrength = strength;
            candidate.LiteralReason = reason;
        }

        foreach (var candidate in candidates.Values)
        {
            if (!candidate.VectorScore.HasValue
                && candidate.Entity.VectorOrdinal is { } ordinal
                && (uint)ordinal < (uint)vectorEvidence.Length)
                candidate.VectorScore = vectorEvidence[ordinal];

            // Computed for every candidate the lexical arm found, not only for those it paid, for
            // the same reason the cosine above is recorded: the surfacing floors need to know how
            // much of the query a candidate accounts for, and the arm's own contribution cut is a
            // different question. Candidates no lexical hit reached keep the default, which no
            // path consults because every consumer requires a BM25 score first.
            if (candidate.LexicalScore.HasValue)
                candidate.LexicalCoverage = LexicalCoverageWeight(snapshot, candidate.Entity, queryTerms);

            if (IsUnlistedCommand(candidate.Entity.Entity))
                candidate.Score *= _options.UnlistedCommandPenalty;

            if (IsUninstallRecord(candidate.Entity.Entity))
                candidate.Score *= UninstallRecordPenalty;

            candidate.UsageContribution = UsageBoost(snapshot, candidate.Entity.Entity.Id);
            candidate.Score += candidate.UsageContribution;
            candidate.MatchReason = ExplainMatch(candidate);
        }

        return [.. candidates.Values];
    }

    /// <summary>
    /// True for entities Windows itself ships, as opposed to anything installed onto it.
    ///
    /// Decided structurally, never by naming programs. Five of the seven entity kinds only exist
    /// because Windows defines them - a Settings page, a Control Panel applet, an MMC snap-in, an
    /// optional feature and a System32 console tool cannot come from anywhere else - and for the
    /// two mixed kinds the test is whether the thing being launched lives under the Windows
    /// directory. Publisher is deliberately not consulted: Word, Edge and Clipchamp all say
    /// Microsoft and none of them ship with Windows.
    /// </summary>
    private static bool IsWindowsComponent(Entity entity)
    {
        if (entity.Kind is EntityKind.SettingsPage
            or EntityKind.ControlPanelApplet
            or EntityKind.ManagementConsole
            or EntityKind.OptionalFeature
            or EntityKind.SystemTool)
            return true;

        if (entity.LaunchKind == LaunchKind.ControlPanel || entity.LaunchKind == LaunchKind.Mmc)
            return true;

        if (entity.LaunchKind == LaunchKind.Uri)
            return entity.LaunchTarget.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase);

        return IsUnderWindowsDirectory(entity.LaunchTarget)
            || IsUnderWindowsDirectory(entity.IconSource);
    }

    private static readonly string WindowsDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static bool IsUnderWindowsDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || WindowsDirectory.Length == 0)
            return false;

        // The launch target may be an AppUserModelId or a bare command name rather than a path;
        // both simply fail this test, which is the intended answer for anything unresolvable.
        return path.StartsWith(WindowsDirectory, StringComparison.OrdinalIgnoreCase)
            && path.Length > WindowsDirectory.Length
            && (path[WindowsDirectory.Length] == Path.DirectorySeparatorChar
                || path[WindowsDirectory.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// True for entities that only the command-alias collector found, meaning no app list, Start
    /// menu, or settings surface shows them. See <see cref="RankingOptions.UnlistedCommandPenalty"/>.
    /// </summary>
    private static bool IsUnlistedCommand(Entity entity) =>
        string.Equals(entity.Source, "command", StringComparison.Ordinal);

    /// <summary>
    /// True for entities the uninstall-registry collector found and no other collector did. Ids are
    /// "source:key", so an entity carries the single source it was discovered through.
    /// See <see cref="RankingOptions.UninstallRecordPenalty"/>.
    /// </summary>
    private static bool IsUninstallRecord(Entity entity) =>
        string.Equals(entity.Source, "uninstall", StringComparison.Ordinal);

    /// <summary>
    /// The penalty in force, overridable from the environment so it can be swept against a fixed
    /// index without a rebuild, in the same way as the BM25 column weights.
    /// </summary>
    private double UninstallRecordPenalty =>
        double.TryParse(
            Environment.GetEnvironmentVariable("SEMANTICSTART_UNINSTALL_PENALTY"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var configured) && configured > 0
            ? configured
            : _options.UninstallRecordPenalty;

    /// <summary>
    /// Removes low-confidence tail results instead of padding the UI to the requested count.
    /// Final RRF scores are only ranks and are not comparable across queries, so the cutoff is
    /// based on the underlying evidence: literal name matches, strong BM25, absolute vector
    /// floors, and a relative score guard for weak-evidence tails after a strong leader.
    /// </summary>
    private List<Candidate> PruneLowConfidence(List<Candidate> candidates, int queryTermCount)
    {
        if (candidates.Count == 0)
            return candidates;

        var topScore = candidates.Max(c => c.Score);
        var topVector = candidates
            .Where(c => c.VectorScore.HasValue)
            .Select(c => c.VectorScore!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var topLexical = candidates
            .Where(c => c.LexicalScore.HasValue)
            .Select(c => c.LexicalScore!.Value)
            .DefaultIfEmpty(0)
            .Max();

        return [.. candidates.Where(c => ShouldSurface(c, topScore, topVector, topLexical, queryTermCount))];
    }

    private bool ShouldSurface(Candidate candidate, double topScore, double topVector, double topLexical, int queryTermCount)
    {
        // Cosine is not comparable between queries, so the floor is stated relative to the best
        // cosine this query found and capped by the fixed value. See
        // RankingOptions.CosineFloorLeaderFraction.
        var vectorFloor = topVector > 0
            ? Math.Min(
                _options.MinHybridSurfaceVectorScore,
                topVector * _options.CosineFloorLeaderFraction)
            : _options.MinHybridSurfaceVectorScore;

        if (candidate.LiteralStrength >= _options.MinLiteralSurfaceStrength)
            return true;

        // BM25 this high is meant to be proof that a *distinctive* term matched: "values around
        // eight in the current corpus correspond to distinctive names or terms". That reading
        // holds for a short query, where there is nowhere else for the score to come from. A
        // multi-word query breaks it, because the same total is reachable by stacking ordinary
        // words: "create a todo list" scored Sysinternals Junction at 8.5 on "Creates and lists
        // directory links", from the two words the query shares with most of the index, while the
        // term that actually says what the user wants appears nowhere in it.
        //
        // So for a multi-word query the score has to be backed by the match covering the query,
        // measured by IDF so that missing the telling word is what costs. Junction covers 58% and
        // is out; Microsoft Edge for "search the web" covers 100% and stays, which is the case
        // this path exists for - its cosine is 28% of that query's leader, far below every other
        // floor, and matching the whole query is the only evidence it has.
        //
        // Reading the cosine here instead was tried first and is wrong: it drops Edge, whose
        // 0.078 is below the floor for exactly the reason the vector arm is unreliable on a
        // two-word query that names no product.
        if (candidate.LexicalScore >= _options.StrongLexicalScore
            && (queryTermCount < 2 || candidate.LexicalCoverage >= _options.MinStrongLexicalCoverage))
            return true;

        // A hit may also surface on lexical evidence alone, but only when it is essentially tied
        // with the best lexical score for the query. A looser bar was tried at 0.70 and rejected:
        // it readmitted the weak single-token matches these floors exist to remove, costing three
        // other cases to recover one. The remaining recall gap it was aimed at ("edit a file" not
        // reaching Visual Studio Code) is a profile-quality problem, not a ranking one - VS Code's
        // harvested text is marketing prose that never states the action - and it is fixed by
        // better synthesis rather than by lowering the evidence bar for every query.
        if (candidate.LexicalScore is { } lexical && topLexical > 0
            && lexical >= topLexical * _options.MinLexicalOnlyLeaderRatio
            // ...but a lexical tie is not evidence of relevance when we also hold a semantic
            // reading that disagrees. "Local Group Policy Editor" ties on words for "edit a file"
            // solely because "Editor" contains the verb; its vector score sits below the floor
            // every other arm must clear. Exempting this clause from that floor made the floor
            // conditional on which arm found the candidate, which is not a property of the match.
            //
            // The test is whether the vector arm ranked the candidate and placed it low, not
            // whether a cosine exists for it. Every candidate now carries a cosine - see Fuse -
            // and reading those as disagreement would turn recording evidence into a penalty,
            // which measured as two lost cases including the "edit" query this comment is about.
            //
            // The exception is the noise band. A cosine at a few per cent of the leader is not an
            // unranked reading, it is the model reporting no relation at all, and a word the two
            // texts happen to share cannot outweigh that. "save a note" ties DxDiag with the
            // lexical leader because its documentation mentions saving text files, on a cosine of
            // three per cent. See RankingOptions.SemanticContradictionLeaderRatio.
            && (!candidate.VectorRanked
                || candidate.VectorScore is not { } tieVector
                || tieVector >= vectorFloor)
            && !ContradictedBySemantics(candidate, topVector))
            return true;

        var relativeScore = topScore <= 0
            || candidate.Score >= topScore * _options.MinRelativeScoreWithoutIndependentEvidence;

        if (candidate.VectorScore is not { } vectorScore)
            return false;

        if (!relativeScore)
            return false;

        if (!candidate.LexicalScore.HasValue)
        {
            return vectorScore >= _options.MinVectorOnlySurfaceScore
                && (topVector <= 0 || vectorScore >= topVector * _options.MinVectorOnlyLeaderRatio);
        }

        var relativeVector = topVector <= 0
            || vectorScore >= topVector * _options.MinHybridVectorLeaderRatio;

        // A cosine is treated as a semantic reading everywhere else in this method. For a third of
        // the index it is not one. Those entities have no description, no task and no harvested
        // text: ToEmbeddingText suppresses "Open Registry Editor." and "open registry editor" as
        // restating the name, so what remains to embed *is* the name. The resulting vector cannot
        // disagree with anything, and the model happily places "Registry Editor" at 78% of the
        // best cosine for "edit a file" - a query the corpus forbids it from answering - because
        // "Editor" and "edit" are the same word to it.
        //
        // Only multi-word queries are affected. A one-word query is a name being typed, where name
        // similarity is the right reading and the literal-match paths above handle it anyway.
        // Such an entity can still surface: it needs a word in common with the query, through the
        // lexical paths above or the corroboration below. What it may not do is arrive on a
        // similarity to its own name alone.
        var nameOnlySemantics = queryTermCount >= 2 && candidate.HasNameOnlySemantics;

        if (vectorScore >= vectorFloor && relativeVector && !nameOnlySemantics)
            return true;

        // Corroboration. Every floor above judges one arm against that arm's leader, which asks
        // whether this is the best answer by that measure. Two arms independently placing a
        // candidate at half the leader is a different fact from one arm doing so, and nothing
        // above can see it: "list processes" retrieved Task Manager at 57% of the best cosine and
        // 40% of the best BM25 and dropped it for missing 60% and 45% - each floor by a hair, both
        // of them, on the entity Windows ships for exactly that request.
        //
        // The evidence is real and correctly placed. Task Manager's indexed text says "names of
        // running processes" four times over; what beats it is Tasklist, whose entire summary is
        // "List running processes and services", and BM25 divides by field length. Raising the
        // weight of the field holding the longer text does not fix it - swept from 0.75 to 2.0,
        // the ratio never reached the floor and the corpus never moved - because the leader's
        // advantage is brevity, not weight.
        //
        // So this is stated as agreement rather than as a lower bar: the two ratios must clear a
        // product, so a candidate weak in one arm has to be correspondingly strong in the other.
        // A single-token FTS coincidence sitting at the lexical floor with noise-band cosine
        // yields around 0.13 and stays out; Task Manager yields 0.23.
        //
        // The cosine floor is kept here rather than letting the product carry the decision alone:
        // dropping it was measured at three thresholds and cost a corpus case at every one. It is
        // the query-relative floor, which removes the reason a result could appear and vanish
        // while a word was being typed - "list process", "list processe" and "list processes"
        // retrieve a byte-identical lexical list, porter stemming folding all three, yet scored
        // Task Manager at 0.248, 0.298 and 0.367 against what used to be a fixed 0.25 bar.
        // Corroboration requires the vector arm to have actually ranked the candidate, not merely
        // to hold a cosine for it. The clause's whole claim is that two arms found the same thing
        // independently; letting a below-floor cosine stand in for that turns it into a second,
        // weaker version of the hybrid floor above. Measured: without this restriction "search the
        // web" buries Microsoft Edge under msoasb, Get Started, Command Palette and IIS.
        if (topVector > 0 && topLexical > 0
            && candidate.VectorRanked
            && !nameOnlySemantics
            && vectorScore >= vectorFloor
            && vectorScore / topVector * (candidate.LexicalScore.Value / topLexical)
                >= _options.MinCorroboratedEvidenceProduct)
            return true;

        return false;
    }

    /// <summary>
    /// Whether the vector arm scored this candidate down in the noise band, which is the arm
    /// stating the candidate is unrelated rather than the arm having formed no view.
    /// See <see cref="RankingOptions.SemanticContradictionLeaderRatio"/>.
    /// </summary>
    private bool ContradictedBySemantics(Candidate candidate, double topVector) =>
        topVector > 0
        && candidate.VectorScore is { } vector
        && vector < topVector * _options.SemanticContradictionLeaderRatio;

    /// <summary>
    /// The bare command name an entity is launched by - "msinfo32" for System Information,
    /// "secpol" for Local Security Policy - or null when there is nothing a user would type.
    ///
    /// Only real program files qualify. URI launches are excluded because nobody types
    /// "ms-settings:signinoptions", and packaged apps launch by AUMID, which merely looks like a
    /// filename: treating "Microsoft.VisualStudioCode" as a path splits it at a non-existent
    /// extension and yields "Microsoft.VisualStudio", a string that belongs to a different product.
    /// Names already equal to the display name are dropped so the arm does not score them twice.
    /// </summary>
    private static string? ImageName(Entity entity)
    {
        if (entity.LaunchKind == LaunchKind.Uri || string.IsNullOrWhiteSpace(entity.LaunchTarget))
            return null;

        var target = entity.LaunchTarget;
        var extension = Path.GetExtension(target);
        if (!ExecutableExtensions.Contains(extension))
            return null;

        string stem;
        try
        {
            stem = Path.GetFileNameWithoutExtension(target);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(stem)
            || stem.Equals(entity.DisplayName, StringComparison.OrdinalIgnoreCase))
            return null;

        return stem;
    }

    private static readonly HashSet<string> ExecutableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".exe", ".msc", ".cpl", ".bat", ".cmd", ".com", ".ps1", ".msi" };

    /// <summary>
    /// Biases results toward what this user launches, blending total frequency with recency.
    /// Both components are capped so learned behaviour tilts close calls without overriding a
    /// clearly better match.
    /// </summary>
    private double UsageBoost(Snapshot snapshot, string entityId)
    {
        if (!snapshot.Usage.TryGetValue(entityId, out var stats) || stats.LaunchCount <= 0)
            return 0;

        // Logarithmic so the 1st launch matters far more than the 51st.
        var familiarity = Math.Min(1.0, Math.Log(1 + stats.LaunchCount) / Math.Log(50));
        var frequency = familiarity * _options.MaxFrequencyBoost;

        var recency = 0.0;
        if (stats.LastLaunchedAt is { } last)
        {
            var age = DateTimeOffset.UtcNow - last;
            if (age >= TimeSpan.Zero)
            {
                var halfLives = age.TotalSeconds / _options.RecencyHalfLife.TotalSeconds;

                // Recency is scaled by familiarity as well as by age. Opened once and never again
                // is not a habit, and it was being paid almost the full boost: a single launch of
                // the registry editor earlier the same day was worth a fifth of a result's whole
                // score, enough to put it above the text editor the semantic arm preferred by a
                // wide margin for "file edit". Being the last thing you opened only means something
                // among things you actually open.
                recency = _options.MaxRecencyBoost * familiarity * Math.Pow(0.5, halfLives);
            }
        }

        return frequency + recency;
    }

    private static string ExplainMatch(Candidate candidate)
    {
        if (candidate.LiteralReason is { } literal
            && candidate.LiteralContribution >= candidate.VectorContribution
            && candidate.LiteralContribution >= candidate.LexicalContribution)
        {
            return literal;
        }

        return (candidate.VectorScore.HasValue, candidate.LexicalScore.HasValue) switch
        {
            (true, true) => "hybrid semantic+lexical",
            (true, false) => "semantic",
            (false, true) => "lexical",
            _ when candidate.LiteralReason is { } literalOnly => literalOnly,
            _ => "usage",
        };
    }

    /// <summary>Shown when the query is empty, so the overlay opens on something useful.</summary>
    private IReadOnlyList<SearchHit> MostUsed(Snapshot snapshot, int limit)
    {
        return [.. snapshot.Entities
            .Select(e => new
            {
                Entity = e,
                Boost = UsageBoost(snapshot, e.Entity.Id),
            })
            .Where(x => x.Boost > 0)
            .OrderByDescending(x => x.Boost)
            .Take(limit)
            .Select(x => new SearchHit
            {
                Entity = x.Entity.Entity,
                Score = x.Boost,
                Summary = x.Entity.Profile?.Summary,
                MatchReason = "frequently used",
                Tasks = x.Entity.Profile?.IndexableTasks(x.Entity.Entity.DisplayName) ?? [],
                Category = x.Entity.Profile?.Category,
                Details = snapshot.DescriptiveDetails(x.Entity.Profile),
            })];
    }

    private static Candidate GetOrAdd(Snapshot snapshot, Dictionary<string, Candidate> candidates, int entityIndex)
    {
        var entity = snapshot.Entities[entityIndex];
        if (!candidates.TryGetValue(entity.Entity.Id, out var candidate))
        {
            candidate = new Candidate { Entity = entity };
            candidates[entity.Entity.Id] = candidate;
        }

        return candidate;
    }

    private static SearchHit ToHit(Snapshot snapshot, Candidate c) => new()
    {
        Entity = c.Entity.Entity,
        Score = c.Score,
        VectorScore = c.VectorScore,
        LexicalScore = c.LexicalScore,
        LexicalCoverage = c.LexicalCoverage,
        Summary = c.Entity.Profile?.Summary,
        MatchReason = c.MatchReason,
        Tasks = c.Entity.Profile?.IndexableTasks(c.Entity.Entity.DisplayName) ?? [],
        Category = c.Entity.Profile?.Category,
        Details = snapshot.DescriptiveDetails(c.Entity.Profile),
    };

    [DebuggerDisplay("{Entity.Entity.DisplayName} = {Score}")]
    private sealed class Candidate
    {
        public required IndexedEntity Entity { get; init; }
        public double Score { get; set; }
        public double? VectorScore { get; set; }

        /// <summary>
        /// True when the vector arm ranked this candidate, as opposed to merely holding a cosine
        /// for it. The distinction is only consulted by the lexical-tie clause in ShouldSurface,
        /// which asks whether a contradicting semantic reading exists rather than how strong the
        /// agreeing one is.
        /// </summary>
        public bool VectorRanked { get; set; }
        public double? LexicalScore { get; set; }

        /// <summary>
        /// How much of the query's distinctiveness this candidate's text accounts for, as an
        /// IDF-weighted fraction. One means every query term appears; a candidate matching only
        /// the query's common words scores near zero however high its BM25 climbs.
        /// </summary>
        public double LexicalCoverage { get; set; } = 1.0;
        public string? MatchReason { get; set; }
        public double VectorContribution { get; set; }
        public double LexicalContribution { get; set; }
        public double LiteralContribution { get; set; }
        public double UsageContribution { get; set; }
        public double LiteralStrength { get; set; }
        public string? LiteralReason { get; set; }

        /// <summary>
        /// True when the text handed to the embedding model carried nothing beyond the entity's
        /// own name and category, so its cosine measures name similarity rather than meaning.
        /// See the use in <c>ShouldSurface</c>.
        ///
        /// Tests exactly the fields <see cref="SynthesizedProfile.ToEmbeddingText"/> reads. The
        /// interface labels are deliberately not among them: they are a lexical column only, and
        /// counting them here would clear an entity of a charge about a vector they never entered.
        /// </summary>
        public bool HasNameOnlySemantics =>
            Entity.Profile is not { } profile
            || (profile.IndexableSummary(Entity.Entity.DisplayName) is null
                && profile.IndexableTasks(Entity.Entity.DisplayName).Count == 0
                && string.IsNullOrWhiteSpace(profile.Details));
    }

    /// <summary>
    /// How much of the query a lexical hit actually accounts for. The FTS query is an OR of the
    /// query's tokens, so a row matching one common word ranks alongside a row matching all of
    /// them: "annotate the screen during a demo" and "record my screen" both returned the Lock
    /// Screen settings page first, on the strength of the single word "screen", pushing ZoomIt and
    /// Steps Recorder down. Scaling the arm by coverage keeps such rows in play — they are still
    /// legitimate weak matches — without letting them lead.
    /// </summary>
    /// <summary>
    /// How much to trust a partial-name match for this query, from 1.0 (the query is not a word
    /// the corpus uses, so it can only be an abbreviated name) down to
    /// <see cref="RankingOptions.MinPartialNameCredibility"/> for a word that appears everywhere.
    ///
    /// Only single-token queries are affected. A multi-word query never earns a word-prefix boost
    /// in the first place, and treating it as a partially-typed name would be wrong anyway.
    /// </summary>
    private double PartialNameCredibility(Snapshot snapshot, string query)
    {
        var token = NameMatcher.Normalize(query);
        if (token.Length == 0 || token.Contains(' ', StringComparison.Ordinal) || snapshot.Entities.Length == 0)
            return 1.0;

        var frequency = snapshot.DocumentFrequency.GetValueOrDefault(token);
        if (frequency == 0)
            return 1.0;

        var share = (double)frequency / snapshot.Entities.Length;
        var decay = Math.Clamp(share / _options.CommonWordShare, 0.0, 1.0);
        return 1.0 - (decay * (1.0 - _options.MinPartialNameCredibility));
    }

    /// <summary>
    /// How much of the query an entity actually accounts for, measured in information rather than
    /// in words. Counting matched terms equally says a row matching only "list" answers half of
    /// "todo list", which is how a command that lists running processes came to outrank the
    /// to-do application: nearly every entity in the index can claim a word that common, while
    /// "todo" belongs to almost none. Weighting each term by how rare it is makes the distinctive
    /// half of a query the half that decides.
    /// </summary>
    private static double LexicalCoverageWeight(Snapshot snapshot, IndexedEntity entity, IReadOnlyList<string> queryTerms)
    {
        // A single-token query is a name or a prefix being typed, where the token *is* the whole
        // query and coverage carries no information.
        if (queryTerms.Count < 2)
            return 1.0;

        var haystack = BuildMatchText(entity);
        var available = 0.0;
        var matched = 0.0;

        foreach (var term in queryTerms)
        {
            var weight = InverseDocumentFrequency(snapshot, term);
            available += weight;
            if (haystack.Contains(term, StringComparison.Ordinal))
                matched += weight;
        }

        return available <= 0 ? 1.0 : Math.Max(matched / available, _minimumCoverageWeight);
    }

    /// <summary>
    /// A term no entity uses is the most telling one in the query, so an absent term is worth the
    /// most rather than nothing: it is usually a name the corpus spells differently.
    /// </summary>
    private static double InverseDocumentFrequency(Snapshot snapshot, string term)
    {
        var frequency = snapshot.DocumentFrequency.GetValueOrDefault(term);
        return Math.Log(1.0 + (snapshot.Entities.Length / (1.0 + frequency)));
    }

    private const double _minimumCoverageWeight = 0.2;

    /// <summary>
    /// The text coverage is measured against: name, summary, tasks and synonyms - the fields that
    /// state what an entity is *for*. Deliberately narrower than the FTS table, which also scores
    /// <see cref="SynthesizedProfile.Details"/> and <see cref="SynthesizedProfile.Features"/>.
    ///
    /// Including those was tried and measured worse: details drops MRR from 0.883 to 0.853, and
    /// details plus features to 0.862, both costing a case. Prose harvested from an article
    /// mentions a great many words in passing, and interface labels are a few hundred nouns per
    /// app, so admitting either makes coverage cheap to satisfy and it stops discriminating -
    /// which is the failure it was added to prevent, one level up.
    ///
    /// The cost is that coverage under-reports for an entity the lexical arm scored on those
    /// columns alone: Process Explorer reads 20% for "list services", the floor meaning nothing
    /// matched, though BM25 paid it for ".NET Services" among its harvested interface labels. That
    /// is the right trade while coverage only ever gates <see cref="RankingOptions.StrongLexicalScore"/>,
    /// which such an entity is nowhere near reaching.
    /// </summary>
    private static string BuildMatchText(IndexedEntity entity)
    {
        var builder = new StringBuilder();
        builder.Append(' ').Append(entity.Entity.DisplayName.ToLowerInvariant());
        if (entity.Profile is { } profile)
        {
            builder.Append(' ').Append(profile.Summary.ToLowerInvariant());
            foreach (var task in profile.Tasks)
                builder.Append(' ').Append(task.ToLowerInvariant());
            foreach (var synonym in profile.Synonyms)
                builder.Append(' ').Append(synonym.ToLowerInvariant());
        }

        return builder.Append(' ').ToString();
    }

    private static readonly HashSet<string> QueryStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "get",
        "how", "i", "in", "is", "it", "me", "my", "of", "on", "or", "that", "the", "then", "there",
        "this", "to", "up", "want", "was", "what", "when", "where", "which", "why", "will", "with",
        "you", "your", "during", "some", "any", "make", "see"
    };

    private static IReadOnlyList<string> ContentTerms(string query) =>
        QueryTermRegex.Matches(query)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length >= 3 && !QueryStopwords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .Take(12)
            .ToArray();

    private static readonly Regex QueryTermRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>
    /// An immutable view of the index. Every field is derived from a single read of the store, so
    /// the entity array, its lookups, and the vector matrix are always consistent with each other.
    /// </summary>
    private sealed record Snapshot(
        IndexedEntity[] Entities,
        Dictionary<string, int> IndexById,
        Dictionary<int, int> IndexByVectorOrdinal,
        float[] Vectors,
        int Dimensions,
        IReadOnlyDictionary<string, UsageStats> Usage)
    {
        public static Snapshot Empty { get; } =
            new([], [], [], [], 0, new Dictionary<string, UsageStats>());

        /// <summary>
        /// How many entities use each word anywhere in their indexed text. Built once per load;
        /// at a few thousand entities this is a few hundred thousand tokens and costs milliseconds.
        /// </summary>
        public Dictionary<string, int> DocumentFrequency { get; } = BuildDocumentFrequency(Entities);

        /// <summary>
        /// Detail prose that is shared by too many entities to be describing any of them. See
        /// <see cref="SharedDetailFilter"/>.
        /// </summary>
        private HashSet<string> SharedDetails { get; } = SharedDetailFilter.Build(Entities.Select(e => e.Profile));

        /// <summary>The profile's detail prose, or null when it is shared boilerplate.</summary>
        public string? DescriptiveDetails(SynthesizedProfile? profile) =>
            SharedDetailFilter.Describing(SharedDetails, profile);

        private static Dictionary<string, int> BuildDocumentFrequency(IndexedEntity[] entities)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entity in entities)
            {
                foreach (var word in BuildMatchText(entity).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal))
                    counts[word] = counts.GetValueOrDefault(word) + 1;
            }

            return counts;
        }
    }
}

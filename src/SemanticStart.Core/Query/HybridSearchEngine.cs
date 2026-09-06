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

        var vectorHits = await vectorTask.ConfigureAwait(false);
        var lexicalHits = await lexicalTask.ConfigureAwait(false);

        var fused = Fuse(snapshot, query, vectorHits, lexicalHits);

        return [.. PruneLowConfidence(fused)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Entity.Entity.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(ToHit)];
    }

    /// <summary>Embeds the query and scans the vector matrix. Returns ordinals paired with cosine similarity.</summary>
    private async Task<List<(int Ordinal, double Score)>> RunVectorArmAsync(
        Snapshot snapshot, string query, CancellationToken cancellationToken)
    {
        var results = new List<(int, double)>();

        if (snapshot.Vectors.Length == 0 || snapshot.Dimensions == 0)
            return results;

        var embedded = await _embeddings.EmbedAsync([query], cancellationToken).ConfigureAwait(false);
        if (embedded.Count == 0)
            return results;

        var q = embedded[0];
        var rows = snapshot.Vectors.Length / snapshot.Dimensions;

        // Both sides are L2-normalized by contract, so the dot product is the cosine similarity.
        for (var row = 0; row < rows; row++)
        {
            var span = snapshot.Vectors.AsSpan(row * snapshot.Dimensions, snapshot.Dimensions);
            var score = VectorMath.Dot(q, span);

            if (score >= _options.MinVectorScore)
                results.Add((row, score));
        }

        results.Sort(static (a, b) => b.Item2.CompareTo(a.Item2));

        if (results.Count > _options.CandidatesPerArm)
            results.RemoveRange(_options.CandidatesPerArm, results.Count - _options.CandidatesPerArm);

        return results;
    }

    private List<Candidate> Fuse(
        Snapshot snapshot,
        string query,
        List<(int Ordinal, double Score)> vectorHits,
        IReadOnlyList<(string EntityId, double Score)> lexicalHits)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        for (var rank = 0; rank < vectorHits.Count; rank++)
        {
            var (ordinal, score) = vectorHits[rank];
            if (!snapshot.IndexByVectorOrdinal.TryGetValue(ordinal, out var index))
                continue;

            var candidate = GetOrAdd(snapshot, candidates, index);
            candidate.VectorScore = score;
            candidate.VectorContribution = _options.VectorArmWeight / (_options.RrfK + rank + 1)
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

        for (var rank = 0; rank < lexicalHits.Count; rank++)
        {
            var (entityId, score) = lexicalHits[rank];
            if (lexicalLeader > 0 && score < lexicalLeader * _options.MinLexicalContributionRatio)
                continue;

            if (!snapshot.IndexById.TryGetValue(entityId, out var index))
                continue;

            var candidate = GetOrAdd(snapshot, candidates, index);
            candidate.LexicalScore = score;
            candidate.LexicalContribution = _options.LexicalArmWeight * LexicalCoverageWeight(snapshot, candidate.Entity, queryTerms)
                / (_options.RrfK + rank + 1);
            candidate.Score += candidate.LexicalContribution;
        }

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
            if (IsUnlistedCommand(candidate.Entity.Entity))
                candidate.Score *= _options.UnlistedCommandPenalty;

            candidate.UsageContribution = UsageBoost(snapshot, candidate.Entity.Entity.Id);
            candidate.Score += candidate.UsageContribution;
            candidate.MatchReason = ExplainMatch(candidate);
        }

        return [.. candidates.Values];
    }

    /// <summary>
    /// True for entities that only the command-alias collector found, meaning no app list, Start
    /// menu, or settings surface shows them. See <see cref="RankingOptions.UnlistedCommandPenalty"/>.
    /// </summary>
    private static bool IsUnlistedCommand(Entity entity) =>
        string.Equals(entity.Source, "command", StringComparison.Ordinal);

    /// <summary>
    /// Removes low-confidence tail results instead of padding the UI to the requested count.
    /// Final RRF scores are only ranks and are not comparable across queries, so the cutoff is
    /// based on the underlying evidence: literal name matches, strong BM25, absolute vector
    /// floors, and a relative score guard for weak-evidence tails after a strong leader.
    /// </summary>
    private List<Candidate> PruneLowConfidence(List<Candidate> candidates)
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

        return [.. candidates.Where(c => ShouldSurface(c, topScore, topVector, topLexical))];
    }

    private bool ShouldSurface(Candidate candidate, double topScore, double topVector, double topLexical)
    {
        if (candidate.LiteralStrength >= _options.MinLiteralSurfaceStrength)
            return true;

        if (candidate.LexicalScore >= _options.StrongLexicalScore)
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
            && (candidate.VectorScore is not { } tieVector
                || tieVector >= _options.MinHybridSurfaceVectorScore))
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

        return vectorScore >= _options.MinHybridSurfaceVectorScore && relativeVector;
    }

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
        var frequency = Math.Min(
            _options.MaxFrequencyBoost,
            Math.Log(1 + stats.LaunchCount) / Math.Log(50) * _options.MaxFrequencyBoost);

        var recency = 0.0;
        if (stats.LastLaunchedAt is { } last)
        {
            var age = DateTimeOffset.UtcNow - last;
            if (age >= TimeSpan.Zero)
            {
                var halfLives = age.TotalSeconds / _options.RecencyHalfLife.TotalSeconds;
                recency = _options.MaxRecencyBoost * Math.Pow(0.5, halfLives);
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
                Tasks = x.Entity.Profile?.Tasks ?? [],
                Category = x.Entity.Profile?.Category,
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

    private static SearchHit ToHit(Candidate c) => new()
    {
        Entity = c.Entity.Entity,
        Score = c.Score,
        VectorScore = c.VectorScore,
        LexicalScore = c.LexicalScore,
        Summary = c.Entity.Profile?.Summary,
        MatchReason = c.MatchReason,
        Tasks = c.Entity.Profile?.Tasks ?? [],
        Category = c.Entity.Profile?.Category,
    };

    [DebuggerDisplay("{Entity.Entity.DisplayName} = {Score}")]
    private sealed class Candidate
    {
        public required IndexedEntity Entity { get; init; }
        public double Score { get; set; }
        public double? VectorScore { get; set; }
        public double? LexicalScore { get; set; }
        public string? MatchReason { get; set; }
        public double VectorContribution { get; set; }
        public double LexicalContribution { get; set; }
        public double LiteralContribution { get; set; }
        public double UsageContribution { get; set; }
        public double LiteralStrength { get; set; }
        public string? LiteralReason { get; set; }
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

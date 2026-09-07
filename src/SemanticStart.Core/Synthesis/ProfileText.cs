using System.Text.RegularExpressions;

namespace SemanticStart.Core.Synthesis;

/// <summary>
/// Shared cleanup for the phrase lists that make up a profile's task and synonym fields.
/// </summary>
internal static class ProfileText
{
    /// <summary>
    /// Preference order for the document that supplies an entity's <c>Details</c>. It mirrors the
    /// order used to choose a summary, except that an encyclopedia lead is preferred over a vendor
    /// manifest here: a manifest is a marketing sentence, while the encyclopedia lead enumerates
    /// what the program can do, and this field exists to carry capability vocabulary.
    /// </summary>
    private static readonly string[] DetailProviders =
        ["wikipedia", "learn", "winget", "msix-manifest", "publisher-site", "cli-help", "local-docs"];

    /// <summary>
    /// Bounded capability prose for retrieval, drawn from whichever document describes the entity
    /// best. Enrichment was previously distilled to a single sentence before it reached the index,
    /// so the words that made a tool findable were harvested and then discarded: Wikipedia says
    /// Task Manager can "forcibly terminate processes", but the profile kept only "Manage running
    /// apps and view system performance" and no amount of ranking work could recover the rest.
    ///
    /// The text is capped rather than taken whole. Documentation pages trail off into navigation,
    /// legal notices, and unrelated links, and BM25 length normalisation means a long field of
    /// mostly-irrelevant text both dilutes real matches and invites incidental ones.
    ///
    /// Indexing the whole document instead of a capped extract was built and measured, and it is
    /// this field that makes it redundant. Documents were chunked into ~420-character passages,
    /// every passage embedded, and retrieval scored max-over-passages with a separate BM25 arm
    /// over passage text - the standard retrieval-augmented layout. Results, all on the 48-case
    /// corpus against a baseline of 45:
    ///
    ///   passages indexed as harvested .......... 43  (regressed)
    ///   passage vectors only, no BM25 arm ...... 43  (regressed)
    ///   prose-filtered, undiscounted ........... 44
    ///   prose-filtered, discounted 0.85 ........ 45  (identical failures to baseline)
    ///   + BM25 arm over passages, any weight ... 44
    ///
    /// Two things went wrong. Taking the best of a dozen passages is a multiple-comparisons
    /// problem: more documentation means more chances at one spuriously close vector, so
    /// "change my password" ranked 1Password first - an article about passwords is topically
    /// adjacent to the query, while the setting that performs the action is not. And most
    /// harvested text is not prose at all but CLI "/?" output and columns of ms-settings: URIs,
    /// which are long, token-dense, and match common words by accident.
    ///
    /// Discounting passages against the profile vector fixed the regression but produced no gain:
    /// top-3 results were byte-identical to the baseline across the corpus and ten unseen queries.
    /// The reason is visible in the data - an entity's extra passages are usually the continuation
    /// of the very article whose opening is already in this field, and an article's lead is the
    /// part that names capabilities. Chunking buys the tail of a document, which is history,
    /// provenance, and links. The ceiling here is source coverage, not extraction: "change what
    /// happens when I close the lid" still fails because no document on the machine says "lid".
    ///
    /// Raising the cap was tried, to 2500 characters, when the Wikipedia enricher was briefly
    /// returning article bodies. It cost two corpus cases and 0.05 MRR and was reverted along with
    /// the bodies; see WikipediaEnricher for the measurements. Length is not free here even at a
    /// low BM25 weight, because a longer column changes which entities MATCH at all.
    /// </summary>
    public static string? Details(IReadOnlyList<Model.EnrichmentDocument> documents, int maxCharacters = 600)
    {
        if (documents is null || documents.Count == 0)
            return null;

        foreach (var provider in DetailProviders)
        {
            var text = documents
                .FirstOrDefault(d => d.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))?
                .Text;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var clean = Regex.Replace(Enrichment.EnrichmentTextNormalizer.ToPlainText(text), @"\s+", " ").Trim();
            clean = WithoutListings(clean);
            if (clean.Length < 40 || !IsProse(clean))
                continue;

            return clean.Length <= maxCharacters ? clean : TrimToSentence(clean, maxCharacters);
        }

        return null;
    }

    /// <summary>
    /// The interface labels harvested from a program's own menu and dialog resources, passed
    /// through unchanged apart from the enricher's own filtering.
    ///
    /// Kept out of <see cref="Details"/> deliberately. Details is chosen by <see cref="IsProse"/>,
    /// which exists to reject columns of labels, and a caption list is exactly that - it would be
    /// rejected, and were the filter relaxed to admit it the filter would stop doing its job.
    /// Giving the captions their own column also lets them be weighted for what they are: broad
    /// recall of capability vocabulary, not evidence strong enough to outrank a task phrase.
    ///
    /// The list is reduced to each word's first appearance. A menu names one capability many times
    /// over - Registry Editor offers "Edit String", "Edit Binary Value", "Edit DWORD (32-bit)
    /// Value" and "Edit Multi-String" - and BM25 reads that repetition as four times the evidence.
    /// It ranked second for "edit a file", a query the corpus explicitly forbids it from answering,
    /// on the strength of a word its interface happens to repeat. What the field should assert is
    /// that a capability is present, not how many menu items mention it.
    ///
    /// Done word by word rather than by dropping whole captions. Discarding near-duplicate phrases
    /// was tried first, reusing <see cref="Distinctive"/>, and it removed "Physical Memory Usage"
    /// as a near-duplicate of "Physical Memory History" - taking the query's own word with it.
    /// That rule is right for synthesized task lists, which restate a single idea, and wrong for a
    /// menu, where two labels sharing two words routinely name two different features.
    ///
    /// Words the name or summary already carry are dropped. The field exists to say what the other
    /// fields cannot; a word they already assert is being counted twice, once in a field weighted
    /// for it and once here. That double counting is not harmless. The corpus records that
    /// "edit a file" must not return Registry Editor, and the reason it did was that "-editor"
    /// yields the verb "edit" - the object of the query has to count for something. Regedit's menu
    /// then reintroduces a literal "Edit" and hands back the match the ranker had learned to
    /// refuse. Comparison is by prefix rather than by equality, with a four-character floor, since
    /// it is precisely the morphological variants - "Editor"/"Edit", "Processes"/"Process" - that
    /// re-assert the name. Process Explorer keeps "Memory" and "Usage", which nothing else it has
    /// says.
    /// </summary>
    public static string? Features(
        IReadOnlyList<Model.EnrichmentDocument> documents,
        string? displayName = null,
        string? summary = null)
    {
        var text = documents?
            .FirstOrDefault(d => d.Provider.Equals("ui-resources", StringComparison.OrdinalIgnoreCase))?
            .Text;

        if (string.IsNullOrWhiteSpace(text))
            return null;

        var separator = text.IndexOf(':', StringComparison.Ordinal);
        var body = separator >= 0 ? text[(separator + 1)..] : text;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        var known = Vocabulary(displayName, summary);

        foreach (var word in Regex.Split(body, @"[^\p{L}\p{N}]+"))
        {
            if (word.Length < 2 || !seen.Add(word) || Restates(word, known))
                continue;

            kept.Add(word);
            if (kept.Count >= MaxDistinctFeatureWords)
                break;
        }

        return kept.Count == 0 ? null : "Interface labels: " + string.Join(" ", kept) + ".";
    }

    private static IReadOnlyCollection<string> Vocabulary(string? displayName, string? summary)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in new[] { displayName, summary })
        {
            if (string.IsNullOrWhiteSpace(source))
                continue;

            foreach (var word in Regex.Split(source, @"[^\p{L}\p{N}]+"))
            {
                if (word.Length >= 2)
                    words.Add(word);
            }
        }

        return words;
    }

    /// <summary>
    /// True when the caption word and a word the entity already carries are the same word in
    /// different form. Shorter than the floor, a prefix match is meaningless - "on" prefixes
    /// "online" - so below it only equality counts.
    /// </summary>
    private static bool Restates(string word, IReadOnlyCollection<string> known)
    {
        foreach (var other in known)
        {
            if (word.Equals(other, StringComparison.OrdinalIgnoreCase))
                return true;

            var shorter = word.Length <= other.Length ? word : other;
            var longer = word.Length <= other.Length ? other : word;

            if (shorter.Length >= MinStemLength &&
                longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private const int MinStemLength = 4;

    /// <summary>
    /// Generous, because unlike the task list this is not a curated set and a large application
    /// genuinely does many distinct things; the point of the cap is only to keep one program from
    /// dominating the lexical index.
    /// </summary>
    private const int MaxDistinctFeatureWords = 200;

    /// <summary>
    /// Drops the sentence-shaped runs that are actually columns of labels, keeping the prose
    /// around them. Applied before the length cap because these listings sit at the top of a
    /// documentation page, so a capped extract is otherwise made almost entirely of them.
    /// </summary>
    private static string WithoutListings(string text)
    {
        var kept = text
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !IsListing(segment))
            .ToArray();

        return kept.Length == 0 ? string.Empty : string.Join(". ", kept) + ".";
    }

    /// <summary>
    /// Rejects harvested text that is a machine-readable listing rather than something written to
    /// be read. Documentation pages routinely carry reference tables - one Windows optional feature
    /// came back with a column of "ms-settings:" URIs as its entire description - and indexing that
    /// contributes no vocabulary a user would ever type while adding tokens that match at random.
    ///
    /// Public because every consumer of harvested text needs the same judgement. Describing Startup
    /// Apps from the Learn page that tabulates every settings URI otherwise yields "download maps"
    /// and "set up a kiosk", because those are the neighbouring rows.
    /// </summary>
    public static bool IsProse(string text)
    {
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 8)
            return false;

        var identifierLike = tokens.Count(t =>
            t.Contains(':', StringComparison.Ordinal)
            || t.Contains('_', StringComparison.Ordinal)
            || t.Contains('\\', StringComparison.Ordinal)
            || (t.Length > 14 && t.All(char.IsLower)));

        if ((double)identifierLike / tokens.Length >= 0.15)
            return false;

        // A listing is also recognisable by what it lacks. Once the URIs are scrubbed out of that
        // same reference table what remains is "Default browser settings Manage optional features
        // Offline Maps Storage Sense" - a column of page titles with no sentence around them.
        // Function words are the cheapest available evidence that someone wrote this to be read,
        // and they are the one part of English vocabulary that carries no topic, so requiring a
        // few of them cannot bias the index toward any subject.
        var functionWords = tokens.Count(t => FunctionWords.Contains(t.Trim(TokenPunctuation)));
        return (double)functionWords / tokens.Length >= 0.06;
    }

    private static readonly char[] TokenPunctuation = ['.', ',', ';', ':', '(', ')', '"', '\'', '!', '?'];

    /// <summary>
    /// True when a single line is a run of labels rather than a sentence.
    ///
    /// <see cref="IsProse"/> judges a whole document, which lets a page that opens with a
    /// reference table and then explains itself pass as a unit - and the opening is exactly the
    /// part a summary takes. The Learn page that tabulates every settings URI did this to 56
    /// Windows features at once, each of them described as "Default browser settings Manage
    /// optional features Offline Maps Startup apps Video playback". Every one of those is a real
    /// page title, none of them is about the feature being described, and no scrubbing of URIs
    /// helps because the URIs are in the neighbouring column.
    ///
    /// Only long lines are judged. A genuine short description - "Browse the web." - has no room
    /// for function words and needs none, while ten or more words strung together with none of
    /// them is not something anybody wrote as a sentence.
    /// </summary>
    public static bool IsListing(string line)
    {
        var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 10)
            return false;

        var functionWords = tokens.Count(t => FunctionWords.Contains(t.Trim(TokenPunctuation)));
        return (double)functionWords / tokens.Length < 0.06;
    }

    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "of", "to", "in", "on", "for", "with", "by", "from", "as", "at", "into",
        "is", "are", "was", "were", "be", "been", "it", "its", "this", "that", "these", "those",
        "and", "or", "but", "if", "when", "which", "who", "whose", "you", "your", "their", "them",
        "can", "will", "would", "should", "may", "not", "than", "then", "there", "such", "each",
        "any", "all", "more", "most", "other", "between", "about", "over", "up", "out",
    };

    /// <summary>
    /// Cuts at the last sentence boundary before the limit so the field never ends mid-clause.
    ///
    /// Both alternatives to a plain leading extract were tried and measured worse. Raising the cap
    /// to 1000 characters pulled in provenance and history ("previously known as", "acquired by
    /// Microsoft"), which are dense with product and company names and match queries having nothing
    /// to do with the tool. Selecting only sentences carrying a capability cue ("can", "allows",
    /// "used to") scored worse still: the cues are common enough to admit boilerplate while
    /// rejecting plainly-worded definitional sentences that were doing real work. The lead of a
    /// well-written article is already the highest-signal part of it, so the simple cut wins.
    /// </summary>
    private static string TrimToSentence(string text, int maxCharacters)
    {
        var window = text[..maxCharacters];
        var lastStop = window.LastIndexOf('.');
        return lastStop > maxCharacters / 2 ? window[..(lastStop + 1)] : window.TrimEnd() + "...";
    }

    /// <summary>
    /// Trims, de-duplicates, and drops near-identical phrases, keeping at most <paramref name="max"/>.
    ///
    /// Generative synthesis reliably produces lists that restate one idea many ways: Power Options
    /// came back with "Change power plan", "Set power settings", "Adjust power options", "Manage
    /// power settings", "Optimize power usage", "Configure power settings". Exact-match de-duplication
    /// keeps all six. That matters because the tasks field carries the highest BM25 weight, so a
    /// six-fold repetition of "power" turned every such entity into a strong lexical match for any
    /// query containing a word it happened to repeat, and unrelated results flooded in: enabling
    /// generative synthesis scored *worse* than the heuristic fallback despite writing visibly
    /// better summaries.
    ///
    /// Two phrases are treated as the same when their content words match, ignoring word order and
    /// the interchangeable verbs vendors and models use for the same action. Distinct phrasings of
    /// genuinely different tasks are kept, since those are the vocabulary bridge the field exists
    /// to provide.
    /// </summary>
    public static IReadOnlyList<string> Distinctive(IEnumerable<string>? values, int max)
    {
        if (values is null)
            return [];

        var kept = new List<string>();
        var keptKeys = new List<HashSet<string>>();

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var phrase = Regex.Replace(raw.Trim(), @"\s+", " ");
            if (phrase.Length == 0)
                continue;

            var key = ContentWords(phrase);
            if (key.Count == 0)
                continue;

            if (keptKeys.Any(existing => IsNearDuplicate(existing, key)))
                continue;

            kept.Add(phrase);
            keptKeys.Add(key);
            if (kept.Count >= max)
                break;
        }

        return kept;
    }

    private static bool IsNearDuplicate(HashSet<string> a, HashSet<string> b)
    {
        var overlap = a.Count < b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        if (overlap == 0)
            return false;

        // Subset means the phrase adds no content word the kept phrase lacks.
        if (overlap == a.Count || overlap == b.Count)
            return true;

        var union = a.Count + b.Count - overlap;
        return union > 0 && (double)overlap / union >= 0.6;
    }

    private static HashSet<string> ContentWords(string phrase)
    {
        var words = Regex.Split(phrase.ToLowerInvariant(), @"\W+")
            .Where(w => w.Length > 1 && !Filler.Contains(w))
            .Select(Canonical);

        return [.. words];
    }

    /// <summary>
    /// Collapses verbs that name the same action so "change power settings" and "adjust power
    /// options" resolve to one entry rather than two.
    /// </summary>
    private static string Canonical(string word)
        => VerbGroups.TryGetValue(word, out var canonical) ? canonical : word;

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "for", "of", "to", "in", "on", "with", "by", "from",
        "is", "are", "your", "my", "this", "that", "it", "as", "at", "into", "up", "new"
    };

    private static readonly Dictionary<string, string> VerbGroups = new(StringComparer.Ordinal)
    {
        ["change"] = "set", ["adjust"] = "set", ["configure"] = "set", ["modify"] = "set",
        ["edit"] = "set", ["update"] = "set", ["customize"] = "set", ["tweak"] = "set",
        ["manage"] = "set", ["control"] = "set", ["optimize"] = "set", ["tune"] = "set",
        ["settings"] = "setting", ["options"] = "setting", ["preferences"] = "setting",
        ["view"] = "see", ["show"] = "see", ["display"] = "see", ["inspect"] = "see",
        ["check"] = "see", ["monitor"] = "see", ["watch"] = "see", ["analyze"] = "see",
        ["open"] = "open", ["launch"] = "open", ["start"] = "open", ["run"] = "open",
        ["remove"] = "delete", ["erase"] = "delete", ["clear"] = "delete", ["clean"] = "delete",
        ["make"] = "create", ["build"] = "create", ["add"] = "create", ["generate"] = "create",
        ["apps"] = "app", ["applications"] = "app", ["application"] = "app", ["programs"] = "app",
        ["program"] = "app", ["processes"] = "process", ["files"] = "file", ["devices"] = "device",
        ["drives"] = "drive", ["disks"] = "disk", ["tasks"] = "task", ["usage"] = "use",
    };
}

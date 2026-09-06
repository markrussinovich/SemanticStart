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
            if (clean.Length < 40 || !IsProse(clean))
                continue;

            return clean.Length <= maxCharacters ? clean : TrimToSentence(clean, maxCharacters);
        }

        return null;
    }

    /// <summary>
    /// Rejects harvested text that is a machine-readable listing rather than something written to
    /// be read. Documentation pages routinely carry reference tables - one Windows optional feature
    /// came back with a column of "ms-settings:" URIs as its entire description - and indexing that
    /// contributes no vocabulary a user would ever type while adding tokens that match at random.
    ///
    /// Public because the same judgement is needed before handing a document to a language model.
    /// A small model asked to describe Startup Apps from the Learn page that tabulates every
    /// settings URI dutifully reports that it can "download maps" and "set up a kiosk", because
    /// those are the neighbouring rows. Filtering the input is far more effective than instructing
    /// the model to ignore it.
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

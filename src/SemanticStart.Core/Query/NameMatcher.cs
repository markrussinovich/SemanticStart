using System.Text;

namespace SemanticStart.Core.Query;

/// <summary>
/// Lexical scoring signals computed directly against the display name. These run over the whole
/// in-memory entity list on every keystroke, which is affordable at this index size and is what
/// guarantees that typing a literal name behaves at least as well as the stock Start menu.
/// </summary>
public static class NameMatcher
{
    /// <summary>
    /// Returns a boost for how well <paramref name="query"/> matches <paramref name="displayName"/>,
    /// along with a human-readable reason used by the relevance harness. Returns 0 when there is
    /// no literal relationship at all, leaving the entity to be judged on semantics alone.
    /// </summary>
    public static (double Boost, string? Reason) Score(string query, string displayName, RankingOptions options)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(displayName))
            return (0, null);

        var q = Normalize(query);
        var name = Normalize(displayName);

        if (q.Length == 0)
            return (0, null);

        if (string.Equals(q, name, StringComparison.Ordinal))
            return (options.ExactMatchBoost, "exact name");

        if (name.StartsWith(q, StringComparison.Ordinal))
            return (options.PrefixBoost, "name prefix");

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            if (word.StartsWith(q, StringComparison.Ordinal))
                return (options.WordPrefixBoost, "word prefix");
        }

        if (words.Length > 1)
        {
            var acronym = string.Concat(words.Select(w => w[0]));
            if (string.Equals(acronym, q, StringComparison.Ordinal))
                return (options.AcronymBoost, "acronym");
        }

        // Subsequence matching lets "vsc" reach "Visual Studio Code" and "devmgr" reach
        // "Device Manager". Scored well below a prefix so it only breaks ties.
        if (IsSubsequence(q, name))
            return (options.WordPrefixBoost * 0.4, "subsequence");

        return (0, null);
    }

    /// <summary>Lowercases, strips punctuation, and collapses whitespace so comparisons are stable.</summary>
    public static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace && sb.Length > 0)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        var i = 0;
        foreach (var ch in haystack)
        {
            if (i < needle.Length && ch == needle[i])
                i++;
        }

        return i == needle.Length;
    }
}

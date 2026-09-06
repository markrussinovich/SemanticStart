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

            // Typing does not stop at the initials. Having typed "vsc" for Visual Studio Code the
            // user continues into the last word - "vsco", "vscod" - and each of those keystrokes
            // used to make the target disappear, because the pure-initials test no longer matched
            // and the subsequence fallback below scores 0.24 against a 0.6 surfacing bar, so it can
            // never surface anything on its own. Treating initials-plus-continuation as the acronym
            // it is keeps the entity through the whole word rather than only at one prefix length.
            if (IsInitialsPrefix(q, words))
                return (options.AcronymBoost, "acronym prefix");
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

    /// <summary>
    /// True when the query can be split into consecutive non-empty chunks, each a prefix of a
    /// later word of the name, with the final chunk allowed to run into the middle of its word.
    /// "vsco" splits as v|s|co across "visual studio code".
    ///
    /// Words may be skipped, because vendors prepend noise a user does not type: "Microsoft Visual
    /// Studio Code" must still answer to "vsc". Requiring every chunk to be a genuine word prefix
    /// is what keeps that from becoming a plain subsequence match - "vsco" does not reach "x86
    /// Native Tools Command Prompt for VS 2022", since nothing after the "vs" word begins with c.
    /// </summary>
    private static bool IsInitialsPrefix(string query, string[] words)
    {
        return Match(0, 0);

        bool Match(int queryIndex, int wordIndex)
        {
            if (queryIndex == query.Length)
                return true;

            for (var w = wordIndex; w < words.Length; w++)
            {
                var word = words[w];
                var maxChunk = Math.Min(word.Length, query.Length - queryIndex);

                // Longest chunk first: the common case consumes as much of the word as was typed.
                for (var length = maxChunk; length >= 1; length--)
                {
                    if (string.CompareOrdinal(query, queryIndex, word, 0, length) != 0)
                        continue;

                    if (Match(queryIndex + length, w + 1))
                        return true;
                }
            }

            return false;
        }
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

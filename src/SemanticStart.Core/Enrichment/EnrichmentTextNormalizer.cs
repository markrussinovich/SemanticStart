using System.Net;
using System.Text.RegularExpressions;

namespace SemanticStart.Core.Enrichment;

/// <summary>
/// Normalizes enrichment payloads before synthesis because embeddings are damaged by chrome,
/// markup, script, and other transport artifacts that are not user intent vocabulary.
/// </summary>
public static class EnrichmentTextNormalizer
{
    private const int MaxNormalizedChars = 12 * 1024;

    public static string ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value!;
        if (LooksLikeHtml(text))
            text = StripHtml(text);
        else
            text = WebUtility.HtmlDecode(text);

        text = Regex.Replace(text, @"(?im)^\s*(html|xml)\s*:\s*", " ");
        text = Regex.Replace(text, @"\b(Skip to main content|Summarize this article for me|In this article|Was this page helpful\??|Feedback|Additional resources|Previous Versions)\b", " ", RegexOptions.IgnoreCase);
        text = ScrubNonDescriptive(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > MaxNormalizedChars ? text[..MaxNormalizedChars] : text;
    }

    /// <summary>
    /// Removes tokens that are addresses rather than descriptions: URLs, protocol URIs, file and
    /// registry paths, GUIDs, and hashes.
    ///
    /// Documentation pages are full of these, and they are pure cost in an index. They contribute
    /// no word a user would ever type, and because the tokenizer splits them into fragments they
    /// match at random - "ms-settings:signinoptions" becomes evidence for the query "options". The
    /// Windows settings pages were the worst affected: the Learn article that documents them is a
    /// two-column table of names and URIs, so entities were being described by a column of
    /// addresses.
    ///
    /// Scrubbing tokens rather than rejecting whole documents is what makes this worth doing. The
    /// same table also carries the only plain-English statement of what several settings pages are
    /// for, and a document-level filter throws that away with the noise.
    /// </summary>
    public static string ScrubNonDescriptive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value!;

        text = Regex.Replace(text, @"\b[a-zA-Z][a-zA-Z0-9+.\-]*://\S+", " ");
        text = Regex.Replace(text, @"\b[\w.+\-]+@[\w\-]+\.[\w.\-]+\b", " ");
        text = Regex.Replace(text, @"\{?\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b\}?", " ");
        text = Regex.Replace(text, @"(?i)\bHKEY_[A-Z_]+(?:\\[^\s]+)?", " ");
        text = Regex.Replace(text, @"(?i)\b(?:HKLM|HKCU|HKCR|HKU)\\[^\s]+", " ");
        text = Regex.Replace(text, @"\b[a-zA-Z]:\\[^\s]*", " ");
        text = Regex.Replace(text, @"%[A-Za-z_][A-Za-z0-9_()]*%[^\s]*", " ");
        text = Regex.Replace(text, @"\b[0-9a-fA-F]{16,}\b", " ");

        // Protocol URIs such as "ms-settings:signinoptions" or "shell:AppsFolder". The scheme must
        // be lowercase and the remainder unbroken, which is what keeps ordinary prose punctuation
        // ("Note: the following", "3:30", "Chapter 2: Setup") out of the pattern.
        text = Regex.Replace(text, @"(?<![\w-])[a-z][a-z0-9+.\-]{1,20}:[^\s]{2,}", " ");

        // Whatever is left behind - empty brackets, stranded separators, doubled stops.
        text = Regex.Replace(text, @"\(\s*\)|\[\s*\]|\{\s*\}", " ");
        text = Regex.Replace(text, @"(?:\s*[|·•>/-]\s*){2,}", " ");
        text = Regex.Replace(text, @"\s+([.,;:])", "$1");
        text = Regex.Replace(text, @"(?:\.\s*){2,}", ". ");

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string StripHtml(string html)
    {
        var text = Regex.Replace(html, @"(?is)<!--.*?-->", " ");
        text = Regex.Replace(text, @"(?is)<(script|style|svg|noscript|template|head|iframe|canvas)[^>]*>.*?</\1>", " ");
        text = Regex.Replace(text, @"(?is)<!doctype[^>]*>", " ");
        text = Regex.Replace(text, @"(?i)</?(p|div|section|article|main|header|footer|nav|aside|table|tr|td|th|li|ul|ol|br|h[1-6])\b[^>]*>", ". ");
        text = Regex.Replace(text, @"(?is)<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string StripMarkdown(string text)
    {
        text = Regex.Replace(text, @"```[\s\S]*?```", " ");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"!\[[^\]]*\]\([^\)]+\)", " ");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        text = Regex.Replace(text, @"^[#>*\-\s]+", "", RegexOptions.Multiline);
        return ToPlainText(text);
    }

    public static bool IsLikelyMarkupLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var value = line.Trim();
        if (value.StartsWith("html:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("xml:", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Regex.IsMatch(value, @"(?i)<\s*(?:!doctype|/?html|/?head|/?body|/?script|/?style|/?div|/?span|/?p|/?a|/?meta|/?link)\b"))
            return true;
        if (Regex.IsMatch(value, @"&(?:lt|gt|nbsp|quot|apos|amp);", RegexOptions.IgnoreCase) && Regex.IsMatch(WebUtility.HtmlDecode(value), @"<[^>]+>"))
            return true;
        if (Regex.IsMatch(value, @"(?i)^\s*(?:\.|#|body\b|html\b)[\w\s.#:-]*\{.*\}\s*$"))
            return true;
        return false;
    }

    public static string? ExtractMetaDescription(string html)
    {
        foreach (Match match in Regex.Matches(html, @"<meta\b(?<attrs>[^>]+)>", RegexOptions.IgnoreCase))
        {
            var attrs = match.Groups["attrs"].Value;
            var name = ReadAttribute(attrs, "name") ?? ReadAttribute(attrs, "property");
            if (name is null)
                continue;

            if (!name.Equals("description", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("og:description", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("twitter:description", StringComparison.OrdinalIgnoreCase))
                continue;

            var content = ReadAttribute(attrs, "content");
            if (!string.IsNullOrWhiteSpace(content))
                return ToPlainText(content);
        }

        return null;
    }

    private static bool LooksLikeHtml(string text)
        => text.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
           || text.Contains("<html", StringComparison.OrdinalIgnoreCase)
           || text.Contains("</", StringComparison.Ordinal)
           || Regex.IsMatch(text, @"(?is)<(?:p|div|span|body|head|script|style|meta|title|h[1-6]|br|ul|ol|li|table)\b");

    private static string? ReadAttribute(string attrs, string name)
    {
        var match = Regex.Match(attrs, $@"(?:^|\s){Regex.Escape(name)}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value) : null;
    }
}

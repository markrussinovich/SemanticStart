using System.Net;
using System.Text.RegularExpressions;

namespace SemanticStart.Core.Enrichment;

/// <summary>
/// Normalizes enrichment payloads before synthesis because embeddings are damaged by chrome,
/// markup, script, and other transport artifacts that are not user intent vocabulary.
/// </summary>
internal static class EnrichmentTextNormalizer
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
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > MaxNormalizedChars ? text[..MaxNormalizedChars] : text;
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

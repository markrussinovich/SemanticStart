using System.Text.Json;
using System.Text.RegularExpressions;

namespace SemanticStart.Core.Enrichment;

/// <summary>
/// An authoritative name-to-URL catalogue of documented Windows tooling, built from the
/// machine-readable tables of contents that Microsoft Learn publishes for each docset.
///
/// This exists because Learn's search API cannot find the pages that matter most here. Querying it
/// for "Task Manager", or even for the exact title "Troubleshoot processes by using Task Manager",
/// returns Intune and Azure hub pages and never the article itself; the same is true for most
/// Sysinternals utilities. Those are precisely the tools a user cannot name and therefore the ones
/// semantic search has to describe well, so leaving discovery to a search engine that cannot reach
/// them left Task Manager indexed with nothing but "Manage running apps" and no mention of ending a
/// process at all.
///
/// A table of contents sidesteps search entirely: it is the publisher's own list of every article
/// in an area, with the title it chose for each. Matching an entity against it is a local string
/// comparison against authoritative data rather than a guess at what a ranking function will
/// return. Each TOC is a single cached request and covers hundreds of tools.
/// </summary>
internal sealed class LearnTocCatalog
{
    /// <summary>
    /// Docsets covering software that ships with Windows or is published by Microsoft for it.
    /// These are areas, not products: a new Sysinternals utility or a new built-in command appears
    /// in its docset's TOC automatically and needs no change here.
    /// </summary>
    private static readonly string[] TocRoots =
    [
        "https://learn.microsoft.com/en-us/sysinternals/toc.json",
        "https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/toc.json",
        "https://learn.microsoft.com/en-us/troubleshoot/windows-server/toc.json",
        "https://learn.microsoft.com/en-us/troubleshoot/windows-client/toc.json",
        "https://learn.microsoft.com/en-us/windows/client-management/toc.json",
    ];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _byName;

    public LearnTocCatalog(HttpClient http) => _http = http;

    /// <summary>
    /// Returns the documentation URL whose TOC title names this entity, or null. Matching is exact
    /// after normalisation, so it cannot drift onto a different product.
    /// </summary>
    public async Task<string?> FindAsync(string displayName, IEnumerable<string> aliases, CancellationToken cancellationToken)
    {
        var map = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (map.Count == 0)
            return null;

        foreach (var candidate in new[] { displayName }.Concat(aliases))
        {
            var key = Normalize(candidate);
            if (key.Length >= 4 && map.TryGetValue(key, out var url))
                return url;
        }

        return null;
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_byName is not null)
            return _byName;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_byName is not null)
                return _byName;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var root in TocRoots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var json = await CachedHttp.GetStringAsync(_http, root, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var baseUri = new Uri(root);
                    Walk(doc.RootElement, baseUri, map);
                }
                catch (JsonException) { }
            }

            _byName = map;
            return map;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Walk(JsonElement element, Uri baseUri, Dictionary<string, string> map)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, baseUri, map);
                return;

            case JsonValueKind.Object:
                if (element.TryGetProperty("toc_title", out var titleProperty)
                    && element.TryGetProperty("href", out var hrefProperty)
                    && titleProperty.ValueKind == JsonValueKind.String
                    && hrefProperty.ValueKind == JsonValueKind.String)
                {
                    Record(titleProperty.GetString(), hrefProperty.GetString(), baseUri, map);
                }

                foreach (var property in element.EnumerateObject())
                    Walk(property.Value, baseUri, map);
                return;
        }
    }

    private static void Record(string? title, string? href, Uri baseUri, Dictionary<string, string> map)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href))
            return;

        // Anchors and absolute links to other sites are not article pages for this entity.
        if (href.StartsWith('#') || href.Contains("://", StringComparison.Ordinal))
            return;

        if (!Uri.TryCreate(baseUri, href, out var absolute) || !absolute.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
            return;

        foreach (var key in TitleKeys(title))
        {
            if (key.Length < 4)
                continue;

            // First writer wins. Roots are ordered most specific first, so a product's own docset
            // takes precedence over a troubleshooting article that mentions it.
            map.TryAdd(key, absolute.GetLeftPart(UriPartial.Path));
        }
    }

    /// <summary>
    /// Yields the names a TOC title can legitimately be taken to be about. Publishers title
    /// articles both bare ("AccessChk") and as a phrase ("Using Task Manager"), so a leading
    /// framing word is stripped to expose the subject. Titles that describe a *problem* rather than
    /// the tool - "Fail to open Task Manager", "Task Manager displays incorrect memory information"
    /// - produce no key and are ignored, which is why the match must be exact rather than a
    /// containment test.
    /// </summary>
    private static IEnumerable<string> TitleKeys(string title)
    {
        var clean = Regex.Replace(title, @"\s+", " ").Trim();
        yield return Normalize(clean);

        var stripped = Regex.Replace(
            clean,
            @"^(using|use|about|what is|what's|overview of|overview|introduction to|intro to|working with|get started with|getting started with|the)\s+",
            string.Empty,
            RegexOptions.IgnoreCase);

        if (!stripped.Equals(clean, StringComparison.OrdinalIgnoreCase))
            yield return Normalize(stripped);
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

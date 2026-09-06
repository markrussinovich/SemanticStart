using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Enrichment;

/// <summary>
/// Harvests the lead section of an entity's Wikipedia article.
///
/// This source fills a gap the vendor sources structurally cannot. A publisher describes a product
/// in the language of the product; an encyclopedia describes it in the language of someone trying
/// to explain what it is for, which is much closer to how a user phrases a query. Microsoft's own
/// page for Task Manager opens "Describes the features of Task Manager and provides examples of how
/// to apply those features when troubleshooting" - prose about the article, contributing no task
/// vocabulary at all - while Wikipedia's opens by saying it can "set process priorities, processor
/// affinity, start and stop services, and forcibly terminate processes". The words a user actually
/// types ("kill a process") appear only in the latter.
///
/// It is also uniformly available: nearly every notable application has an article, whereas
/// Microsoft Learn covers only Microsoft's own software, so this is the one online source that
/// treats third-party and inbox tools alike.
/// </summary>
public sealed class WikipediaEnricher : IEnricher
{
    private readonly HttpClient _http;
    public string Provider => "wikipedia";
    public bool RequiresNetwork => true;

    public WikipediaEnricher(HttpClient? http = null) => _http = http ?? new HttpClient();

    public bool CanEnrich(Entity entity)
        => !string.IsNullOrWhiteSpace(entity.DisplayName) && entity.DisplayName.Trim().Length >= 3;

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        try
        {
            var titles = await ResolveTitlesAsync(entity, cancellationToken).ConfigureAwait(false);

            foreach (var title in titles)
            {
                var document = await TryFetchAsync(entity, title, cancellationToken).ConfigureAwait(false);
                if (document is not null)
                    return [document];
            }

            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// Fetches one candidate article, returning null when it turns out not to describe this entity.
    /// Candidates are tried in order rather than committed to, because the best-scoring title can
    /// still be unusable - "Notepad (disambiguation)" outranked everything on title shape alone and
    /// then failed the disambiguation check, leaving Notepad with no article at all even though
    /// "Windows Notepad" was in the same result set.
    /// </summary>
    private async Task<EnrichmentDocument?> TryFetchAsync(Entity entity, string title, CancellationToken cancellationToken)
    {
        var summaryJson = await CachedHttp.GetStringAsync(
            _http,
            "https://en.wikipedia.org/api/rest_v1/page/summary/" + Uri.EscapeDataString(title.Replace(' ', '_')),
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(summaryJson))
            return null;

        using var document = JsonDocument.Parse(summaryJson);
        var root = document.RootElement;

        // A disambiguation page lists unrelated topics that merely share a name, so its text
        // describes none of them. This is the guard that keeps the Sysinternals tool ZoomIt
        // away from the "ZOOMIT" page, which is about a Persian technology magazine.
        var type = root.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
        if (!string.Equals(type, "standard", StringComparison.OrdinalIgnoreCase))
            return null;

        var extract = root.TryGetProperty("extract", out var extractProperty) ? extractProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(extract) || extract!.Length < 60)
            return null;

        var shortDescription = root.TryGetProperty("description", out var descriptionProperty) ? descriptionProperty.GetString() : null;

        if (!DescribesSoftware(extract, shortDescription, entity))
            return null;

        var text = string.IsNullOrWhiteSpace(shortDescription)
            ? extract
            : shortDescription + ". " + extract;

        var canonical = root.TryGetProperty("content_urls", out var urls)
                        && urls.TryGetProperty("desktop", out var desktop)
                        && desktop.TryGetProperty("page", out var page)
            ? page.GetString()
            : "https://en.wikipedia.org/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_'));

        return new EnrichmentDocument
        {
            EntityId = entity.Id,
            Provider = Provider,
            IsOnline = true,
            Text = EnrichmentTextNormalizer.ToPlainText(text),
            SourceUri = canonical,
        };
    }

    /// <summary>
    /// Finds the article whose title *is* this entity's name, rather than one that merely ranks
    /// well for it. Wikipedia disambiguates by suffixing a parenthetical qualifier, so
    /// "Task Manager (Windows)" is still the article for "Task Manager"; anything that differs
    /// beyond that qualifier is a different subject and is rejected. Vendor-prefixed titles are
    /// accepted too, because encyclopedias title articles by the full product name
    /// ("Microsoft PowerPoint") where a Start menu entry uses the short one ("PowerPoint").
    ///
    /// Several articles can legitimately match: "Task Manager" hits both the Windows program and
    /// the general article about task managers as a class. Candidates are therefore scored rather
    /// than taken in search order, so the concrete product wins over the concept.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveTitlesAsync(Entity entity, CancellationToken cancellationToken)
    {
        var wanted = Normalize(entity.DisplayName);
        if (wanted.Length < 3)
            return [];

        var qualified = string.IsNullOrWhiteSpace(entity.Publisher)
            ? null
            : Normalize(entity.Publisher + entity.DisplayName);

        var matches = new List<(string Title, int Score)>();
        foreach (var query in Queries(entity))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&srlimit=5&srsearch="
                      + Uri.EscapeDataString(query);

            var json = await CachedHttp.GetStringAsync(_http, uri, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                continue;

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("query", out var queryElement)
                || !queryElement.TryGetProperty("search", out var results)
                || results.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var result in results.EnumerateArray())
            {
                var title = result.TryGetProperty("title", out var titleProperty) ? titleProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(title))
                    continue;

                // An index of disambiguation pages is never what we want, and such a page would
                // otherwise score highest of all, since it is by definition parenthetically
                // qualified.
                if (title!.Contains("(disambiguation)", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (matches.Any(m => string.Equals(m.Title, title, StringComparison.Ordinal)))
                    continue;

                var bare = StripQualifier(title);
                var key = Normalize(bare);
                var vendorPrefixed = IsVendorPrefixed(key, wanted, bare, entity);

                if (key == wanted || (qualified is not null && key == qualified) || vendorPrefixed)
                    matches.Add((title, TitleScore(title, entity, vendorPrefixed)));
            }
        }

        return [.. matches.OrderByDescending(m => m.Score).Select(m => m.Title)];
    }

    /// <summary>
    /// Prefers the article about this specific program over a same-named article about something
    /// else. A vendor prefix that the machine itself corroborates is the strongest signal: for
    /// Windows' Notepad, "Windows Notepad" is confirmed by the entity's own package identity,
    /// whereas "Notepad++" is a different publisher's product that merely begins with the same
    /// word - and would otherwise have won on the shared prefix.
    /// </summary>
    private static int TitleScore(string title, Entity entity, bool vendorPrefixed)
    {
        var score = 0;
        if (vendorPrefixed)
            score += 4;

        if (Regex.IsMatch(title, @"\([^)]*\)\s*$"))
            score += 2;

        if (!string.IsNullOrWhiteSpace(entity.Publisher)
            && title.StartsWith(entity.Publisher!, StringComparison.OrdinalIgnoreCase))
            score += 2;

        // An exact casing match indicates a proper noun - a product - rather than a common noun.
        if (title.StartsWith(entity.DisplayName, StringComparison.Ordinal))
            score += 1;

        return score;
    }

    /// <summary>
    /// Accepts an article titled with the vendor's name in front of the entity's, which is the
    /// normal encyclopedic form: the Start menu says "PowerPoint" and "Edge", Wikipedia says
    /// "Microsoft PowerPoint" and "Microsoft Edge". The leading words are only trusted when they
    /// already appear in the entity's own metadata - its publisher, or the install path that
    /// produced it - so the vendor is established from the machine rather than assumed, and
    /// "Adobe Photoshop" cannot attach itself to an unrelated "Photoshop" entry from someone else.
    /// </summary>
    private static bool IsVendorPrefixed(string key, string wanted, string title, Entity entity)
    {
        if (!key.EndsWith(wanted, StringComparison.Ordinal) || key.Length == wanted.Length)
            return false;

        var prefix = title[..^entity.DisplayName.Length].Trim();
        if (prefix.Length is 0 or > 20)
            return false;

        var evidence = string.Join(
            ' ',
            new[] { entity.Publisher, entity.LaunchTarget }
                .Concat(entity.RawMetadata.Values)
                .Where(v => !string.IsNullOrWhiteSpace(v)));

        return prefix
            .Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .All(word => evidence.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Queries(Entity entity)
    {
        var name = entity.DisplayName.Trim();
        yield return name;

        // The publisher disambiguates common product names, so that "Photos" reaches Microsoft's
        // application rather than the general article about photography.
        if (!string.IsNullOrWhiteSpace(entity.Publisher) && !name.Contains(entity.Publisher!, StringComparison.OrdinalIgnoreCase))
            yield return entity.Publisher + " " + name;
    }

    /// <summary>
    /// Confirms the article is about a piece of software. Exact-title matching still admits
    /// same-named subjects from other domains, and an article about, say, a band or a film would
    /// otherwise be embedded as though it described the program.
    /// </summary>
    private static bool DescribesSoftware(string extract, string? shortDescription, Entity entity)
    {
        var haystack = ((shortDescription ?? string.Empty) + " " + extract).ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(entity.Publisher)
            && haystack.Contains(entity.Publisher!.ToLowerInvariant(), StringComparison.Ordinal))
            return true;

        return SoftwareMarkers.Any(marker => Regex.IsMatch(haystack, $@"\b{marker}\b"));
    }

    private static readonly string[] SoftwareMarkers =
    [
        "software", "application", "app", "program", "utility", "utilities", "tool", "toolkit",
        "operating system", "freeware", "shareware", "open-source", "open source", "computer",
        "windows", "command", "browser", "editor", "client", "server", "package", "suite",
    ];

    private static string StripQualifier(string title)
        => Regex.Replace(title, @"\s*\([^)]*\)\s*$", string.Empty).Trim();

    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

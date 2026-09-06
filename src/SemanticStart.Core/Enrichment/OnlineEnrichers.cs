using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Enrichment;

public sealed class WingetManifestEnricher : IEnricher
{
    private readonly HttpClient _http;
    public string Provider => "winget";
    public bool RequiresNetwork => true;
    public WingetManifestEnricher(HttpClient? http = null) => _http = http ?? new HttpClient();
    public bool CanEnrich(Entity entity) => entity.Kind is EntityKind.Application or EntityKind.PackagedApp && !string.IsNullOrWhiteSpace(entity.DisplayName);

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = !string.IsNullOrWhiteSpace(entity.Publisher) ? $"{entity.Publisher} {entity.DisplayName}" : entity.DisplayName;
            var id = await ResolvePackageIdAsync(entity, query, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(id))
                return [];

            var fields = await ReadManifestFromGitHubAsync(id, cancellationToken).ConfigureAwait(false)
                         ?? ExtractWingetFields(await CachedProcess.RunAsync("winget", ["show", "--source", "winget", "--id", id, "--disable-interactivity"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false));

            var text = FormatFields(fields);
            return string.IsNullOrWhiteSpace(text) ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = true, Text = text, SourceUri = $"winget:{id}" }];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException or HttpRequestException or JsonException) { return []; }
    }

    private async Task<string?> ResolvePackageIdAsync(Entity entity, string query, CancellationToken cancellationToken)
    {
        var output = await CachedProcess.RunAsync("winget", ["search", "--source", "winget", "--query", query, "--disable-interactivity"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
        return ParseWingetId(output, entity);
    }

    private async Task<IReadOnlyDictionary<string, string[]>?> ReadManifestFromGitHubAsync(string packageId, CancellationToken cancellationToken)
    {
        var parts = packageId.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return null;

        var manifestDirectory = $"manifests/{char.ToLowerInvariant(packageId[0])}/{string.Join('/', parts)}";
        var listingUri = $"https://api.github.com/repos/microsoft/winget-pkgs/contents/{manifestDirectory}?ref=master";
        var listing = await CachedHttp.GetStringAsync(_http, listingUri, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(listing))
            return null;

        using var doc = JsonDocument.Parse(listing);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var version = doc.RootElement.EnumerateArray()
            .Select(e => e.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(v => !string.IsNullOrWhiteSpace(v) && Version.TryParse(NormalizeVersion(v!), out _))
            .OrderByDescending(v => Version.Parse(NormalizeVersion(v!)))
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var localeCandidates = new[]
        {
            $"{packageId}.locale.en-US.yaml",
            $"{packageId}.locale.en.yaml",
            $"{packageId}.yaml",
        };
        foreach (var file in localeCandidates)
        {
            var raw = $"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/{manifestDirectory}/{version}/{file}";
            var yaml = await CachedHttp.GetStringAsync(_http, raw, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(yaml))
                continue;

            var fields = ParseManifestFields(yaml);
            if (fields.Count > 0)
                return fields;
        }

        return null;
    }

    private static string NormalizeVersion(string value)
    {
        var pieces = value.Split(['.', '-'], StringSplitOptions.RemoveEmptyEntries).Take(4).Select(p => int.TryParse(p, out _) ? p : "0").ToList();
        while (pieces.Count < 2) pieces.Add("0");
        return string.Join('.', pieces);
    }

    private static string? ParseWingetId(string output, Entity entity)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = Regex.Replace(line.Trim(), @"\s{2,}", "|");
            var cols = trimmed.Split('|');
            if (cols.Length < 2 || !cols[1].Contains('.', StringComparison.Ordinal) || cols[1].Equals("Id", StringComparison.OrdinalIgnoreCase))
                continue;

            var name = cols[0].Trim();
            var id = cols[1].Trim();
            if (IsPlausibleMatch(entity, name, id))
                return id;
        }
        return null;
    }

    /// <summary>
    /// Decides whether a winget search hit is really this entity's package.
    ///
    /// Matching is on whole words rather than substrings. Substring containment silently attached
    /// third-party lookalikes to inbox tools: Windows' own Notepad matched a package called
    /// "SkyNotepad" by an unrelated author, and every description, tag, and task on the Notepad
    /// entry then came from that clone - which is why "edit a file" could not find the real Notepad.
    /// Identifiers are split only on punctuation, never on case. Splitting "SkyNotepad" into "sky"
    /// and "notepad" reintroduces exactly the false match, and buys nothing: winget reports a
    /// separate, already-tokenised name column, so a genuine package is matched through that.
    /// </summary>
    private static bool IsPlausibleMatch(Entity entity, string name, string id)
    {
        var wanted = NormalizeForMatch(entity.DisplayName)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3)
            .ToArray();

        if (wanted.Length == 0)
            return false;

        var candidate = new HashSet<string>(
            NormalizeForMatch(name + " " + id.Replace('.', ' '))
                .Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);

        return wanted.All(candidate.Contains) && PublisherAgrees(entity, id);
    }

    /// <summary>
    /// Requires the package's vendor to be the entity's vendor, when the machine already told us
    /// who that is. Name matching alone cannot separate an inbox tool from the third-party programs
    /// named after it, and winget has no entry for most inbox tools, so every candidate it returns
    /// for one is wrong by construction: Windows Notepad matched "SkyNotepad" and then "Notepad++",
    /// each of which then supplied Notepad's description, tags, and tasks.
    ///
    /// A winget identifier is "Publisher.Package", and the entity's publisher comes from its own
    /// MSIX manifest or version resource, so both sides are authoritative. Comparison is on
    /// normalised text with legal suffixes removed, so "Microsoft Corporation" still matches
    /// "Microsoft" and "AgileBits" still matches "Agile Bits".
    /// </summary>
    private static bool PublisherAgrees(Entity entity, string id)
    {
        var publisher = PublisherHint(entity);
        if (string.IsNullOrWhiteSpace(publisher))
            return true;

        var separator = id.IndexOf('.');
        if (separator <= 0)
            return true;

        var declared = NormalizePublisher(publisher!);
        var packaged = NormalizePublisher(id[..separator]);

        // Once the vendor is known, a package whose vendor segment does not correspond to it is a
        // different product regardless of how short that segment is. Treating a brief segment as
        // "no evidence" and passing was how "ndd.Notepad--" attached itself to Windows Notepad.
        if (declared.Length < 4 || packaged.Length < 2)
            return true;

        return declared.Contains(packaged, StringComparison.Ordinal)
               || packaged.Contains(declared, StringComparison.Ordinal);
    }

    /// <summary>
    /// The entity's vendor, taken from its declared publisher or, failing that, from the leading
    /// segment of its MSIX package family name - "Microsoft.WindowsNotepad_8wekyb3d8bbwe" is
    /// published by Microsoft. Shell-enumerated applications carry no publisher field, which is
    /// most of the ones that need this check, so the identity string is the only signal available.
    ///
    /// Publisher-hash style segments are ignored: some packages use an opaque account id
    /// ("dc5c6510.2032887045529") that names no vendor, and treating it as one would reject
    /// perfectly good matches.
    /// </summary>
    private static string? PublisherHint(Entity entity)
    {
        if (!string.IsNullOrWhiteSpace(entity.Publisher))
            return entity.Publisher;

        // Only a package identity names a publisher. A file system path does not: reading the
        // leading segment of "powerpoint.lnk" as a vendor made PowerPoint disagree with
        // "Microsoft.PowerPoint" and lose a correct match.
        var identity = entity.RawMetadata.GetValueOrDefault("packageFamilyName")
                       ?? entity.RawMetadata.GetValueOrDefault("appUserModelId");

        if (string.IsNullOrWhiteSpace(identity) && entity.Id.StartsWith("appsfolder:", StringComparison.OrdinalIgnoreCase))
            identity = entity.Id["appsfolder:".Length..];

        if (string.IsNullOrWhiteSpace(identity))
            return null;

        var trimmed = identity!.Split('!')[0];
        var separator = trimmed.IndexOf('.');
        if (separator <= 0)
            return null;

        var segment = trimmed[..separator];
        if (segment.Count(char.IsLetter) < 4 || segment.Any(char.IsDigit))
            return null;

        return segment;
    }

    private static string NormalizePublisher(string value)
    {
        var stripped = Regex.Replace(
            value,
            @"\b(corporation|corp|incorporated|inc|limited|ltd|llc|gmbh|team|software|technologies|company|co)\b",
            string.Empty,
            RegexOptions.IgnoreCase);

        return new string(stripped.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static string NormalizeForMatch(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"\b(microsoft|windows|app|desktop|64-bit|32-bit|x64|x86)\b|[^a-z0-9]+", " ").Trim();

    private static IReadOnlyDictionary<string, string[]> ExtractWingetFields(string output)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            if (key is not ("Description" or "Short Description" or "Tags" or "Moniker" or "Homepage" or "Publisher"))
                continue;

            Add(fields, key, line[(separator + 1)..]);
        }

        return fields.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string[]> ParseManifestFields(string yaml)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? activeSequence = null;
        var wanted = new HashSet<string>(["PackageName", "Publisher", "ShortDescription", "Description", "PackageUrl", "Moniker", "Tags"], StringComparer.OrdinalIgnoreCase);
        foreach (var raw in yaml.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd();
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || trimmed.Length == 0)
                continue;

            if (activeSequence is not null && trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                Add(fields, activeSequence, trimmed[1..]);
                continue;
            }

            activeSequence = null;
            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
                continue;

            var key = trimmed[..separator].Trim();
            if (!wanted.Contains(key))
                continue;

            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length == 0 && key.Equals("Tags", StringComparison.OrdinalIgnoreCase))
                activeSequence = key;
            else
                Add(fields, key, value);
        }

        return fields.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static string FormatFields(IReadOnlyDictionary<string, string[]> fields)
    {
        var lines = new List<string>();
        AddLine(lines, "Name", fields, "PackageName");
        AddLine(lines, "Publisher", fields, "Publisher");
        AddLine(lines, "Short Description", fields, "ShortDescription", "Short Description");
        AddLine(lines, "Description", fields, "Description");
        AddLine(lines, "Tags", fields, "Tags");
        AddLine(lines, "Moniker", fields, "Moniker");
        AddLine(lines, "Homepage", fields, "PackageUrl", "Homepage");
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddLine(List<string> lines, string label, IReadOnlyDictionary<string, string[]> fields, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!fields.TryGetValue(key, out var values) || values.Length == 0)
                continue;
            lines.Add($"{label}: {string.Join("; ", values)}");
            return;
        }
    }

    private static void Add(Dictionary<string, List<string>> fields, string key, string? value)
    {
        value = CleanYamlScalar(value);
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!fields.TryGetValue(key, out var values))
            fields[key] = values = [];
        values.Add(value);
    }

    private static string? CleanYamlScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim().Trim('"', '\'');
        return EnrichmentTextNormalizer.ToPlainText(value);
    }
}

public sealed class LearnEnricher : IEnricher
{
    private readonly HttpClient _http;
    private readonly LearnTocCatalog _toc;
    public string Provider => "learn";
    public bool RequiresNetwork => true;
    public LearnEnricher(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _toc = new LearnTocCatalog(_http);
    }
    /// <summary>
    /// Learn documents Windows features and a great many first-party tools. Applications were
    /// excluded, which silently ruled out precisely the tools users cannot name: every Sysinternals
    /// utility is documented on Learn, but ZoomIt and Process Explorer could never be enriched from
    /// it and indexed with no text beyond their own names. Apps are now included; irrelevant results
    /// are already filtered by <see cref="IsLikelyRelevant"/>.
    /// </summary>
    public bool CanEnrich(Entity entity) =>
        entity.Kind is EntityKind.SettingsPage or EntityKind.ControlPanelApplet or EntityKind.ManagementConsole
            or EntityKind.OptionalFeature or EntityKind.SystemTool or EntityKind.Application or EntityKind.PackagedApp
        && !string.IsNullOrWhiteSpace(entity.DisplayName);

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        try
        {
            // Collect across every query and keep only the best tier. Stopping at the first query
            // that returned anything used to accept a page that merely name-dropped the entity —
            // Snipping Tool was described by the "Features on Demand" catalogue — while its own
            // article was one query away.
            var candidates = new List<(int Tier, LearnResult Result)>();
            var neighbourhood = new List<string>();

            // The publisher's own table of contents is consulted first and trusted above anything
            // search returns: an exact title match in a docset is a statement that this article is
            // about this tool, whereas a search hit is a guess. This is the only path that finds
            // Task Manager and much of Sysinternals at all.
            var tocUrl = await _toc.FindAsync(entity.DisplayName, TocAliases(entity), cancellationToken).ConfigureAwait(false);
            if (tocUrl is not null)
                candidates.Add((SlugTier + 1, new LearnResult(entity.DisplayName, null, tocUrl)));

            foreach (var query in BuildQueries(entity).Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
            {
                if (candidates.Count > 0)
                    break;

                var uri = "https://learn.microsoft.com/api/search?locale=en-us&$top=5&search=" + Uri.EscapeDataString(query);
                var json = await CachedHttp.GetStringAsync(_http, uri, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                foreach (var result in ParseResults(json))
                {
                    if (IsHubPage(result.Url))
                        continue;

                    // Even a rejected result is topically adjacent, which is exactly what the
                    // sibling probe below needs.
                    if (!string.IsNullOrWhiteSpace(result.Url))
                        neighbourhood.Add(result.Url!);

                    var tier = Relevance(entity, result);
                    if (tier >= AboutTier)
                        candidates.Add((tier, result));
                }

                if (candidates.Any(c => c.Tier >= SlugTier))
                    break;
            }

            if (!candidates.Any(c => c.Tier >= SlugTier))
            {
                var probed = await ProbeSiblingArticleAsync(entity, neighbourhood, cancellationToken).ConfigureAwait(false);
                if (probed is not null)
                    candidates.Add((SlugTier, probed));
            }

            if (candidates.Count == 0)
                return [];

            var bestTier = candidates.Max(c => c.Tier);
            var docs = new List<EnrichmentDocument>();
            foreach (var result in candidates.Where(c => c.Tier == bestTier).Select(c => c.Result).DistinctBy(r => r.Url).Take(2))
            {
                // The page title is deliberately not used as prose. It reads like a description but
                // is really a heading, and it became the entity's whole summary: Power Automate was
                // "Limits of automated, scheduled, and instant flows", Clipchamp was "Video
                // analytics in Microsoft 365". The synthesizer takes the leading sentences, so
                // anything put first here becomes what the user sees.
                var snippets = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.Description)) snippets.Add(result.Description!);
                var article = await FetchArticleExcerptAsync(result.Url, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(article)) snippets.Add(article!);

                if (snippets.Count == 0)
                    continue;

                var text = EnrichmentTextNormalizer.ToPlainText(string.Join(" ", snippets));
                if (!string.IsNullOrWhiteSpace(text))
                    docs.Add(new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = true, Text = text, SourceUri = result.Url });
            }

            return docs.DistinctBy(d => d.SourceUri).Take(3).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException) { }
        return [];
    }

    /// <summary>
    /// Extra names to look up in the table of contents. Collectors often report a tool by its
    /// executable ("taskmgr.exe") or by a suite display name, while the TOC titles it as the
    /// product, so the file name without extension is tried as well.
    /// </summary>
    private static IEnumerable<string> TocAliases(Entity entity)
    {
        var file = entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileName(entity.LaunchTarget);
        if (!string.IsNullOrWhiteSpace(file))
        {
            yield return file;
            var bare = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(bare))
                yield return bare;
        }

        if (entity.RawMetadata.TryGetValue("featureName", out var feature) && !string.IsNullOrWhiteSpace(feature))
            yield return feature.Replace('-', ' ');
    }

    private async Task<string?> FetchArticleExcerptAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var html = await CachedHttp.GetStringAsync(_http, uri.ToString(), TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
            return null;

        // Extraction precedence follows established practice for documentation pages: structured
        // data first, then link-preview prose, then the SEO meta tag, then body text. The page
        // <title> is deliberately never used. Titles are written to be clicked, not read: they
        // carry site suffixes and describe the article rather than the product, which is how
        // Clipchamp came to be summarised as "Video analytics in Microsoft 365" and Claude as
        // "Configure Claude Code for Microsoft Foundry".
        var parts = new List<string>();
        var title = PageTitle(html);
        var lead = StructuredDescription(html, title);
        if (!string.IsNullOrWhiteSpace(lead))
            parts.Add(lead!);

        var prose = EnrichmentTextNormalizer.ToPlainText(html);
        var sentences = SelectUsefulSentences(prose, 900);
        if (!string.IsNullOrWhiteSpace(sentences))
            parts.Add(sentences);

        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// Section headings from a documentation page, which are the closest thing the web offers to a
    /// list of what a tool can do. Prose describes a product; headings name its operations - "End
    /// task", "Manage startup apps", "Analyze wait chain" - in the imperative form a user actually
    /// types. Meta descriptions frequently describe the *article* rather than the tool ("Describes
    /// the features of Task Manager and provides examples..."), which contributes no task
    /// vocabulary at all, so the headings often carry the only usable signal on the page.
    /// </summary>
    private static string[] SectionHeadings(string html)
    {
        var results = new List<string>();
        foreach (Match match in Regex.Matches(html, @"<h[23][^>]*>(.*?)</h[23]>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var text = EnrichmentTextNormalizer.ToPlainText(match.Groups[1].Value).Trim().TrimEnd('.', ':');
            if (text.Length is < 3 or > 70)
                continue;

            if (NavigationHeadings.Contains(text))
                continue;

            if (!results.Contains(text, StringComparer.OrdinalIgnoreCase))
                results.Add(text);

            if (results.Count == 12)
                break;
        }

        return [.. results];
    }

    /// <summary>
    /// Headings that are part of every documentation page's furniture rather than anything about
    /// the tool being documented.
    /// </summary>
    private static readonly HashSet<string> NavigationHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "In this article", "See also", "Next steps", "Related articles", "Related content",
        "Prerequisites", "Requirements", "Feedback", "Additional resources", "References",
        "Applies to", "Summary", "Overview", "Introduction", "Remarks", "Examples", "Syntax",
        "Parameters", "Notes", "Table of contents", "More information", "Symptoms", "Cause",
        "Resolution", "Comments", "Contents", "Disclaimer", "Data collection", "Recommended content",
    };

    private static string? StructuredDescription(string html, string? title)
    {
        foreach (var candidate in new[] { JsonLdDescription(html), MetaContent(html, "og:description", "property"), MetaContent(html, "description", "name") })
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate!.Length < 25)
                continue;

            // A meta description that merely repeats the title is auto-generated filler and carries
            // the same problems as using the title directly.
            if (title is not null && SharesLead(candidate, title))
                continue;

            return candidate;
        }

        return null;
    }

    private static bool SharesLead(string a, string b)
    {
        static string Norm(string s) => Regex.Replace(s.Split('|')[0].Split('-')[0], @"\W+", " ").Trim().ToLowerInvariant();
        var x = Norm(a);
        var y = Norm(b);
        return x.Length > 0 && y.Length > 0 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal));
    }

    private static string? PageTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;
        var t = System.Net.WebUtility.HtmlDecode(m.Groups["t"].Value).Trim();
        return t.Length == 0 ? null : t;
    }

    private static string? JsonLdDescription(string html)
    {
        foreach (Match block in Regex.Matches(html, @"<script[^>]*type\s*=\s*[""']application/ld\+json[""'][^>]*>(?<j>.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var m = Regex.Match(block.Groups["j"].Value, @"""description""\s*:\s*""(?<d>(?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var text = Regex.Unescape(m.Groups["d"].Value).Trim();
            if (text.Length >= 25) return text;
        }

        return null;
    }

    private static string? MetaContent(string html, string key, string attribute)
    {
        var pattern = $@"<meta\s+[^>]*{attribute}\s*=\s*[""']{Regex.Escape(key)}[""'][^>]*content\s*=\s*[""'](?<c>[^""']*)[""']";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            pattern = $@"<meta\s+[^>]*content\s*=\s*[""'](?<c>[^""']*)[""'][^>]*{attribute}\s*=\s*[""']{Regex.Escape(key)}[""']";
            match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        }

        if (!match.Success)
            return null;

        var text = System.Net.WebUtility.HtmlDecode(match.Groups["c"].Value).Trim();
        return text.Length == 0 ? null : text;
    }

    private static IEnumerable<string> BuildQueries(Entity entity)
    {
        if (entity.LaunchTarget.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            yield return entity.LaunchTarget;
            yield return $"Windows {entity.DisplayName} settings";
            yield return $"{entity.DisplayName} ms-settings Windows";
            yield break;
        }

        // For packaged apps the family name carries the suite or publisher, which is often the
        // difference between finding the right article and finding nothing: a Learn search for
        // "ZoomIt" alone is ambiguous, while "Sysinternals ZoomIt" lands on its documentation page.
        // This is tried *first* because it is the most specific query available; the filename query
        // below is broad enough that it used to crowd the publisher query out of the query budget.
        if (entity.Kind == EntityKind.PackagedApp)
        {
            var aumid = entity.RawMetadata.GetValueOrDefault("appUserModelId") ?? entity.LaunchTarget;
            var family = aumid.Split('!', 2)[0];
            var publisherSeparator = family.LastIndexOf('_');
            var name = publisherSeparator > 0 ? family[..publisherSeparator] : family;

            foreach (var part in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                // "SysinternalsSuite" searches poorly; its leading word "Sysinternals" is the brand
                // that actually appears in article titles and URL slugs.
                var brand = Regex.Split(part, @"(?<=[a-z0-9])(?=[A-Z])").FirstOrDefault() ?? part;
                if (brand.Length < 4 || GenericTokens.Contains(brand) || brand.Equals(entity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return $"{brand} {entity.DisplayName}";
            }
        }

        var file = entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileNameWithoutExtension(entity.LaunchTarget);
        if (!string.IsNullOrWhiteSpace(file))
        {
            var withoutExtension = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(withoutExtension) && entity.Kind == EntityKind.SystemTool)
                yield return $"windows command {withoutExtension}";
            yield return $"{entity.DisplayName} {file} Windows";
        }

        yield return entity.Kind == EntityKind.OptionalFeature
            ? $"Windows optional feature {entity.DisplayName}"
            : $"{entity.DisplayName} Windows";
    }

    private const int AboutTier = 2;
    private const int SlugTier = 3;

    /// <summary>
    /// Learn's search ranking is unreliable for individual tool pages: a search for "Sysinternals
    /// Process Explorer" returns TCPView, Handle, ZoomIt and ProcDump but never process-explorer
    /// itself, even at $top=10. Those siblings do, however, reveal the *directory* the tool's own
    /// article lives in, so we construct the canonical URL directly and fetch it. This is general
    /// (any doc set that groups articles by area benefits) and costs at most a handful of requests,
    /// only for entities that search alone could not resolve.
    /// </summary>
    private async Task<LearnResult?> ProbeSiblingArticleAsync(Entity entity, IReadOnlyList<string> neighbourhood, CancellationToken cancellationToken)
    {
        var directories = neighbourhood
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase) ? u : null)
            .Where(u => u is not null)
            .Select(u => u!.GetLeftPart(UriPartial.Authority) + u.AbsolutePath.TrimEnd('/')[..(u.AbsolutePath.TrimEnd('/').LastIndexOf('/') + 1)])
            .Where(d => d.Split('/', StringSplitOptions.RemoveEmptyEntries).Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        foreach (var directory in directories)
        {
            foreach (var slug in SlugCandidates(entity).Take(3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = directory + slug;
                var html = await CachedHttp.GetStringAsync(_http, url, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                    continue;

                var title = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value.Trim();
                title = Regex.Replace(System.Net.WebUtility.HtmlDecode(title), @"\s*\|\s*Microsoft Learn\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
                // Guard against doc sites that serve a soft-404 landing page instead of a 404.
                if (string.IsNullOrWhiteSpace(title) || !ContainsWord(title, entity.DisplayName))
                    continue;

                return new LearnResult(title, null, url);
            }
        }

        return null;
    }

    private static IEnumerable<string> SlugCandidates(Entity entity)
    {
        var name = entity.DisplayName.Trim();
        if (name.Length >= 4)
        {
            var hyphenated = Regex.Replace(name, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
            if (hyphenated.Length >= 4)
                yield return hyphenated;

            var collapsed = Normalize(name);
            if (collapsed.Length >= 4 && collapsed != hyphenated)
                yield return collapsed;
        }

        // Documentation frequently uses the executable name as the slug rather than the display
        // name — Process Monitor is documented at .../procmon, not .../process-monitor.
        foreach (var token in IdentifyingTokens(entity))
        {
            var slug = Normalize(token);
            if (slug.Length >= 4)
                yield return slug;
        }
    }

    /// <summary>
    /// Scores how strongly a result is *about* the entity rather than merely mentioning it:
    /// 3 = the article's URL slug is the entity, 2 = its title names the entity. Anything weaker is
    /// rejected outright. A "mere mention" tier used to exist and was the single largest source of
    /// nonsense descriptions: Process Monitor was described by the Dev Drive article and the Run
    /// dialog by the Learn front page, purely because those pages contained the word somewhere.
    /// </summary>
    private static int Relevance(Entity entity, LearnResult result)
    {
        var slug = SlugOf(result.Url);
        var normalizedName = Normalize(entity.DisplayName);
        var tokens = IdentifyingTokens(entity).Select(Normalize).Where(t => t.Length >= 4).ToArray();

        // A page that merely *lists* an ms-settings URI is a catalogue, not a description. Learn's
        // "Launch Windows Settings" page enumerates every URI in Windows, so it matched every
        // optional feature and settings page at the highest tier — which is how PowerShell ISE and
        // Notepad came to be summarised as "Launch Windows Settings - Windows apps". Requiring the
        // URI in the title or description, not the URL or body, keeps genuine per-setting articles.
        if (entity.LaunchTarget.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
            && $"{result.Title} {result.Description}".Contains(entity.LaunchTarget, StringComparison.OrdinalIgnoreCase))
            return 3;

        if (normalizedName.Length >= 4 && (slug == normalizedName || tokens.Contains(slug)))
            return 3;

        // The title must name the entity as a whole word. Substring matching promoted "Maps" from
        // any title containing "Bitmaps", and short names like "Run" match almost anything, so
        // names under four characters are only ever accepted via their URL slug above.
        //
        // Names that are ordinary English words are excluded here too. "Files" matched a Defender
        // for Office article titled "Manage quarantined messages and files as an admin", which then
        // became that app's entire description. Such names are only ever accepted via their slug.
        if (entity.DisplayName.Length >= 4
            && !IsGenericName(entity.DisplayName)
            && !string.IsNullOrWhiteSpace(result.Title)
            && ContainsWord(result.Title!, entity.DisplayName))
            return AboutTier;

        return 0;
    }

    /// <summary>
    /// True when the display name is a common word that routinely appears in unrelated article
    /// titles, so matching it there is not evidence the article is about this entity.
    /// </summary>
    private static bool IsGenericName(string displayName) =>
        !displayName.Contains(' ') && GenericNames.Contains(displayName.Trim());

    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "files", "file", "photos", "photo", "maps", "map", "mail", "code", "run", "store", "people",
        "calendar", "clock", "camera", "video", "music", "news", "weather", "notes", "tips", "help",
        "settings", "search", "home", "chat", "phone", "links", "tasks", "groups", "teams", "media"
    };

    private static bool ContainsWord(string haystack, string needle) =>
        Regex.IsMatch(haystack, $@"(?<![\w]){Regex.Escape(needle)}(?![\w])", RegexOptions.IgnoreCase);

    /// <summary>
    /// Documentation hubs, landing pages, and non-product areas. Hubs describe a whole product area
    /// rather than a single tool, so their prose ("Windows technical documentation for developers
    /// and IT pros") is pure noise in an embedding. The excluded areas are worse than noise: the
    /// editorial style guide describes *how to write about* a product, which is why Snipping Tool
    /// was summarised as capitalization rules and its relationship with Snip &amp; Sketch, and
    /// training and Q&amp;A pages describe exercises and questions rather than the thing itself.
    /// </summary>
    private static bool IsHubPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // e.g. /en-us/windows/ -> ["en-us","windows"]; a real article is at least one level deeper.
        if (segments.Length <= 2)
            return true;

        return segments.Any(segment => ExcludedAreas.Any(area => segment.StartsWith(area, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly string[] ExcludedAreas =
    [
        "product-style-guide", "style-guide", "training", "certifications", "credentials",
        "answers", "shows", "events", "samples", "assessments", "plans"
    ];

    private static string SlugOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return string.Empty;
        var segment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return Normalize(segment ?? string.Empty);
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "app", "apps", "application", "tool", "tools", "system", "shell", "exe", "com", "net"
    };

    private static IEnumerable<string> IdentifyingTokens(Entity entity)
    {
        var candidates = new List<string?>();

        var aumid = entity.RawMetadata.GetValueOrDefault("appUserModelId") ?? entity.LaunchTarget;
        var bang = aumid.IndexOf('!');
        if (bang >= 0 && bang < aumid.Length - 1)
            candidates.Add(aumid[(bang + 1)..]);

        var file = entity.RawMetadata.GetValueOrDefault("fileName");
        if (string.IsNullOrWhiteSpace(file) && !entity.LaunchTarget.Contains('!') && !entity.LaunchTarget.Contains("://", StringComparison.Ordinal))
            file = Path.GetFileName(entity.LaunchTarget);
        if (!string.IsNullOrWhiteSpace(file))
            candidates.Add(Path.GetFileNameWithoutExtension(file));

        return candidates
            .Where(token => !string.IsNullOrWhiteSpace(token) && token!.Length >= 4 && !GenericTokens.Contains(token))
            .Select(token => token!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<LearnResult> ParseResults(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Select(result => new LearnResult(
                result.TryGetProperty("title", out var t) ? t.GetString() : null,
                result.TryGetProperty("description", out var d) ? d.GetString() : null,
                result.TryGetProperty("url", out var u) ? u.GetString() : null))
            .Where(r => !string.IsNullOrWhiteSpace(r.Description) || !string.IsNullOrWhiteSpace(r.Title))
            .ToArray();
    }

    private static readonly string[] BoilerplateMarkers =
    [
        "Microsoft Learn", "Sign in", "Upgrade to Microsoft Edge", "Microsoft Edge", "technical support",
        "This browser is no longer supported", "Table of contents", "Skip to main content",
        "Read in English", "Save Add to Collections", "Add to plan", "Share via", "was this page helpful",
        "Submit and view feedback", "Additional resources", "In this article", "Feedback",
        "Access to this page requires authorization", "changing directories", "Download Microsoft Edge",
        "only available to authorized"
    ];

    private static string SelectUsefulSentences(string prose, int maxChars)
    {
        var sentences = Regex.Split(prose, @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length is >= 40 and <= 300)
            .Where(s => !BoilerplateMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Take(5);
        var text = string.Join(" ", sentences);
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private sealed record LearnResult(string? Title, string? Description, string? Url);
}

public sealed class PublisherSiteEnricher : IEnricher
{
    private readonly HttpClient _http;
    public string Provider => "publisher-site";
    public bool RequiresNetwork => true;
    public PublisherSiteEnricher(HttpClient? http = null) => _http = http ?? new HttpClient();
    public bool CanEnrich(Entity entity) => entity.RawMetadata.Keys.Any(k => k.Contains("url", StringComparison.OrdinalIgnoreCase) || k.Contains("website", StringComparison.OrdinalIgnoreCase) || k.Contains("homepage", StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var uri = entity.RawMetadata.FirstOrDefault(kv => (kv.Key.Contains("url", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("website", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("homepage", StringComparison.OrdinalIgnoreCase)) && IsPublicHttpUri(kv.Value)).Value;
        if (string.IsNullOrWhiteSpace(uri)) return [];
        try
        {
            var html = await CachedHttp.GetStringAsync(_http, uri, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var desc = EnrichmentTextNormalizer.ExtractMetaDescription(html);
            return string.IsNullOrWhiteSpace(desc) ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = true, Text = desc, SourceUri = uri }];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException) { return []; }
    }

    private static bool IsPublicHttpUri(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

internal static class CachedProcess
{
    public static async Task<string> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = ResolveOnPath(fileName);
        if (executable is null)
            return string.Empty;

        Directory.CreateDirectory(AppPaths.EnrichmentCacheDirectory);
        var key = "process-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName + "\0" + string.Join("\0", args)))).ToLowerInvariant();
        var path = Path.Combine(AppPaths.EnrichmentCacheDirectory, key + ".txt");
        if (File.Exists(path))
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        var result = await ProcessRunner.RunAsync(executable, args, timeout, cancellationToken).ConfigureAwait(false);
        var output = result.Output.Length > 64 * 1024 ? result.Output[..(64 * 1024)] : result.Output;
        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            await File.WriteAllTextAsync(path, output, cancellationToken).ConfigureAwait(false);
        return output;
    }

    private static string? ResolveOnPath(string fileName)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
                if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    candidate = Path.Combine(dir.Trim(), fileName + ".exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return null;
    }
}

internal static class CachedHttp
{
    private const int MaxResponseBytes = 512 * 1024;
    private static readonly SemaphoreSlim Throttle = new(3, 3);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private static readonly object DelayLock = new();

    public static async Task<string> GetStringAsync(HttpClient http, string uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.EnrichmentCacheDirectory);
        var key = "http-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri))).ToLowerInvariant();
        var path = Path.Combine(AppPaths.EnrichmentCacheDirectory, key + ".txt");
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        await Throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan wait;
            lock (DelayLock)
            {
                var elapsed = DateTimeOffset.UtcNow - _lastRequest;
                wait = elapsed < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) - elapsed : TimeSpan.Zero;
                _lastRequest = DateTimeOffset.UtcNow + wait;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SemanticStart/1.0 (+https://github.com/microsoft/SemanticStart)");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return string.Empty;
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaxResponseBytes) return string.Empty;
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            var total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaxResponseBytes) return string.Empty;
                memory.Write(buffer, 0, read);
            }
            var text = Encoding.UTF8.GetString(memory.ToArray());
            await File.WriteAllTextAsync(path, text, cts.Token).ConfigureAwait(false);
            return text;
        }
        finally { Throttle.Release(); }
    }
}

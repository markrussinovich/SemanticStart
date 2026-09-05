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

    private static bool IsPlausibleMatch(Entity entity, string name, string id)
    {
        var normalizedName = NormalizeForMatch(entity.DisplayName);
        var haystack = NormalizeForMatch(name + " " + id);
        if (normalizedName.Length >= 4 && haystack.Contains(normalizedName, StringComparison.Ordinal))
            return true;

        var tokens = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 4).ToArray();
        return tokens.Length > 0 && tokens.All(t => haystack.Contains(t, StringComparison.Ordinal));
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
    public string Provider => "learn";
    public bool RequiresNetwork => true;
    public LearnEnricher(HttpClient? http = null) => _http = http ?? new HttpClient();
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
            foreach (var query in BuildQueries(entity).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
            {
                var uri = "https://learn.microsoft.com/api/search?locale=en-us&$top=5&search=" + Uri.EscapeDataString(query);
                var json = await CachedHttp.GetStringAsync(_http, uri, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                foreach (var result in ParseResults(json))
                {
                    var tier = Relevance(entity, result);
                    if (tier > 0)
                        candidates.Add((tier, result));
                }

                if (candidates.Any(c => c.Tier >= AboutTier))
                    break;
            }

            if (candidates.Count == 0)
                return [];

            var bestTier = candidates.Max(c => c.Tier);
            var docs = new List<EnrichmentDocument>();
            foreach (var result in candidates.Where(c => c.Tier == bestTier).Select(c => c.Result).DistinctBy(r => r.Url).Take(2))
            {
                var snippets = new List<string>();
                if (!string.IsNullOrWhiteSpace(result.Title)) snippets.Add(result.Title!);
                if (!string.IsNullOrWhiteSpace(result.Description)) snippets.Add(result.Description!);
                var article = await FetchArticleExcerptAsync(result.Url, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(article)) snippets.Add(article!);
                var text = EnrichmentTextNormalizer.ToPlainText(string.Join(". ", snippets));
                if (!string.IsNullOrWhiteSpace(text))
                    docs.Add(new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = true, Text = text, SourceUri = result.Url });
            }

            return docs.DistinctBy(d => d.SourceUri).Take(3).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException) { }
        return [];
    }

    private async Task<string?> FetchArticleExcerptAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Equals("learn.microsoft.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var html = await CachedHttp.GetStringAsync(_http, uri.ToString(), TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var prose = EnrichmentTextNormalizer.ToPlainText(html);
        return SelectUsefulSentences(prose, 900);
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

        var file = entity.RawMetadata.GetValueOrDefault("fileName") ?? Path.GetFileNameWithoutExtension(entity.LaunchTarget);
        if (!string.IsNullOrWhiteSpace(file))
        {
            var withoutExtension = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(withoutExtension) && entity.Kind == EntityKind.SystemTool)
                yield return $"windows command {withoutExtension}";
            yield return $"{entity.DisplayName} {file} Windows";
        }

        // For packaged apps the family name carries the suite or publisher, which is often the
        // difference between finding the right article and finding nothing: a Learn search for
        // "ZoomIt" alone is ambiguous, while "Sysinternals ZoomIt" lands on its documentation page.
        if (entity.Kind == EntityKind.PackagedApp)
        {
            var aumid = entity.RawMetadata.GetValueOrDefault("appUserModelId") ?? entity.LaunchTarget;
            var family = aumid.Split('!', 2)[0];
            var publisherSeparator = family.LastIndexOf('_');
            var name = publisherSeparator > 0 ? family[..publisherSeparator] : family;

            foreach (var part in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length < 4 || part.Equals(entity.DisplayName, StringComparison.OrdinalIgnoreCase))
                    continue;

                yield return $"{part} {entity.DisplayName}";
            }
        }

        yield return entity.Kind == EntityKind.OptionalFeature
            ? $"Windows optional feature {entity.DisplayName}"
            : $"{entity.DisplayName} Windows";
    }

    private const int AboutTier = 2;

    /// <summary>
    /// Scores how strongly a result is *about* the entity rather than merely mentioning it:
    /// 3 = the article's URL slug is the entity, 2 = its title names the entity, 1 = only the body
    /// or an identifying token matches. Only the best available tier is kept. Accepting any mention
    /// let unrelated articles become an entity's whole description.
    /// </summary>
    private static int Relevance(Entity entity, LearnResult result)
    {
        var slug = SlugOf(result.Url);
        var normalizedName = Normalize(entity.DisplayName);
        var tokens = IdentifyingTokens(entity).Select(Normalize).Where(t => t.Length >= 4).ToArray();

        if (normalizedName.Length >= 4 && (slug == normalizedName || tokens.Contains(slug)))
            return 3;
        if (!string.IsNullOrWhiteSpace(result.Title) && result.Title!.Contains(entity.DisplayName, StringComparison.OrdinalIgnoreCase))
            return AboutTier;

        var haystack = $"{result.Title} {result.Description} {result.Url}";
        if (haystack.Contains(entity.DisplayName, StringComparison.OrdinalIgnoreCase))
            return 1;
        if (entity.LaunchTarget.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) && haystack.Contains(entity.LaunchTarget, StringComparison.OrdinalIgnoreCase))
            return 3;

        return tokens.Any(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase)) ? 1 : 0;
    }

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

    private static string SelectUsefulSentences(string prose, int maxChars)
    {
        var sentences = Regex.Split(prose, @"(?<=[.!?])\s+")
            .Select(s => s.Trim())
            .Where(s => s.Length is >= 40 and <= 300)
            .Where(s => !s.Contains("Microsoft Learn", StringComparison.OrdinalIgnoreCase))
            .Where(s => !s.Contains("Sign in", StringComparison.OrdinalIgnoreCase))
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

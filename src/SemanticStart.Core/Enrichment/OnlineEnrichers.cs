using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Enrichment;

public sealed class WingetManifestEnricher : IEnricher
{
    public string Provider => "winget";
    public bool RequiresNetwork => true;
    public bool CanEnrich(Entity entity) => entity.Kind is EntityKind.Application or EntityKind.PackagedApp && !string.IsNullOrWhiteSpace(entity.DisplayName);

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        var winget = ResolveWinget();
        if (winget is null) return [];
        try
        {
            var query = !string.IsNullOrWhiteSpace(entity.Publisher) ? $"{entity.Publisher} {entity.DisplayName}" : entity.DisplayName;
            var search = await ProcessRunner.RunAsync(winget, ["search", "--source", "winget", "--query", query, "--disable-interactivity"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            var id = ParseWingetId(search.Output);
            if (id is null) return [];
            var show = await ProcessRunner.RunAsync(winget, ["show", "--source", "winget", "--id", id, "--disable-interactivity"], TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            var text = ExtractWingetFields(show.Output);
            return string.IsNullOrWhiteSpace(text) ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = true, Text = text, SourceUri = $"winget:{id}" }];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException) { return []; }
    }

    private static string? ResolveWinget()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try { var candidate = Path.Combine(dir.Trim(), "winget.exe"); if (File.Exists(candidate)) return candidate; } catch (Exception) { }
        }
        return null;
    }

    private static string? ParseWingetId(string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = Regex.Replace(line.Trim(), @"\s{2,}", "|");
            var cols = trimmed.Split('|');
            if (cols.Length >= 2 && cols[1].Contains('.', StringComparison.Ordinal) && !cols[1].Equals("Id", StringComparison.OrdinalIgnoreCase)) return cols[1].Trim();
        }
        return null;
    }

    private static string ExtractWingetFields(string output)
    {
        var keep = new[] { "Description:", "Short Description:", "Tags:", "Moniker:" };
        return string.Join(Environment.NewLine, output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(l => keep.Any(k => l.TrimStart().StartsWith(k, StringComparison.OrdinalIgnoreCase))).Select(l => l.Trim()));
    }
}

public sealed class LearnEnricher : IEnricher
{
    private readonly HttpClient _http;
    public string Provider => "learn";
    public bool RequiresNetwork => true;
    public LearnEnricher(HttpClient? http = null) => _http = http ?? new HttpClient();
    public bool CanEnrich(Entity entity) => entity.Kind is EntityKind.SettingsPage or EntityKind.ControlPanelApplet or EntityKind.ManagementConsole or EntityKind.OptionalFeature or EntityKind.SystemTool;

    public async Task<IReadOnlyList<EnrichmentDocument>> EnrichAsync(Entity entity, CancellationToken cancellationToken = default)
    {
        try
        {
            var term = entity.Kind == EntityKind.SettingsPage ? entity.LaunchTarget : entity.DisplayName + " Windows";
            var uri = "https://learn.microsoft.com/api/search?locale=en-us&$top=3&search=" + Uri.EscapeDataString(term);
            var json = await CachedHttp.GetStringAsync(_http, uri, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) return [];
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr)) return [];
            foreach (var result in arr.EnumerateArray())
            {
                var title = result.TryGetProperty("title", out var t) ? WebUtility.HtmlDecode(t.GetString()) : null;
                var desc = result.TryGetProperty("description", out var d) ? WebUtility.HtmlDecode(d.GetString()) : null;
                var url = result.TryGetProperty("url", out var u) ? u.GetString() : uri;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    var text = string.IsNullOrWhiteSpace(title) ? desc! : $"{title}: {desc}";
                    return [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = true, Text = Regex.Replace(text, @"\s+", " ").Trim(), SourceUri = url }];
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException) { }
        return [];
    }
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
        var uri = entity.RawMetadata.FirstOrDefault(kv => (kv.Key.Contains("url", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("website", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("homepage", StringComparison.OrdinalIgnoreCase)) && Uri.IsWellFormedUriString(kv.Value, UriKind.Absolute)).Value;
        if (string.IsNullOrWhiteSpace(uri)) return [];
        try
        {
            var html = await CachedHttp.GetStringAsync(_http, uri, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var match = Regex.Match(html, "<meta[^>]+name=[\"']description[\"'][^>]+content=[\"'](?<c>[^\"']+)[\"']", RegexOptions.IgnoreCase);
            if (!match.Success) match = Regex.Match(html, "<meta[^>]+content=[\"'](?<c>[^\"']+)[\"'][^>]+name=[\"']description[\"']", RegexOptions.IgnoreCase);
            var desc = match.Success ? WebUtility.HtmlDecode(match.Groups["c"].Value) : null;
            return string.IsNullOrWhiteSpace(desc) ? [] : [new EnrichmentDocument { EntityId = entity.Id, Provider = Provider, IsOnline = true, Text = Regex.Replace(desc, @"\s+", " ").Trim(), SourceUri = uri }];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException) { return []; }
    }
}

internal static class CachedHttp
{
    private static readonly SemaphoreSlim Throttle = new(4, 4);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private static readonly object DelayLock = new();

    public static async Task<string> GetStringAsync(HttpClient http, string uri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.EnrichmentCacheDirectory);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri))).ToLowerInvariant();
        var path = Path.Combine(AppPaths.EnrichmentCacheDirectory, key + ".txt");
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        await Throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan wait;
            lock (DelayLock)
            {
                var elapsed = DateTimeOffset.UtcNow - _lastRequest;
                wait = elapsed < TimeSpan.FromMilliseconds(150) ? TimeSpan.FromMilliseconds(150) - elapsed : TimeSpan.Zero;
                _lastRequest = DateTimeOffset.UtcNow + wait;
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SemanticStart/1.0 enrichment");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return string.Empty;
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(path, text, cts.Token).ConfigureAwait(false);
            return text;
        }
        finally { Throttle.Release(); }
    }
}

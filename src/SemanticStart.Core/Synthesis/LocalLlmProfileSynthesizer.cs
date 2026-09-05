using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Synthesis;

public enum LocalLlmMode
{
    Off,
    Auto,
    Custom
}

public sealed record LocalLlmOptions
{
    public const string DefaultModelName = "qwen2.5:1.5b";

    public LocalLlmMode Mode { get; init; } = LocalLlmMode.Auto;
    public string EndpointBaseUrl { get; init; } = string.Empty;
    public string ModelName { get; init; } = DefaultModelName;
}

public sealed record LocalLlmConnectionResult(
    bool Success,
    string? EndpointBaseUrl,
    string? ModelName,
    string Message);

public sealed class LocalLlmProfileSynthesizer : IProfileSynthesizer
{
    private readonly HttpClient _http;
    private readonly LocalLlmOptions _options;
    private readonly object _resolveGate = new();
    private Task<ResolvedEndpoint?>? _resolveTask;
    private ResolvedEndpoint? _resolved;

    public string Generator => _resolved is { } resolved ? $"llm:{resolved.ModelName}" : $"llm:{NormalizedModelName(_options)}";
    public bool IsAvailable => _options.Mode != LocalLlmMode.Off;

    public LocalLlmProfileSynthesizer(LocalLlmOptions? options = null, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _options = options ?? new LocalLlmOptions();
    }

    public static async Task<LocalLlmConnectionResult> TestConnectionAsync(LocalLlmOptions options, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        if (options.Mode == LocalLlmMode.Off)
            return new LocalLlmConnectionResult(false, null, null, "Local LLM synthesis is off.");

        var ownsClient = http is null;
        http ??= new HttpClient();
        try
        {
            var resolved = await ResolveEndpointAsync(http, options, cancellationToken).ConfigureAwait(false);
            return resolved is null
                ? new LocalLlmConnectionResult(false, null, null, "No OpenAI-compatible local LLM endpoint responded.")
                : new LocalLlmConnectionResult(true, resolved.Endpoint.ToString().TrimEnd('/'), resolved.ModelName, $"Connected to {resolved.Endpoint} using {resolved.ModelName}.");
        }
        finally
        {
            if (ownsClient)
                http.Dispose();
        }
    }

    public async Task<SynthesizedProfile> SynthesizeAsync(Entity entity, IReadOnlyList<EnrichmentDocument> documents, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (resolved is null) throw new InvalidOperationException("Local LLM synthesis is unavailable.");

        var prompt = BuildPrompt(entity, documents);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(resolved.Endpoint, "/v1/chat/completions"));
        request.Content = JsonContent(new
        {
            model = resolved.ModelName,
            messages = new[]
            {
                new { role = "system", content = "Return strict JSON only with keys summary, tasks, synonyms, category. No markdown." },
                new { role = "user", content = prompt }
            },
            temperature = 0.1,
            max_tokens = 500
        });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var responseJson = JsonDocument.Parse(body);
        var content = responseJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        var json = ExtractJsonObject(content);
        if (json is null) throw new JsonException("No JSON object in LLM response.");
        var dto = JsonSerializer.Deserialize<LlmDto>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (dto is null || string.IsNullOrWhiteSpace(dto.Summary)) throw new JsonException("Malformed LLM profile.");
        return new SynthesizedProfile
        {
            EntityId = entity.Id,
            Summary = dto.Summary.Trim(),
            Tasks = CleanList(dto.Tasks),
            Synonyms = CleanList(dto.Synonyms),
            Category = string.IsNullOrWhiteSpace(dto.Category) ? null : dto.Category.Trim(),
            Generator = Generator
        };
    }

    private Task<ResolvedEndpoint?> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_options.Mode == LocalLlmMode.Off)
            return Task.FromResult<ResolvedEndpoint?>(null);

        // Endpoint probing can touch a dead local service, so it is intentionally deferred until
        // synthesis instead of blocking app or CLI construction.
        cancellationToken.ThrowIfCancellationRequested();
        lock (_resolveGate)
        {
            _resolveTask ??= ResolveAndCacheAsync(_http, _options, CancellationToken.None);
            return _resolveTask.WaitAsync(cancellationToken);
        }
    }

    private async Task<ResolvedEndpoint?> ResolveAndCacheAsync(HttpClient http, LocalLlmOptions options, CancellationToken cancellationToken)
    {
        _resolved = await ResolveEndpointAsync(http, options, cancellationToken).ConfigureAwait(false);
        return _resolved;
    }

    private static HttpContent JsonContent(object payload) => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string BuildPrompt(Entity entity, IReadOnlyList<EnrichmentDocument> documents)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Synthesize semantic launcher metadata for this Windows entity.");
        sb.AppendLine($"Name: {entity.DisplayName}");
        sb.AppendLine($"Kind: {entity.Kind}");
        sb.AppendLine($"Launch target: {entity.LaunchTarget}");
        if (!string.IsNullOrWhiteSpace(entity.Publisher)) sb.AppendLine($"Publisher: {entity.Publisher}");
        foreach (var kv in entity.RawMetadata.Take(12)) sb.AppendLine($"Metadata {kv.Key}: {kv.Value}");
        foreach (var doc in documents.Take(8))
        {
            var text = doc.Text.Length > 1500 ? doc.Text[..1500] : doc.Text;
            sb.AppendLine($"Document from {doc.Provider}: {text}");
        }
        sb.AppendLine("Return JSON: {\"summary\":\"one sentence\",\"tasks\":[\"user intent phrase\"],\"synonyms\":[\"aliases\"],\"category\":\"broad category\"}");
        return sb.ToString();
    }

    private static string? ExtractJsonObject(string content)
    {
        content = Regex.Replace(content.Trim(), "^```(?:json)?|```$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Multiline).Trim();
        var start = content.IndexOf('{');
        if (start < 0) return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < content.Length; i++)
        {
            var ch = content[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }
            if (ch == '"') inString = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return content[start..(i + 1)];
        }
        return null;
    }

    private static IReadOnlyList<string> CleanList(IEnumerable<string>? values) => values?.Select(v => Regex.Replace(v.Trim(), @"\s+", " ")).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray() ?? [];

    private static async Task<ResolvedEndpoint?> ResolveEndpointAsync(HttpClient http, LocalLlmOptions options, CancellationToken cancellationToken)
    {
        var endpoints = options.Mode == LocalLlmMode.Custom
            ? CustomEndpoints(options.EndpointBaseUrl)
            : await AutoEndpointsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var endpoint in endpoints.DistinctBy(e => e.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var models = await ProbeModelsAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
            if (models.Count == 0)
                continue;

            return new ResolvedEndpoint(endpoint, SelectModel(options, models));
        }

        return null;
    }

    private static string SelectModel(LocalLlmOptions options, IReadOnlyList<string> models)
    {
        var preferred = NormalizedModelName(options);
        if (options.Mode == LocalLlmMode.Custom)
            return preferred;

        return models.FirstOrDefault(m => m.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? models.FirstOrDefault(m => m.Contains("1.5b", StringComparison.OrdinalIgnoreCase) || m.Contains("1b", StringComparison.OrdinalIgnoreCase))
            ?? models[0];
    }

    private static string NormalizedModelName(LocalLlmOptions options) =>
        string.IsNullOrWhiteSpace(options.ModelName) ? LocalLlmOptions.DefaultModelName : options.ModelName.Trim();

    private static IEnumerable<Uri> CustomEndpoints(string endpointBaseUrl)
    {
        if (Uri.TryCreate(endpointBaseUrl, UriKind.Absolute, out var endpoint)
            && (endpoint.Scheme == Uri.UriSchemeHttp || endpoint.Scheme == Uri.UriSchemeHttps))
        {
            yield return NormalizeEndpoint(endpoint);
        }
    }

    private static async Task<IEnumerable<Uri>> AutoEndpointsAsync(CancellationToken cancellationToken)
    {
        var endpoints = new List<Uri>();
        endpoints.AddRange(await DiscoverFoundryEndpointsAsync(cancellationToken).ConfigureAwait(false));
        endpoints.Add(new Uri("http://localhost:59321"));
        endpoints.Add(new Uri("http://127.0.0.1:59321"));
        endpoints.Add(new Uri("http://localhost:11434"));
        endpoints.Add(new Uri("http://localhost:1234"));
        endpoints.Add(new Uri("http://localhost:8080"));
        return endpoints.Select(NormalizeEndpoint);
    }

    private static Uri NormalizeEndpoint(Uri endpoint)
    {
        var builder = new UriBuilder(endpoint) { Path = string.Empty, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    private static async Task<IReadOnlyList<string>> ProbeModelsAsync(HttpClient http, Uri endpoint, CancellationToken cancellationToken)
    {
        var openAiModels = await ProbeOpenAiModelsAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
        if (openAiModels.Count > 0)
            return openAiModels;

        return await ProbeOllamaTagsAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> ProbeOpenAiModelsAsync(HttpClient http, Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            using var response = await http.GetAsync(new Uri(endpoint, "/v1/models"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            return data.EnumerateArray()
                .Select(model => model.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<string>> ProbeOllamaTagsAsync(HttpClient http, Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            using var response = await http.GetAsync(new Uri(endpoint, "/api/tags"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return [];

            return models.EnumerateArray()
                .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    private static async Task<IEnumerable<Uri>> DiscoverFoundryEndpointsAsync(CancellationToken cancellationToken)
    {
        var foundry = ResolveOnPath("foundry.exe") ?? ResolveOnPath("foundry");
        if (foundry is null) return [];
        try
        {
            var result = await ProcessRunner.RunAsync(foundry, ["service", "status"], TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return Regex.Matches(result.Output, @"https?://(?:localhost|127\.0\.0\.1):\d+").Select(m => new Uri(m.Value)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException) { return []; }
    }

    private static string? ResolveOnPath(string fileName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try { var path = Path.Combine(dir.Trim(), fileName); if (File.Exists(path)) return path; } catch (Exception) { }
        }
        return null;
    }

    private sealed record LlmDto(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("tasks")] string[]? Tasks,
        [property: JsonPropertyName("synonyms")] string[]? Synonyms,
        [property: JsonPropertyName("category")] string? Category);

    private sealed record ResolvedEndpoint(Uri Endpoint, string ModelName);
}

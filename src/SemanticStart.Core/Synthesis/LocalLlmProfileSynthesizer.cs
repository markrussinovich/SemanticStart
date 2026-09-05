using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Enrichment;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Synthesis;

public sealed class LocalLlmProfileSynthesizer : IProfileSynthesizer
{
    private readonly HttpClient _http;
    private readonly Uri? _endpoint;
    private readonly string _modelName;
    public string Generator => $"llm:{_modelName}";
    public bool IsAvailable => _endpoint is not null;

    public LocalLlmProfileSynthesizer(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        (_endpoint, _modelName) = ProbeFoundryAsync(_http).GetAwaiter().GetResult();
    }

    public async Task<SynthesizedProfile> SynthesizeAsync(Entity entity, IReadOnlyList<EnrichmentDocument> documents, CancellationToken cancellationToken = default)
    {
        if (_endpoint is null) throw new InvalidOperationException("Foundry Local is unavailable.");
        var prompt = BuildPrompt(entity, documents);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "/v1/chat/completions"));
        request.Content = JsonContent(new
        {
            model = _modelName,
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

    private static async Task<(Uri? Endpoint, string Model)> ProbeFoundryAsync(HttpClient http)
    {
        var endpoints = new List<Uri>();
        endpoints.AddRange(await DiscoverFoundryEndpointsAsync().ConfigureAwait(false));
        endpoints.Add(new Uri("http://localhost:59321"));
        endpoints.Add(new Uri("http://127.0.0.1:59321"));
        foreach (var endpoint in endpoints.DistinctBy(e => e.ToString()))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                using var response = await http.GetAsync(new Uri(endpoint, "/v1/models"), cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var model = doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0 && data[0].TryGetProperty("id", out var id)
                    ? id.GetString() ?? "local"
                    : "local";
                return (endpoint, model);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
        }
        return (null, "unavailable");
    }

    private static async Task<IEnumerable<Uri>> DiscoverFoundryEndpointsAsync()
    {
        var foundry = ResolveOnPath("foundry.exe") ?? ResolveOnPath("foundry");
        if (foundry is null) return [];
        try
        {
            var result = await ProcessRunner.RunAsync(foundry, ["service", "status"], TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            return Regex.Matches(result.Output, @"https?://(?:localhost|127\.0\.0\.1):\d+").Select(m => new Uri(m.Value)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return []; }
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
}

using System.Diagnostics;
using System.Globalization;
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

public enum LocalLlmRuntime
{
    FoundryLocal,
    Ollama,
    LmStudio,
    OpenAiCompatible,
    Curated
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

public sealed record LocalLlmCatalogItem
{
    public required string ModelName { get; init; }
    public required string DisplayName { get; init; }
    public required LocalLlmRuntime Runtime { get; init; }
    public required string RuntimeName { get; init; }
    public string? EndpointBaseUrl { get; init; }
    public bool IsReady { get; init; }
    public bool CanDownload { get; init; }
    public string DownloadSize { get; init; } = "size varies";
    public string RamRequirement { get; init; } = "RAM varies";
    public string BestFor { get; init; } = "General local synthesis.";
    public string Source { get; init; } = "Detected";
    public bool IsCurated { get; init; }

    public string PickerLabel =>
        $"{DisplayName} · {RuntimeName} · {(IsReady ? "Ready" : "Download required")} · {DownloadSize} · {RamRequirement}";
}

public sealed record LocalLlmCatalog(
    IReadOnlyList<LocalLlmCatalogItem> Models,
    string StatusMessage,
    string? DetectedRuntime,
    string? DetectedEndpointBaseUrl);

public sealed class LocalLlmProfileSynthesizer : IProfileSynthesizer
{
    private readonly HttpClient _http;
    private readonly LocalLlmOptions _options;
    private readonly object _resolveGate = new();
    private Task<ResolvedEndpoint?>? _resolveTask;
    private ResolvedEndpoint? _resolved;
    private int _recoveryAttempts;
    private const int MaxRecoveryAttempts = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private readonly SemaphoreSlim _requestGate = new(1, 1);

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
            if (resolved is null)
                return new LocalLlmConnectionResult(false, null, null, "No OpenAI-compatible local LLM endpoint responded.");

            var completion = await TestCompletionAsync(http, resolved, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(completion))
                return new LocalLlmConnectionResult(false, resolved.Endpoint.ToString().TrimEnd('/'), resolved.ModelName, $"Connected to {resolved.Endpoint}, but {resolved.ModelName} did not complete a test prompt.");

            if (!IsCoherentProbeResponse(completion))
                return new LocalLlmConnectionResult(false, resolved.Endpoint.ToString().TrimEnd('/'), resolved.ModelName, $"{resolved.ModelName} responded but did not follow the test prompt, which means a broken or mismatched build: \"{Excerpt(completion)}\". Pick another model or runtime.");

            return new LocalLlmConnectionResult(true, resolved.Endpoint.ToString().TrimEnd('/'), resolved.ModelName, $"Generated a test completion with {resolved.ModelName}.");
        }
        finally
        {
            if (ownsClient)
                http.Dispose();
        }
    }

    public static async Task<LocalLlmCatalog> DiscoverCatalogAsync(HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        var ownsClient = http is null;
        http ??= new HttpClient();
        try
        {
            var foundryEndpoint = (await DiscoverFoundryEndpointsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault()?.ToString().TrimEnd('/');
            var foundry = await DiscoverFoundryModelsAsync(foundryEndpoint, cancellationToken).ConfigureAwait(false);
            var ollama = await DiscoverOllamaModelsAsync(http, new Uri("http://localhost:11434"), cancellationToken).ConfigureAwait(false);
            var lmStudio = await DiscoverOpenAiModelsAsync(http, new Uri("http://localhost:1234"), LocalLlmRuntime.LmStudio, "LM Studio", cancellationToken).ConfigureAwait(false);
            var generic = await DiscoverOpenAiModelsAsync(http, new Uri("http://localhost:8080"), LocalLlmRuntime.OpenAiCompatible, "OpenAI-compatible", cancellationToken).ConfigureAwait(false);

            var models = MergeCatalogItems(foundry.Concat(ollama).Concat(lmStudio).Concat(generic)).ToArray();
            if (models.Length == 0)
            {
                var foundryInstalled = ResolveFoundryCli() is not null;
                var fallback = CuratedModels(null, foundryInstalled: foundryInstalled, ollamaAvailable: false).ToArray();
                return new LocalLlmCatalog(
                    fallback,
                    foundryInstalled
                        ? "Foundry Local is installed, but its model catalog did not respond quickly. Try Refresh, or download one of these recommended models."
                        : "No local LLM runtime was detected. Install Foundry Local (recommended), then refresh and download one of these small models.",
                    foundryInstalled ? "Foundry Local" : null,
                    null);
            }

            var firstReady = models.FirstOrDefault(m => m.IsReady);
            return new LocalLlmCatalog(
                models,
                firstReady is null
                    ? "Models were found, but none are downloaded yet. Pick one and download it before rebuilding the index."
                    : $"{firstReady.RuntimeName} detected at {firstReady.EndpointBaseUrl ?? "local runtime"}; {models.Count(m => m.IsReady)} model(s) ready.",
                firstReady?.RuntimeName ?? models[0].RuntimeName,
                firstReady?.EndpointBaseUrl ?? models[0].EndpointBaseUrl);
        }
        finally
        {
            if (ownsClient)
                http.Dispose();
        }
    }

    public static async Task<LocalLlmConnectionResult> DownloadModelAsync(LocalLlmCatalogItem item, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(0);
        if (item.Runtime == LocalLlmRuntime.FoundryLocal)
            return await DownloadFoundryModelAsync(item, progress, cancellationToken).ConfigureAwait(false);
        if (item.Runtime == LocalLlmRuntime.Ollama && !string.IsNullOrWhiteSpace(item.EndpointBaseUrl))
            return await DownloadOllamaModelAsync(item, progress, cancellationToken).ConfigureAwait(false);

        return new LocalLlmConnectionResult(false, item.EndpointBaseUrl, item.ModelName, $"Automatic download is not available for {item.RuntimeName}.");
    }

    public async Task<SynthesizedProfile> SynthesizeAsync(Entity entity, IReadOnlyList<EnrichmentDocument> documents, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SynthesizeOnceAsync(entity, documents, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            // A local runtime can exit mid-build: Foundry Local's server terminated part way through
            // a 468-entity index and every remaining entity silently degraded to heuristics, because
            // the resolved endpoint is cached once and each failure looks like an ordinary synthesis
            // error. Drop the cached endpoint so the next attempt restarts and reloads the runtime.
            if (!TryInvalidateEndpoint())
                throw;

            return await SynthesizeOnceAsync(entity, documents, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Small models drop a closing brace or trail commentary often enough to matter: this
            // accounted for most of the entities that still fell back on an otherwise healthy run.
            return await SynthesizeOnceAsync(entity, documents, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryInvalidateEndpoint()
    {
        lock (_resolveGate)
        {
            if (_recoveryAttempts >= MaxRecoveryAttempts)
                return false;

            _recoveryAttempts++;
            _resolveTask = null;
            _resolved = null;
            return true;
        }
    }

    private async Task<SynthesizedProfile> SynthesizeOnceAsync(Entity entity, IReadOnlyList<EnrichmentDocument> documents, CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (resolved is null) throw new InvalidOperationException("Local LLM synthesis is unavailable.");

        // A single small local model gains nothing from concurrency and loses a great deal to it:
        // the index pipeline enriches entities in parallel, and the resulting simultaneous
        // completions pushed every request past its timeout, so all but a handful of entities fell
        // back to heuristics. Requests are serialised and given a generous budget instead.
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            return await SendSynthesisRequestAsync(entity, documents, resolved, timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<SynthesizedProfile> SendSynthesisRequestAsync(Entity entity, IReadOnlyList<EnrichmentDocument> documents, ResolvedEndpoint resolved, CancellationToken cancellationToken)
    {
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
            Details = ProfileText.Details(documents),
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
        sb.AppendLine("You are writing search metadata for a Windows launcher.");
        sb.AppendLine($"Name: {entity.DisplayName}");
        sb.AppendLine($"Kind: {entity.Kind}");
        sb.AppendLine($"Launch target: {entity.LaunchTarget}");
        if (!string.IsNullOrWhiteSpace(entity.Publisher)) sb.AppendLine($"Publisher: {entity.Publisher}");
        foreach (var kv in entity.RawMetadata.Take(12)) sb.AppendLine($"Metadata {kv.Key}: {kv.Value}");
        foreach (var doc in documents.Take(8))
        {
            // Online documents are retrieved by title match and are often directory pages that
            // describe everything except this entity. A reference table has no sentence structure,
            // which is what the prose test detects; local documents are exempt because a captured
            // help screen is a usage listing by nature and is still the best evidence there is
            // about a command-line tool.
            if (doc.IsOnline && !ProfileText.IsProse(doc.Text))
                continue;

            var text = doc.Text.Length > 1500 ? doc.Text[..1500] : doc.Text;
            sb.AppendLine($"Document from {doc.Provider}: {text}");
        }

        // Documents are retrieved by search and are frequently about something else entirely, so the
        // model is told to discard them rather than summarise them. The task list is where most of
        // the value is: it must be phrased the way a user types into a search box, because those
        // words are exactly what the vendor's own documentation never contains. Concrete example
        // phrases are deliberately absent - supplying them caused small models to copy them
        // verbatim, and Process Explorer confidently claimed it could free up disk space.
        //
        // The instructions are kept deliberately flat. Splitting the list into "plain goals" and
        // "symptoms" was measurably worse (35/41 against 36/41): a 1.5B model answers a multi-part
        // instruction with long instructional sentences that name the entity in every entry, which
        // is the exact wording a lost user cannot produce. One extra constraint is affordable; a
        // second structure on top of it is not.
        sb.AppendLine();
        sb.AppendLine("Some documents may be irrelevant. Ignore any document that is not about this specific entity, and rely on what you already know instead.");
        sb.AppendLine("summary: one sentence describing what it does for the user. Never restate only the name.");
        sb.AppendLine("tasks: 6-10 short phrases someone would type into a search box when they want this. Each phrase is a goal in everyday words, starting with a verb, and must be something this entity genuinely does. Do not invent capabilities it lacks. Do not write step-by-step instructions or refer to buttons, menus, or clicking.");
        sb.AppendLine("Never use this entity's name inside a task phrase, and include the problem or symptom that brings someone here when they do not know the name.");
        sb.AppendLine("synonyms: other names, abbreviations, and executable names people call it.");
        sb.AppendLine("Return only JSON: {\"summary\":\"...\",\"tasks\":[\"...\"],\"synonyms\":[\"...\"],\"category\":\"...\"}");
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

    private static IReadOnlyList<string> CleanList(IEnumerable<string>? values) => ProfileText.Distinctive(values, 12);

    /// <summary>
    /// True when a reply to the "Reply with exactly: OK" probe shows the model is actually
    /// following instructions, rather than merely returning bytes.
    ///
    /// Checking only that a response was non-empty is not enough. A Foundry Local CUDA build of
    /// qwen2.5-7b on the development machine reported itself ready and answered every request, but
    /// emitted token salad - "\u0e40\u0e02\u0e49\u0e32\u0e21\u0e32yenyenyen a a a laptop laptop's's power power" - for any prompt.
    /// Connection testing passed, so the model was selectable in Settings, and the damage only
    /// became visible after a nine-minute index run had written the garbage into every profile.
    /// A model too broken to say OK cannot write usable descriptions, and failing here costs a
    /// second where failing later costs the whole index.
    /// </summary>
    public static bool IsCoherentProbeResponse(string? completion)
    {
        if (string.IsNullOrWhiteSpace(completion))
            return false;

        // A compliant answer is "OK", possibly quoted, punctuated, or wrapped in a short pleasantry.
        // Anything much longer is not following an instruction this explicit, and the degenerate
        // outputs seen in practice were both long and repetitive.
        var trimmed = completion.Trim();
        return trimmed.Length <= MaxProbeResponseLength
            && trimmed.Contains("ok", StringComparison.OrdinalIgnoreCase);
    }

    private const int MaxProbeResponseLength = 40;

    private static string Excerpt(string text)
    {
        var collapsed = text.Trim().ReplaceLineEndings(" ");
        return collapsed.Length <= 60 ? collapsed : collapsed[..60] + "...";
    }

    private static async Task<string?> TestCompletionAsync(HttpClient http, ResolvedEndpoint resolved, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(resolved.Endpoint, "/v1/chat/completions"));
            request.Content = JsonContent(new
            {
                model = resolved.ModelName,
                messages = new[] { new { role = "user", content = "Reply with exactly: OK" } },
                temperature = 0,
                max_tokens = 8,
                stream = false
            });

            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var responseJson = JsonDocument.Parse(body);
            return responseJson.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IEnumerable<LocalLlmCatalogItem> MergeCatalogItems(IEnumerable<LocalLlmCatalogItem> items) =>
        items
            .GroupBy(i => $"{i.Runtime}|{i.ModelName}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var best = g.OrderByDescending(i => i.IsReady).ThenBy(i => i.IsCurated).First();
                return best with { IsReady = g.Any(i => i.IsReady) || best.IsReady };
            })
            .OrderBy(i => i.Runtime == LocalLlmRuntime.FoundryLocal ? 0 : i.Runtime == LocalLlmRuntime.Ollama ? 1 : 2)
            .ThenByDescending(i => i.IsReady)
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<LocalLlmCatalogItem>> DiscoverFoundryModelsAsync(string? endpointBaseUrl, CancellationToken cancellationToken)
    {
        var foundry = ResolveFoundryCli();
        if (foundry is null)
            return [];

        try
        {
            var list = await ProcessRunner.RunAsync(foundry, ["model", "list"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var cache = await ProcessRunner.RunAsync(foundry, ["cache", "list"], TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            if (list.ExitCode != 0 && string.IsNullOrWhiteSpace(list.Output))
                return [];

            var parsed = ParseFoundryModelList(list.Output)
                .Select(name => CreateDetectedItem(
                    name,
                    LocalLlmRuntime.FoundryLocal,
                    "Foundry Local",
                    endpointBaseUrl,
                    cache.Output.Contains(name, StringComparison.OrdinalIgnoreCase),
                    canDownload: true,
                    source: "foundry model list"))
                .ToArray();

            return parsed.Length > 0 ? parsed : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<LocalLlmCatalogItem>> DiscoverOllamaModelsAsync(HttpClient http, Uri endpoint, CancellationToken cancellationToken)
    {
        var installed = await ProbeOllamaTagsWithSizesAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
        if (installed is null)
            return [];

        var endpointText = endpoint.ToString().TrimEnd('/');
        var detected = installed.Select(model => CreateDetectedItem(
            model.Name,
            LocalLlmRuntime.Ollama,
            "Ollama",
            endpointText,
            isReady: true,
            canDownload: true,
            source: "/api/tags",
            downloadSize: model.SizeBytes is { } size ? FormatBytes(size) : null));

        return detected.Concat(CuratedModels(endpointText, foundryInstalled: false, ollamaAvailable: true)).ToArray();
    }

    private static async Task<IReadOnlyList<LocalLlmCatalogItem>> DiscoverOpenAiModelsAsync(
        HttpClient http,
        Uri endpoint,
        LocalLlmRuntime runtime,
        string runtimeName,
        CancellationToken cancellationToken)
    {
        var models = await ProbeOpenAiModelsAsync(http, endpoint, cancellationToken).ConfigureAwait(false);
        var endpointText = endpoint.ToString().TrimEnd('/');
        return models
            .Select(model => CreateDetectedItem(model, runtime, runtimeName, endpointText, isReady: true, canDownload: false, source: "/v1/models"))
            .ToArray();
    }

    private static LocalLlmCatalogItem CreateDetectedItem(
        string modelName,
        LocalLlmRuntime runtime,
        string runtimeName,
        string? endpointBaseUrl,
        bool isReady,
        bool canDownload,
        string source,
        string? downloadSize = null)
    {
        var curated = CuratedModels(endpointBaseUrl, foundryInstalled: runtime == LocalLlmRuntime.FoundryLocal, ollamaAvailable: runtime == LocalLlmRuntime.Ollama)
            .FirstOrDefault(c => c.Runtime == runtime && c.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));

        return new LocalLlmCatalogItem
        {
            ModelName = modelName,
            DisplayName = curated?.DisplayName ?? modelName,
            Runtime = runtime,
            RuntimeName = runtimeName,
            EndpointBaseUrl = endpointBaseUrl,
            IsReady = isReady,
            CanDownload = canDownload,
            DownloadSize = downloadSize ?? curated?.DownloadSize ?? "size unknown",
            RamRequirement = curated?.RamRequirement ?? "depends on quantization",
            BestFor = curated?.BestFor ?? "Detected local model.",
            Source = source,
            IsCurated = false,
        };
    }

    private static IEnumerable<LocalLlmCatalogItem> CuratedModels(string? endpointBaseUrl, bool foundryInstalled, bool ollamaAvailable)
    {
        if (foundryInstalled || !ollamaAvailable)
        {
            yield return Curated("qwen2.5-0.5b", "Qwen2.5 0.5B", LocalLlmRuntime.FoundryLocal, "Foundry Local", endpointBaseUrl, foundryInstalled, "≈400 MB", "≈2 GB RAM", "Fastest CPU setup and smoke tests.");
            yield return Curated("qwen2.5-1.5b", "Qwen2.5 1.5B", LocalLlmRuntime.FoundryLocal, "Foundry Local", endpointBaseUrl, foundryInstalled, "≈1.0 GB", "≈4 GB RAM", "Best default for fast JSON metadata synthesis.");
            yield return Curated("llama-3.2-1b", "Llama 3.2 1B", LocalLlmRuntime.FoundryLocal, "Foundry Local", endpointBaseUrl, foundryInstalled, "≈1.3 GB", "≈4 GB RAM", "Very small multilingual instruction following.");
            yield return Curated("phi-3.5-mini", "Phi-3.5 Mini", LocalLlmRuntime.FoundryLocal, "Foundry Local", endpointBaseUrl, foundryInstalled, "≈2.2 GB", "≈6 GB RAM", "Higher quality reasoning when latency is acceptable.");
        }

        if (ollamaAvailable || !foundryInstalled)
        {
            yield return Curated("qwen2.5:0.5b", "Qwen2.5 0.5B", LocalLlmRuntime.Ollama, "Ollama", ollamaAvailable ? endpointBaseUrl ?? "http://localhost:11434" : null, ollamaAvailable, "398 MB", "≈2 GB RAM", "Fastest CPU setup and smoke tests.");
            yield return Curated("qwen2.5:1.5b", "Qwen2.5 1.5B", LocalLlmRuntime.Ollama, "Ollama", ollamaAvailable ? endpointBaseUrl ?? "http://localhost:11434" : null, ollamaAvailable, "986 MB", "≈4 GB RAM", "Best default for fast JSON metadata synthesis.");
            yield return Curated("llama3.2:1b", "Llama 3.2 1B", LocalLlmRuntime.Ollama, "Ollama", ollamaAvailable ? endpointBaseUrl ?? "http://localhost:11434" : null, ollamaAvailable, "≈1.3 GB", "≈4 GB RAM", "Very small multilingual instruction following.");
            yield return Curated("llama3.2:3b", "Llama 3.2 3B", LocalLlmRuntime.Ollama, "Ollama", ollamaAvailable ? endpointBaseUrl ?? "http://localhost:11434" : null, ollamaAvailable, "≈2.0 GB", "≈6 GB RAM", "Better quality while still practical on CPU.");
            yield return Curated("gemma2:2b", "Gemma 2 2B", LocalLlmRuntime.Ollama, "Ollama", ollamaAvailable ? endpointBaseUrl ?? "http://localhost:11434" : null, ollamaAvailable, "≈1.6 GB", "≈4 GB RAM", "Balanced summarization and categorization.");
            yield return Curated("phi3.5:mini", "Phi-3.5 Mini", LocalLlmRuntime.Ollama, "Ollama", ollamaAvailable ? endpointBaseUrl ?? "http://localhost:11434" : null, ollamaAvailable, "≈2.2 GB", "≈6 GB RAM", "Higher quality reasoning when latency is acceptable.");
        }
    }

    private static LocalLlmCatalogItem Curated(
        string modelName,
        string displayName,
        LocalLlmRuntime runtime,
        string runtimeName,
        string? endpointBaseUrl,
        bool canDownload,
        string downloadSize,
        string ramRequirement,
        string bestFor) => new()
        {
            ModelName = modelName,
            DisplayName = displayName,
            Runtime = runtime,
            RuntimeName = runtimeName,
            EndpointBaseUrl = endpointBaseUrl,
            IsReady = false,
            CanDownload = canDownload,
            DownloadSize = downloadSize,
            RamRequirement = ramRequirement,
            BestFor = bestFor,
            Source = "Curated small-model list",
            IsCurated = true,
        };

    private static async Task<ResolvedEndpoint?> ResolveEndpointAsync(HttpClient http, LocalLlmOptions options, CancellationToken cancellationToken)
    {
        var endpointCandidates = options.Mode == LocalLlmMode.Custom
            ? CustomEndpoints(options.EndpointBaseUrl).Select(e => new EndpointCandidate(e, LocalLlmRuntime.OpenAiCompatible))
            : await AutoEndpointsAsync(cancellationToken).ConfigureAwait(false);

        var probes = new List<(EndpointCandidate Candidate, IReadOnlyList<string> Models)>();

        foreach (var candidate in endpointCandidates.DistinctBy(e => e.Endpoint.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var models = await ProbeModelsAsync(http, candidate.Endpoint, cancellationToken).ConfigureAwait(false);
            if (models.Count == 0)
                continue;

            probes.Add((candidate, models));
        }

        if (probes.Count == 0)
            return null;

        var preferred = NormalizedModelName(options);
        if (options.Mode != LocalLlmMode.Custom)
        {
            var preferredProbe = probes.FirstOrDefault(p => p.Models.Any(m => m.Equals(preferred, StringComparison.OrdinalIgnoreCase)));
            if (preferredProbe.Models is not null)
            {
                var exact = new ResolvedEndpoint(preferredProbe.Candidate.Endpoint, preferred, preferredProbe.Candidate.Runtime);
                await EnsureModelLoadedAsync(http, exact, cancellationToken).ConfigureAwait(false);
                return exact;
            }
        }

        var selected = probes[0];
        var resolved = new ResolvedEndpoint(selected.Candidate.Endpoint, SelectModel(options, selected.Models), selected.Candidate.Runtime);
        await EnsureModelLoadedAsync(http, resolved, cancellationToken).ConfigureAwait(false);
        return resolved;
    }

    /// <summary>
    /// Foundry Local advertises every downloaded model through <c>/v1/models</c> but refuses
    /// completions until one has been explicitly loaded into memory, answering "Model is not
    /// loaded" instead. Index builds fall back to heuristics on any synthesis failure, so this
    /// presented as the LLM silently never running. Loading takes about ten seconds and only has to
    /// happen once per session, so it is done here rather than asking the user to run a CLI command.
    /// </summary>
    private static async Task<bool> EnsureModelLoadedAsync(HttpClient http, ResolvedEndpoint resolved, CancellationToken cancellationToken)
    {
        if (resolved.Runtime != LocalLlmRuntime.FoundryLocal)
            return true;

        if (!string.IsNullOrWhiteSpace(await TestCompletionAsync(http, resolved, cancellationToken).ConfigureAwait(false)))
            return true;

        var foundry = ResolveFoundryCli();
        if (foundry is null)
            return false;

        var alias = await FoundryAliasAsync(http, resolved, cancellationToken).ConfigureAwait(false) ?? resolved.ModelName;
        try
        {
            var result = await ProcessRunner.RunAsync(foundry, ["model", "load", alias], TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException) { return false; }
    }

    /// <summary>
    /// Maps the served model id (<c>qwen2.5-1.5b-instruct-trtrtx-gpu</c>) back to the alias the
    /// Foundry CLI accepts (<c>qwen2.5-1.5b</c>), which the catalog exposes as <c>parent</c>.
    /// </summary>
    private static async Task<string?> FoundryAliasAsync(HttpClient http, ResolvedEndpoint resolved, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var json = await http.GetStringAsync(new Uri(resolved.Endpoint, "/v1/models"), timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var model in data.EnumerateArray())
            {
                if (!model.TryGetProperty("id", out var id) || !string.Equals(id.GetString(), resolved.ModelName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (model.TryGetProperty("parent", out var parent) && !string.IsNullOrWhiteSpace(parent.GetString()))
                    return parent.GetString();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
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

    private static async Task<IEnumerable<EndpointCandidate>> AutoEndpointsAsync(CancellationToken cancellationToken)
    {
        var endpoints = new List<EndpointCandidate>();
        endpoints.AddRange((await DiscoverFoundryEndpointsAsync(cancellationToken).ConfigureAwait(false)).Select(e => new EndpointCandidate(e, LocalLlmRuntime.FoundryLocal)));
        endpoints.Add(new EndpointCandidate(new Uri("http://localhost:59321"), LocalLlmRuntime.FoundryLocal));
        endpoints.Add(new EndpointCandidate(new Uri("http://127.0.0.1:59321"), LocalLlmRuntime.FoundryLocal));
        endpoints.Add(new EndpointCandidate(new Uri("http://localhost:11434"), LocalLlmRuntime.Ollama));
        endpoints.Add(new EndpointCandidate(new Uri("http://localhost:1234"), LocalLlmRuntime.LmStudio));
        endpoints.Add(new EndpointCandidate(new Uri("http://localhost:8080"), LocalLlmRuntime.OpenAiCompatible));
        return endpoints.Select(e => e with { Endpoint = NormalizeEndpoint(e.Endpoint) });
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

    private static async Task<IReadOnlyList<(string Name, long? SizeBytes)>?> ProbeOllamaTagsWithSizesAsync(HttpClient http, Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
            using var response = await http.GetAsync(new Uri(endpoint, "/api/tags"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                return null;

            return models.EnumerateArray()
                .Select(model =>
                {
                    var name = model.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    long? size = model.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : null;
                    return (Name: name, SizeBytes: size);
                })
                .Where(model => !string.IsNullOrWhiteSpace(model.Name))
                .Select(model => (model.Name!, model.SizeBytes))
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<LocalLlmConnectionResult> DownloadOllamaModelAsync(LocalLlmCatalogItem item, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(item.EndpointBaseUrl!), "/api/pull"));
            request.Content = JsonContent(new { name = item.ModelName, stream = true });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new LocalLlmConnectionResult(false, item.EndpointBaseUrl, item.ModelName, $"Ollama pull failed with HTTP {(int)response.StatusCode}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("total", out var totalElement)
                    && root.TryGetProperty("completed", out var completedElement)
                    && totalElement.TryGetInt64(out var total)
                    && completedElement.TryGetInt64(out var completed)
                    && total > 0)
                {
                    progress?.Report(Math.Clamp((double)completed / total, 0, 1));
                }

                if (root.TryGetProperty("error", out var error))
                    return new LocalLlmConnectionResult(false, item.EndpointBaseUrl, item.ModelName, error.GetString() ?? "Ollama pull failed.");
            }

            progress?.Report(1);
            return new LocalLlmConnectionResult(true, item.EndpointBaseUrl, item.ModelName, $"Downloaded {item.ModelName} with Ollama.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or IOException)
        {
            return new LocalLlmConnectionResult(false, item.EndpointBaseUrl, item.ModelName, $"Ollama download failed: {ex.Message}");
        }
    }

    private static async Task<LocalLlmConnectionResult> DownloadFoundryModelAsync(LocalLlmCatalogItem item, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var foundry = ResolveFoundryCli();
        if (foundry is null)
            return new LocalLlmConnectionResult(false, item.EndpointBaseUrl, item.ModelName, "Foundry Local is not installed.");

        try
        {
            var result = await RunProcessWithProgressAsync(foundry, ["model", "download", item.ModelName], progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(result.ExitCode == 0 ? 1 : 0);
            return result.ExitCode == 0
                ? new LocalLlmConnectionResult(true, item.EndpointBaseUrl, item.ModelName, $"Downloaded {item.ModelName} with Foundry Local.")
                : new LocalLlmConnectionResult(false, item.EndpointBaseUrl, item.ModelName, $"Foundry download failed: {TrimOutput(result.Output)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException)
        {
            return new LocalLlmConnectionResult(false, item.EndpointBaseUrl, item.ModelName, $"Foundry download failed: {ex.Message}");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunProcessWithProgressAsync(string fileName, IReadOnlyList<string> args, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => UpdateProgress(e.Data);
        process.ErrorDataReceived += (_, e) => UpdateProgress(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, output.ToString());

        void UpdateProgress(string? line)
        {
            if (line is null)
                return;

            output.AppendLine(line);
            var percent = Regex.Match(line, @"(?<percent>\d+(?:\.\d+)?)\s*%");
            if (percent.Success && double.TryParse(percent.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                progress?.Report(Math.Clamp(value / 100, 0, 1));
        }
    }

    private static string TrimOutput(string output)
    {
        output = Regex.Replace(output.Trim(), @"\s+", " ");
        return output.Length <= 240 ? output : output[..240] + "...";
    }

    private static async Task<IEnumerable<Uri>> DiscoverFoundryEndpointsAsync(CancellationToken cancellationToken)
    {
        var foundry = ResolveFoundryCli();
        if (foundry is null) return [];
        try
        {
            var result = await ProcessRunner.RunAsync(foundry, ["server", "status"], TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !Regex.IsMatch(result.Output, @"https?://(?:localhost|127\.0\.0\.1):\d+"))
                result = await ProcessRunner.RunAsync(foundry, ["service", "status"], TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            // "Not running" still reports the last URL it used, so an installed-but-stopped runtime
            // would be advertised as available and then refuse every request. Start it instead.
            if (result.Output.Contains("Not running", StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(result.Output, @"https?://(?:localhost|127\.0\.0\.1):\d+"))
            {
                var start = await ProcessRunner.RunAsync(foundry, ["server", "start"], TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                result = Regex.IsMatch(start.Output, @"https?://(?:localhost|127\.0\.0\.1):\d+")
                    ? start
                    : await ProcessRunner.RunAsync(foundry, ["server", "status"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }

            return Regex.Matches(result.Output, @"https?://(?:localhost|127\.0\.0\.1):\d+").Select(m => new Uri(m.Value)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TaskCanceledException) { return []; }
    }

    private static IEnumerable<string> ParseFoundryModelList(string output)
    {
        var names = ParseModelNames(output)
            .Where(name => name.Contains('-', StringComparison.Ordinal) || name.Contains('.', StringComparison.Ordinal))
            .Where(name => !name.Equals("model-id", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("model", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("alias", StringComparison.OrdinalIgnoreCase));
        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ParseModelNames(string output)
    {
        foreach (var raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = Regex.Replace(raw, @"[│┃|]", " ");
            foreach (Match match in Regex.Matches(line, @"(?<![A-Za-z0-9_.:/-])(?<name>[A-Za-z][A-Za-z0-9_.]*(?:[-:/][A-Za-z0-9_.]+)+)(?![A-Za-z0-9_.:/-])"))
            {
                var name = match.Groups["name"].Value.Trim();
                if (!name.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("foundry-local", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("microsoft.com", StringComparison.OrdinalIgnoreCase))
                {
                    yield return name;
                }
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 10 || unit == 0
            ? $"{value:F0} {units[unit]}"
            : $"{value:F1} {units[unit]}";
    }

    private static string? ResolveOnPath(string fileName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try { var path = Path.Combine(dir.Trim(), fileName); if (File.Exists(path)) return path; } catch (Exception) { }
        }
        return null;
    }

    private static string? ResolveFoundryCli() => ResolveOnPath("foundry.exe") ?? ResolveOnPath("foundry");

    private sealed record LlmDto(
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("tasks")] string[]? Tasks,
        [property: JsonPropertyName("synonyms")] string[]? Synonyms,
        [property: JsonPropertyName("category")] string? Category);

    private sealed record EndpointCandidate(Uri Endpoint, LocalLlmRuntime Runtime);
    private sealed record ResolvedEndpoint(Uri Endpoint, string ModelName, LocalLlmRuntime Runtime);
}

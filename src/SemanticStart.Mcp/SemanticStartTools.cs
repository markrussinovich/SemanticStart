using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;
using SemanticStart.Core.Query;

namespace SemanticStart.Mcp;

[McpServerToolType]
public static class SemanticStartTools
{
    private const int DefaultDocumentCharacters = 12_000;
    private const int MaximumDocumentCharacters = 50_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    [McpServerTool(Name = "search")]
    [Description("Searches the local SemanticStart index using hybrid semantic and lexical retrieval.")]
    public static async Task<string> SearchAsync(
        SemanticIndexRuntime runtime,
        [Description("Natural-language intent or application name to search for.")] string query,
        [Description("Maximum number of results, from 1 through 50.")] int limit = 8,
        CancellationToken cancellationToken = default)
    {
        var hits = await runtime.SearchAsync(query, Math.Clamp(limit, 1, 50), cancellationToken);

        return Serialize(new
        {
            query,
            count = hits.Count,
            results = hits.Select(hit => new
            {
                id = hit.Entity.Id,
                name = hit.Entity.DisplayName,
                kind = hit.Entity.Kind.ToString(),
                publisher = hit.Entity.Publisher,
                summary = hit.Summary,
                category = hit.Category,
                tasks = hit.Tasks,
                details = hit.Details,
                match_reason = hit.MatchReason,
            }),
        });
    }

    [McpServerTool(Name = "get_entity")]
    [Description("Retrieves an indexed entity and its synthesized metadata by exact stable entity id.")]
    public static async Task<string> GetEntityAsync(
        SemanticIndexRuntime runtime,
        [Description("Exact stable entity id returned by search or list_entities.")] string id,
        [Description("Include collector metadata, which may contain local paths or machine-specific values.")] bool includeRawMetadata = false,
        [Description("Include launch target, arguments, and icon source, which may contain local paths.")] bool includeLaunchInfo = false,
        CancellationToken cancellationToken = default)
    {
        var item = await runtime.GetEntityAsync(id, cancellationToken);
        if (item is null)
            return Serialize(new { found = false, id });

        return Serialize(new
        {
            found = true,
            entity = ProjectEntity(item, includeRawMetadata, includeLaunchInfo),
        });
    }

    [McpServerTool(Name = "list_entities")]
    [Description("Lists and filters local indexed entities without semantic ranking.")]
    public static async Task<string> ListEntitiesAsync(
        SemanticIndexRuntime runtime,
        [Description("Optional case-insensitive substring of the display name.")] string? nameContains = null,
        [Description("Optional exact entity kind, such as Application, PackagedApp, SettingsPage, or SystemTool.")] string? kind = null,
        [Description("Optional case-insensitive publisher substring.")] string? publisher = null,
        [Description("Optional exact collector source.")] string? source = null,
        [Description("Optional case-insensitive synthesized category substring.")] string? category = null,
        [Description("Return entities whose stable id sorts after this id, for pagination.")] string? afterId = null,
        [Description("Maximum number of entities, from 1 through 100.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var parsedKind = ParseKind(kind);
        var items = await runtime.ListEntitiesAsync(
            nameContains, parsedKind, publisher, source, category, afterId, limit, cancellationToken);
        var next = items.Count == Math.Clamp(limit, 1, 100) ? items[^1].Entity.Id : null;

        return Serialize(new
        {
            count = items.Count,
            next_after_id = next,
            entities = items.Select(item => ProjectEntity(item, includeRawMetadata: false, includeLaunchInfo: false)),
        });
    }

    [McpServerTool(Name = "get_documents")]
    [Description("Retrieves bounded enrichment documents and provenance for one indexed entity.")]
    public static async Task<string> GetDocumentsAsync(
        SemanticIndexRuntime runtime,
        [Description("Exact stable entity id returned by search or list_entities.")] string id,
        [Description("Optional exact document provider name.")] string? provider = null,
        [Description("Whether documents originally retrieved from online sources may be returned.")] bool includeOnline = true,
        [Description("Maximum characters returned across all documents, from 1 through 50000.")] int maxCharacters = DefaultDocumentCharacters,
        [Description("Include source URIs, which may contain local file paths.")] bool includeSourceUri = false,
        CancellationToken cancellationToken = default)
    {
        var entity = await runtime.GetEntityAsync(id, cancellationToken);
        if (entity is null)
            return Serialize(new { found = false, id });

        var remaining = Math.Clamp(maxCharacters, 1, MaximumDocumentCharacters);
        var truncated = false;
        var projected = new List<object>();
        var documents = await runtime.GetDocumentsAsync(id, cancellationToken);

        foreach (var document in documents.Where(document =>
                     (includeOnline || !document.IsOnline) &&
                     (string.IsNullOrWhiteSpace(provider) ||
                      document.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))))
        {
            if (remaining == 0)
            {
                truncated = true;
                break;
            }

            var text = document.Text;
            if (text.Length > remaining)
            {
                text = text[..remaining];
                truncated = true;
            }

            remaining -= text.Length;
            projected.Add(new
            {
                provider = document.Provider,
                online = document.IsOnline,
                source_uri = includeSourceUri ? document.SourceUri : null,
                retrieved_at = document.RetrievedAt,
                text,
            });
        }

        return Serialize(new
        {
            found = true,
            entity = new { id = entity.Entity.Id, name = entity.Entity.DisplayName },
            documents = projected,
            truncated,
        });
    }

    [McpServerTool(Name = "get_index_status")]
    [Description("Reports whether the local SemanticStart index is available and how many entities it contains.")]
    public static async Task<string> GetIndexStatusAsync(
        SemanticIndexRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await runtime.InitializeAsync(cancellationToken);
        }
        catch (FileNotFoundException ex)
        {
            return Serialize(new { available = false, error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Serialize(new { available = false, error = ex.Message });
        }

        return Serialize(new
        {
            available = true,
            entity_count = runtime.Count,
            embedding_model = "all-MiniLM-L6-v2",
            embedding_dimensions = 384,
            transport = "stdio",
            read_only = true,
        });
    }

    [McpServerTool(Name = "refresh_index")]
    [Description("Reloads the read-only in-memory snapshot after the SemanticStart desktop app rebuilds the index.")]
    public static async Task<string> RefreshIndexAsync(
        SemanticIndexRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        await runtime.RefreshAsync(cancellationToken);
        return Serialize(new { refreshed = true, entity_count = runtime.Count });
    }

    private static object ProjectEntity(IndexedEntity item, bool includeRawMetadata, bool includeLaunchInfo) => new
    {
        id = item.Entity.Id,
        name = item.Entity.DisplayName,
        kind = item.Entity.Kind.ToString(),
        publisher = item.Entity.Publisher,
        source = item.Entity.Source,
        summary = item.Profile?.Summary,
        category = item.Profile?.Category,
        tasks = item.Profile?.Tasks ?? [],
        synonyms = item.Profile?.Synonyms ?? [],
        details = item.Profile?.Details,
        features = item.Profile?.Features,
        profile_generator = item.Profile?.Generator,
        launch = includeLaunchInfo
            ? new
            {
                kind = item.Entity.LaunchKind.ToString(),
                target = item.Entity.LaunchTarget,
                arguments = item.Entity.LaunchArguments,
                icon_source = item.Entity.IconSource,
            }
            : null,
        raw_metadata = includeRawMetadata ? item.Entity.RawMetadata : null,
    };

    private static EntityKind? ParseKind(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return Enum.TryParse<EntityKind>(value, ignoreCase: true, out var kind)
            ? kind
            : throw new ArgumentException($"Unknown entity kind '{value}'.");
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOptions);
}

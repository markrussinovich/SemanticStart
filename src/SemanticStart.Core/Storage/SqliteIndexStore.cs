using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Model;

namespace SemanticStart.Core.Storage;

public sealed partial class SqliteIndexStore : IIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _databasePath;
    private readonly string _vectorPath;
    private readonly object _gate = new();

    private SqliteConnection? _connection;
    private VectorFile? _vectors;
    private int _dimensions;
    private int _nextOrdinal;
    private bool _disposed;

    public SqliteIndexStore()
        : this(AppPaths.IndexDatabase, AppPaths.VectorFile)
    {
    }

    public SqliteIndexStore(string databasePath, string vectorPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorPath);

        _databasePath = databasePath;
        _vectorPath = vectorPath;
    }

    public Task InitializeAsync(string embeddingModelId, int dimensions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfDisposed();

            AppPaths.EnsureCreated();
            CreateParentDirectory(_databasePath);
            CreateParentDirectory(_vectorPath);

            _connection?.Dispose();
            SQLitePCL.Batteries_V2.Init();
            _connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());
            _connection.Open();

            IndexSchema.Initialize(_connection);
            _vectors = new VectorFile(_vectorPath, dimensions);
            _dimensions = dimensions;

            if (!IndexSchema.IsCompatible(_connection, embeddingModelId))
            {
                IndexSchema.ClearContent(_connection);
                _vectors.Truncate();
                _nextOrdinal = 0;
            }
            else
            {
                _nextOrdinal = Math.Max(GetNextEntityOrdinal(_connection), _vectors.RowCount);
            }

            IndexSchema.SetMeta(_connection, "schema_version", IndexSchema.Version.ToString(CultureInfo.InvariantCulture));
            IndexSchema.SetMeta(_connection, "embedding_model", embeddingModelId);
            IndexSchema.SetMeta(_connection, "embedding_dimensions", dimensions.ToString(CultureInfo.InvariantCulture));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT id, content_hash FROM entities WHERE content_hash IS NOT NULL;";
            using var reader = cmd.ExecuteReader();
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read())
                hashes[reader.GetString(0)] = reader.GetString(1);

            return Task.FromResult<IReadOnlyDictionary<string, string>>(hashes);
        }
    }

    public Task UpsertAsync(
        Entity entity,
        IReadOnlyList<EnrichmentDocument> documents,
        SynthesizedProfile? profile,
        float[]? embedding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            using var tx = Connection.BeginTransaction();
            var ordinal = GetExistingOrdinal(entity.Id, tx);
            if (embedding is not null && ordinal is null)
                ordinal = _nextOrdinal++;

            UpsertEntity(entity, ordinal, tx);
            ReplaceDocuments(entity.Id, documents, tx);
            ReplaceProfile(entity.Id, profile, tx);
            if (embedding is not null)
                Vectors.Write(ordinal!.Value, embedding);
            RefreshFts(entity, profile, tx);
            tx.Commit();
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        cancellationToken.ThrowIfCancellationRequested();

        if (entityIds.Count == 0)
            return Task.CompletedTask;

        lock (_gate)
        {
            using var tx = Connection.BeginTransaction();
            foreach (var id in entityIds)
            {
                using var fts = Connection.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = "DELETE FROM entities_fts WHERE entity_id = $id;";
                fts.Parameters.AddWithValue("$id", id);
                fts.ExecuteNonQuery();

                using var entity = Connection.CreateCommand();
                entity.Transaction = tx;
                entity.CommandText = "DELETE FROM entities WHERE id = $id;";
                entity.Parameters.AddWithValue("$id", id);
                entity.ExecuteNonQuery();
            }

            // Vector ordinals are never compacted; deleted entities leave zero/unused holes.
            tx.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<IndexedEntity>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                SELECT e.id, e.kind, e.display_name, e.launch_kind, e.launch_target,
                       e.launch_arguments, e.icon_source, e.publisher, e.source,
                       e.raw_metadata, e.content_hash, e.vector_ordinal,
                       p.summary, p.tasks, p.synonyms, p.category, p.generator, p.details
                FROM entities e
                LEFT JOIN profiles p ON p.entity_id = e.id
                ORDER BY e.id;
                """;
            using var reader = cmd.ExecuteReader();
            var results = new List<IndexedEntity>();
            while (reader.Read())
            {
                var entity = new Entity
                {
                    Id = reader.GetString(0),
                    Kind = (EntityKind)reader.GetInt32(1),
                    DisplayName = reader.GetString(2),
                    LaunchKind = (LaunchKind)reader.GetInt32(3),
                    LaunchTarget = reader.GetString(4),
                    LaunchArguments = GetNullableString(reader, 5),
                    IconSource = GetNullableString(reader, 6),
                    Publisher = GetNullableString(reader, 7),
                    Source = reader.GetString(8),
                    RawMetadata = DeserializeDictionary(reader.GetString(9)),
                    ContentHash = GetNullableString(reader, 10)
                };

                SynthesizedProfile? profile = null;
                if (!reader.IsDBNull(12))
                {
                    profile = new SynthesizedProfile
                    {
                        EntityId = entity.Id,
                        Summary = reader.GetString(12),
                        Tasks = DeserializeList(reader.GetString(13)),
                        Synonyms = DeserializeList(reader.GetString(14)),
                        Category = GetNullableString(reader, 15),
                        Generator = reader.GetString(16),
                        Details = GetNullableString(reader, 17)
                    };
                }

                results.Add(new IndexedEntity
                {
                    Entity = entity,
                    Profile = profile,
                    VectorOrdinal = reader.IsDBNull(11) ? null : reader.GetInt32(11)
                });
            }

            return Task.FromResult<IReadOnlyList<IndexedEntity>>(results);
        }
    }

    public Task<float[]> GetVectorMatrixAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            return Task.FromResult(Vectors.ReadAll());
    }

    public Task<IReadOnlyList<(string EntityId, double Score)>> SearchLexicalAsync(
        string query, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0)
            return Task.FromResult<IReadOnlyList<(string EntityId, double Score)>>([]);

        var match = BuildFtsQuery(query);
        if (match.Length == 0)
            return Task.FromResult<IReadOnlyList<(string EntityId, double Score)>>([]);

        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();

            // Per-column BM25 weights, in declaration order:
            //   entity_id, display_name, summary, tasks, synonyms, publisher, details
            //
            // BM25 divides term frequency by document length, so a term landing in a very short
            // field scores enormously. With uniform weights that made the display name the single
            // strongest lexical signal: "set low power" ranked Power Automate first purely because
            // "Power" is one of two words in its name, and "record my screen" ranked the Lock
            // Screen settings page first for the same reason. Neither has anything to do with the
            // query's intent.
            //
            // The name is deliberately weighted *below* the intent fields. Literal-name lookup is
            // not BM25's job here: exact, prefix, acronym, and subsequence name matching all run
            // in NameMatcher and are fused separately, so lowering this weight costs nothing on
            // "wor" -> Word while removing the false intent matches. entity_id is UNINDEXED and
            // publisher is near-useless for ranking ("Microsoft Corporation" matches everything).
            // Details is weighted low. It is many sentences of harvested prose, so it is the field
            // most likely to contain an incidental term; it is here to make a genuinely relevant
            // entity reachable at all, not to outrank a curated task phrase.
            //
            // The weights are read from the environment so they can be swept against a fixed index
            // without a rebuild. Retuning them matters whenever the shape of the profiles changes:
            // weights fitted to one-line summaries are not the right weights once enrichment
            // yields several sentences and a dozen task phrases per entity.
            var w = LexicalWeights;
            cmd.CommandText = $"""
                SELECT entity_id, -bm25(entities_fts, {w}) AS score
                FROM entities_fts
                WHERE entities_fts MATCH $query
                ORDER BY bm25(entities_fts, {w})
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$query", match);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var hits = new List<(string EntityId, double Score)>();
            while (reader.Read())
                hits.Add((reader.GetString(0), reader.GetDouble(1)));

            return Task.FromResult<IReadOnlyList<(string EntityId, double Score)>>(hits);
        }
    }

    /// <summary>
    /// BM25 column weights, in the order the FTS table declares its columns: entity_id,
    /// display_name, summary, tasks, synonyms, publisher, details. Overridable through
    /// SEMANTICSTART_BM25 purely so the relevance harness can sweep them; the literal below is the
    /// shipped default and the only value any user sees.
    /// </summary>
    private static string LexicalWeights { get; } = ResolveLexicalWeights();

    private static string ResolveLexicalWeights()
    {
        const string shipped = "0.0, 1.0, 3.0, 5.0, 2.0, 0.25, 0.75";
        var raw = Environment.GetEnvironmentVariable("SEMANTICSTART_BM25");
        if (string.IsNullOrWhiteSpace(raw))
            return shipped;

        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 7 || !parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
            return shipped;

        return string.Join(", ", parts.Select(p => double.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture).ToString("0.####", CultureInfo.InvariantCulture)));
    }

    public Task<IReadOnlyDictionary<string, UsageStats>> GetUsageStatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT entity_id, launch_count, last_launched_at FROM usage_stats;";
            using var reader = cmd.ExecuteReader();
            var stats = new Dictionary<string, UsageStats>(StringComparer.Ordinal);
            while (reader.Read())
            {
                var id = reader.GetString(0);
                stats[id] = new UsageStats
                {
                    EntityId = id,
                    LaunchCount = reader.GetInt32(1),
                    LastLaunchedAt = reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)
                };
            }

            return Task.FromResult<IReadOnlyDictionary<string, UsageStats>>(stats);
        }
    }

    public Task RecordLaunchAsync(string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO usage_stats (entity_id, launch_count, last_launched_at)
                VALUES ($id, 1, $now)
                ON CONFLICT(entity_id) DO UPDATE SET
                    launch_count = launch_count + 1,
                    last_launched_at = excluded.last_launched_at;
                """;
            cmd.Parameters.AddWithValue("$id", entityId);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        return Task.CompletedTask;
    }

    public Task PurgeOnlineContentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var tx = Connection.BeginTransaction();
            using (var delete = Connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM documents WHERE is_online = 1;";
                delete.ExecuteNonQuery();
            }

            // The searchable mirror is projected from the entity and its profile, never from the
            // raw documents, so dropping the online corpus leaves it correct as it stands. The
            // profiles themselves go stale, which is what the caller's subsequent reindex is for.
            tx.Commit();
        }

        return Task.CompletedTask;
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            using var cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM entities;";
            return Task.FromResult(Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _connection?.Dispose();
            _disposed = true;
        }
    }

    private SqliteConnection Connection
    {
        get
        {
            ThrowIfDisposed();
            return _connection ?? throw new InvalidOperationException("The store has not been initialized.");
        }
    }

    private VectorFile Vectors => _vectors ?? throw new InvalidOperationException("The store has not been initialized.");

    private static void CreateParentDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    private int? GetExistingOrdinal(string entityId, SqliteTransaction tx)
    {
        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT vector_ordinal FROM entities WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", entityId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private void UpsertEntity(Entity entity, int? ordinal, SqliteTransaction tx)
    {
        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO entities (
                id, kind, display_name, launch_kind, launch_target, launch_arguments,
                icon_source, publisher, source, raw_metadata, content_hash, vector_ordinal, indexed_at)
            VALUES (
                $id, $kind, $display_name, $launch_kind, $launch_target, $launch_arguments,
                $icon_source, $publisher, $source, $raw_metadata, $content_hash, $vector_ordinal, $indexed_at)
            ON CONFLICT(id) DO UPDATE SET
                kind = excluded.kind,
                display_name = excluded.display_name,
                launch_kind = excluded.launch_kind,
                launch_target = excluded.launch_target,
                launch_arguments = excluded.launch_arguments,
                icon_source = excluded.icon_source,
                publisher = excluded.publisher,
                source = excluded.source,
                raw_metadata = excluded.raw_metadata,
                content_hash = excluded.content_hash,
                vector_ordinal = COALESCE(excluded.vector_ordinal, entities.vector_ordinal),
                indexed_at = excluded.indexed_at;
            """;
        cmd.Parameters.AddWithValue("$id", entity.Id);
        cmd.Parameters.AddWithValue("$kind", (int)entity.Kind);
        cmd.Parameters.AddWithValue("$display_name", entity.DisplayName);
        cmd.Parameters.AddWithValue("$launch_kind", (int)entity.LaunchKind);
        cmd.Parameters.AddWithValue("$launch_target", entity.LaunchTarget);
        AddNullable(cmd, "$launch_arguments", entity.LaunchArguments);
        AddNullable(cmd, "$icon_source", entity.IconSource);
        AddNullable(cmd, "$publisher", entity.Publisher);
        cmd.Parameters.AddWithValue("$source", entity.Source);
        cmd.Parameters.AddWithValue("$raw_metadata", JsonSerializer.Serialize(entity.RawMetadata, JsonOptions));
        AddNullable(cmd, "$content_hash", entity.ContentHash);
        AddNullable(cmd, "$vector_ordinal", ordinal);
        cmd.Parameters.AddWithValue("$indexed_at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    private void ReplaceDocuments(string entityId, IReadOnlyList<EnrichmentDocument> documents, SqliteTransaction tx)
    {
        using (var delete = Connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM documents WHERE entity_id = $entity_id;";
            delete.Parameters.AddWithValue("$entity_id", entityId);
            delete.ExecuteNonQuery();
        }

        foreach (var document in documents)
        {
            using var insert = Connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO documents (entity_id, provider, is_online, text, source_uri, retrieved_at)
                VALUES ($entity_id, $provider, $is_online, $text, $source_uri, $retrieved_at);
                """;
            insert.Parameters.AddWithValue("$entity_id", document.EntityId);
            insert.Parameters.AddWithValue("$provider", document.Provider);
            insert.Parameters.AddWithValue("$is_online", document.IsOnline ? 1 : 0);
            insert.Parameters.AddWithValue("$text", document.Text);
            AddNullable(insert, "$source_uri", document.SourceUri);
            insert.Parameters.AddWithValue("$retrieved_at", document.RetrievedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
    }

    private void ReplaceProfile(string entityId, SynthesizedProfile? profile, SqliteTransaction tx)
    {
        if (profile is null)
        {
            using var delete = Connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM profiles WHERE entity_id = $entity_id;";
            delete.Parameters.AddWithValue("$entity_id", entityId);
            delete.ExecuteNonQuery();
            return;
        }

        using var cmd = Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO profiles (entity_id, summary, tasks, synonyms, category, details, generator)
            VALUES ($entity_id, $summary, $tasks, $synonyms, $category, $details, $generator)
            ON CONFLICT(entity_id) DO UPDATE SET
                summary = excluded.summary,
                tasks = excluded.tasks,
                synonyms = excluded.synonyms,
                category = excluded.category,
                details = excluded.details,
                generator = excluded.generator;
            """;
        cmd.Parameters.AddWithValue("$entity_id", profile.EntityId);
        cmd.Parameters.AddWithValue("$summary", profile.Summary);
        cmd.Parameters.AddWithValue("$tasks", JsonSerializer.Serialize(profile.Tasks, JsonOptions));
        cmd.Parameters.AddWithValue("$synonyms", JsonSerializer.Serialize(profile.Synonyms, JsonOptions));
        AddNullable(cmd, "$category", profile.Category);
        AddNullable(cmd, "$details", profile.Details);
        cmd.Parameters.AddWithValue("$generator", profile.Generator);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rewrites the searchable mirror for one entity. The projection is built in C# rather than by
    /// copying the profile columns, because what is worth *storing* and what is worth *indexing*
    /// differ: a summary that only restates the entity's name is a usable subtitle but a harmful
    /// search signal, so it is written to profiles and withheld from here.
    /// </summary>
    private void RefreshFts(Entity entity, SynthesizedProfile? profile, SqliteTransaction tx)
    {
        using (var delete = Connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM entities_fts WHERE entity_id = $id;";
            delete.Parameters.AddWithValue("$id", entity.Id);
            delete.ExecuteNonQuery();
        }

        var summary = profile?.IndexableSummary(entity.DisplayName) ?? string.Empty;
        var tasks = profile is null ? [] : profile.IndexableTasks(entity.DisplayName);

        using var insert = Connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO entities_fts (entity_id, display_name, summary, tasks, synonyms, publisher, details)
            VALUES ($id, $name, $summary, $tasks, $synonyms, $publisher, $details);
            """;
        insert.Parameters.AddWithValue("$id", entity.Id);
        insert.Parameters.AddWithValue("$name", WithFoldedCompounds(entity.DisplayName));
        insert.Parameters.AddWithValue("$summary", WithFoldedCompounds(summary));
        insert.Parameters.AddWithValue("$tasks", WithFoldedCompounds(string.Join(". ", tasks)));
        insert.Parameters.AddWithValue("$synonyms", WithFoldedCompounds(string.Join(", ", profile?.Synonyms ?? [])));
        insert.Parameters.AddWithValue("$publisher", entity.Publisher ?? string.Empty);
        insert.Parameters.AddWithValue("$details", WithFoldedCompounds(profile?.Details ?? string.Empty));
        insert.ExecuteNonQuery();
    }

    /// <summary>
    /// Appends the separator-free spelling of every hyphenated compound in the text, because the
    /// tokenizer splits on the hyphen and users do not type one. Outlook describes itself as
    /// managing "to-dos", which the index stored as "to" and "dos"; the word "todo" therefore
    /// reached nothing that spelled it that way. The folded form is added rather than substituted,
    /// so "read-only" is still found by "read" and by "only" as well as by "readonly".
    /// </summary>
    internal static string WithFoldedCompounds(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var folded = new List<string>();
        foreach (Match match in HyphenatedCompoundRegex().Matches(text))
        {
            var joined = match.Value.Replace("-", string.Empty);
            if (!folded.Contains(joined, StringComparer.OrdinalIgnoreCase))
                folded.Add(joined);
        }

        return folded.Count == 0 ? text : text + " " + string.Join(" ", folded);
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)+")]
    private static partial Regex HyphenatedCompoundRegex();

    private static int GetNextEntityOrdinal(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(vector_ordinal) + 1, 0) FROM entities;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the FTS5 MATCH expression for a user query.
    /// <para>
    /// Tokens are combined with <c>OR</c>, not FTS5's implicit <c>AND</c>. Requiring every term to
    /// appear in the same row makes the lexical arm silently return nothing for ordinary
    /// multi-word intent queries — "default microphone" found no row containing both words, so the
    /// whole arm dropped out and results came from the vector arm alone. With <c>OR</c>, BM25 still
    /// ranks rows matching more of the query higher, which is the behaviour we actually want.
    /// </para>
    /// <para>
    /// Stopwords are dropped so that filler words ("the", "my", "how") cannot drag in unrelated
    /// rows, and the final token is a prefix match so results update sensibly while still typing.
    /// </para>
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var all = FtsTokenRegex().Matches(query)
            .Select(m => m.Value)
            .Where(t => t.Length > 0)
            .Take(16)
            .ToArray();
        if (all.Length == 0)
            return string.Empty;

        // Never drop the trailing token: it is the one the user is still typing.
        var tokens = all
            .Where((t, i) => i == all.Length - 1 || !Stopwords.Contains(t))
            .ToArray();
        if (tokens.Length == 0)
            tokens = all;

        // Parameterization prevents SQL injection; quoting tokens avoids FTS5 syntax errors.
        tokens[^1] = $"\"{tokens[^1]}\"*";
        for (var i = 0; i < tokens.Length - 1; i++)
            tokens[i] = $"\"{tokens[i]}\"";

        return string.Join(" OR ", tokens);
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for", "from", "get",
        "how", "i", "in", "is", "it", "me", "my", "of", "on", "or", "that", "the", "then", "there",
        "this", "to", "up", "want", "was", "what", "when", "where", "which", "why", "will", "with",
        "you", "your"
    };

    private static IReadOnlyDictionary<string, string> DeserializeDictionary(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
           ?? new Dictionary<string, string>();

    private static IReadOnlyList<string> DeserializeList(string json)
        => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];

    private static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddNullable(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    [GeneratedRegex(@"[\p{L}\p{Nd}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex FtsTokenRegex();
}

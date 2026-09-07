using Microsoft.Data.Sqlite;

namespace SemanticStart.Core.Storage;

/// <summary>
/// Owns the physical schema and its migrations. The index is disposable: if the schema version
/// or the embedding model changes, the correct response is to rebuild rather than migrate data,
/// because every vector would be invalid anyway.
/// </summary>
public static class IndexSchema
{
    /// <summary>Bump on any breaking schema change. A mismatch triggers a full rebuild.</summary>
    /// <remarks>
    /// v3 added the Porter stemmer to the FTS tokenizer. Without it the lexical arm matched only
    /// exact surface forms, so "edit a file" could not reach a profile that says "code editor" and
    /// "record a video" could not reach one that says "recording". Stemming is applied to both the
    /// indexed text and the query, so the two always agree.
    /// v4 added a details column carrying bounded prose from the best harvested document. Profiles
    /// previously kept only a one-line summary, so text like Wikipedia's "forcibly terminate
    /// processes" was fetched, used to pick a single sentence, and then thrown away - which is why
    /// Task Manager could not be found by "kill a process" no matter how good the enrichment was.
    /// v5 added a features column carrying the interface labels read out of a program's own menu
    /// and dialog resources. Prose about a tool describes its purpose; its menus state its
    /// capabilities, and the two often share no vocabulary at all - nothing written about Process
    /// Explorer mentions memory, while its View menu offers "Physical Memory History".
    /// </remarks>
    public const int Version = 5;

    public static void Initialize(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // WAL keeps the background indexer from blocking the query path, which must stay
        // responsive while a reindex is running.
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");
        Execute(connection, "PRAGMA foreign_keys=ON;");

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_info (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS entities (
                id               TEXT PRIMARY KEY,
                kind             INTEGER NOT NULL,
                display_name     TEXT    NOT NULL,
                launch_kind      INTEGER NOT NULL,
                launch_target    TEXT    NOT NULL,
                launch_arguments TEXT,
                icon_source      TEXT,
                publisher        TEXT,
                source           TEXT    NOT NULL,
                raw_metadata     TEXT    NOT NULL DEFAULT '{}',
                content_hash     TEXT,
                -- Row index into vectors.bin. NULL until the entity has been embedded.
                vector_ordinal   INTEGER,
                indexed_at       TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_entities_source ON entities(source);
            CREATE INDEX IF NOT EXISTS ix_entities_kind   ON entities(kind);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_entities_vector_ordinal
                ON entities(vector_ordinal) WHERE vector_ordinal IS NOT NULL;

            CREATE TABLE IF NOT EXISTS documents (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                entity_id    TEXT    NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
                provider     TEXT    NOT NULL,
                is_online    INTEGER NOT NULL,
                text         TEXT    NOT NULL,
                source_uri   TEXT,
                retrieved_at TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_documents_entity ON documents(entity_id);
            -- Lets the privacy control purge every network-derived document in one statement.
            CREATE INDEX IF NOT EXISTS ix_documents_online ON documents(is_online);

            CREATE TABLE IF NOT EXISTS profiles (
                entity_id TEXT PRIMARY KEY REFERENCES entities(id) ON DELETE CASCADE,
                summary   TEXT NOT NULL,
                tasks     TEXT NOT NULL DEFAULT '[]',
                synonyms  TEXT NOT NULL DEFAULT '[]',
                category  TEXT,
                details   TEXT,
                features  TEXT,
                generator TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS usage_stats (
                entity_id        TEXT PRIMARY KEY,
                launch_count     INTEGER NOT NULL DEFAULT 0,
                last_launched_at TEXT
            );
            """);
        EnsureUsageStatsWithoutEntityForeignKey(connection);

        // Contentless-external FTS5 table over the searchable text. This is the lexical arm and
        // it is what preserves fast literal-name behaviour ("wor" -> Word) alongside semantics.
        Execute(connection, """
            CREATE VIRTUAL TABLE IF NOT EXISTS entities_fts USING fts5(
                entity_id UNINDEXED,
                display_name,
                summary,
                tasks,
                synonyms,
                publisher,
                details,
                features,
                tokenize = 'porter unicode61 remove_diacritics 2'
            );
            """);
    }

    /// <summary>
    /// True when the stored schema version and embedding model both match what this build
    /// expects, and the tables on disk really have the shape that version implies.
    ///
    /// The recorded version is not trusted on its own. It is written at the end of startup, so a
    /// build that failed to migrate still stamps the new number, and every subsequent run then
    /// believes a stale table is current. Reading the columns back costs one query per table and
    /// makes the check answer the question that actually matters.
    /// </summary>
    public static bool IsCompatible(SqliteConnection connection, string expectedModelId)
    {
        var version = GetMeta(connection, "schema_version");
        var model = GetMeta(connection, "embedding_model");

        return version == Version.ToString()
               && string.Equals(model, expectedModelId, StringComparison.Ordinal)
               && HasCurrentShape(connection);
    }

    /// <summary>
    /// True when every content table that exists carries at least the columns this build writes.
    /// A table that does not exist yet is fine - Initialize is about to create it correctly.
    /// </summary>
    private static bool HasCurrentShape(SqliteConnection connection) =>
        RequiredColumns.All(table => HasColumns(connection, table.Key, table.Value));

    private static readonly Dictionary<string, string[]> RequiredColumns = new(StringComparer.Ordinal)
    {
        ["entities"] = ["id", "kind", "display_name", "launch_kind", "launch_target", "content_hash", "vector_ordinal"],
        ["documents"] = ["entity_id", "provider", "is_online", "text"],
        ["profiles"] = ["entity_id", "summary", "tasks", "synonyms", "category", "details", "features", "generator"],
        ["entities_fts"] = ["entity_id", "display_name", "summary", "tasks", "synonyms", "publisher", "details", "features"],
    };

    private static bool HasColumns(SqliteConnection connection, string table, IReadOnlyList<string> required)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table});";
            try
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    present.Add(reader.GetString(1));
            }
            catch (SqliteException)
            {
                return true;
            }
        }

        // No rows means the table does not exist, which Initialize will put right.
        return present.Count == 0 || required.All(present.Contains);
    }

    public static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_info (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static string? GetMeta(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM schema_info WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Drops all indexed content but preserves usage stats, which are user-earned and expensive to relearn.</summary>
    public static void ClearContent(SqliteConnection connection)
    {
        Execute(connection, """
            DELETE FROM entities_fts;
            DELETE FROM profiles;
            DELETE FROM documents;
            DELETE FROM entities;
            """);
    }

    /// <summary>
    /// Removes the content tables outright, so that <see cref="Initialize"/> recreates them at the
    /// current shape. Required whenever the stored version does not match, because every statement
    /// in Initialize is IF NOT EXISTS and would otherwise leave a table from an older version in
    /// place, missing whatever columns were added since.
    ///
    /// Usage stats and the schema metadata are deliberately left alone: they are not derived from
    /// the index, and a user's launch history is expensive to relearn.
    /// </summary>
    public static void DropContent(SqliteConnection connection)
    {
        Execute(connection, """
            DROP TABLE IF EXISTS entities_fts;
            DROP TABLE IF EXISTS profiles;
            DROP TABLE IF EXISTS documents;
            DROP TABLE IF EXISTS entities;
            """);
    }

    /// <summary>
    /// Creates just enough of the schema to read the stored version, so compatibility can be
    /// decided before any table is created at the current shape.
    /// </summary>
    public static void EnsureMetaTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_info (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    private static void EnsureUsageStatsWithoutEntityForeignKey(SqliteConnection connection)
    {
        bool hasForeignKey;
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText = "PRAGMA foreign_key_list(usage_stats);";
            using var reader = probe.ExecuteReader();
            hasForeignKey = reader.Read();
        }

        if (!hasForeignKey)
            return;

        Execute(connection, "PRAGMA foreign_keys=OFF;");
        try
        {
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS usage_stats_preserved (
                    entity_id        TEXT PRIMARY KEY,
                    launch_count     INTEGER NOT NULL DEFAULT 0,
                    last_launched_at TEXT
                );

                INSERT OR REPLACE INTO usage_stats_preserved (entity_id, launch_count, last_launched_at)
                    SELECT entity_id, launch_count, last_launched_at FROM usage_stats;

                DROP TABLE usage_stats;

                ALTER TABLE usage_stats_preserved RENAME TO usage_stats;
                """);
        }
        finally
        {
            Execute(connection, "PRAGMA foreign_keys=ON;");
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

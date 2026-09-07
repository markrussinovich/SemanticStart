using Microsoft.Data.Sqlite;
using SemanticStart.Core.Abstractions;
using SemanticStart.Core.Indexing;
using SemanticStart.Core.Model;
using SemanticStart.Core.Storage;

namespace SemanticStart.Tests;

/// <summary>
/// Covers what happens to an index built by an older version of the program. Every table in the
/// schema is created with IF NOT EXISTS, so a table from a previous version survives
/// initialisation untouched - and because the version check ran afterwards, the mismatch was
/// noticed but the stale shape was kept. The result was an index build in which all 521 entities
/// failed to persist while the run reported success.
/// </summary>
public sealed class SchemaUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ss-schema-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "index.sqlite");
    private string VectorPath => Path.Combine(_dir, "vectors.bin");

    public SchemaUpgradeTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task Build_SucceedsOverADatabaseLeftByAnEarlierSchemaVersion()
    {
        WritePreviousSchema();

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var collector = new FakeCollector("appsfolder",
        [
            Make("appsfolder", "notepad-aumid", "Notepad"),
        ]);
        var builder = new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), store);

        var result = await builder.BuildAsync(IndexOptions.Default);

        Assert.Equal(0, result.Failed);
        Assert.Null(result.FirstFailure);
        Assert.Single(await store.GetAllAsync());
    }

    /// <summary>
    /// The version is stamped at the end of startup, so a run that failed to migrate still records
    /// the current number against tables that were never rebuilt. Every run after that saw a
    /// matching version and left the stale shape in place, which is how one bad build made the
    /// index permanently unwritable.
    /// </summary>
    [Fact]
    public async Task Build_RecoversWhenTheRecordedVersionIsCurrentButTheTablesAreNot()
    {
        WritePreviousSchema(recordedVersion: IndexSchema.Version.ToString());

        using var store = new SqliteIndexStore(DbPath, VectorPath);
        var collector = new FakeCollector("appsfolder", [Make("appsfolder", "notepad-aumid", "Notepad")]);
        var builder = new IndexBuilder([collector], new FakeProfiler(), new FakeEmbeddings(), store);

        var result = await builder.BuildAsync(IndexOptions.Default);

        Assert.Null(result.FirstFailure);
        Assert.Equal(0, result.Failed);
    }

    /// <summary>
    /// A failure that is only written to the debugger is invisible in a release build, which is
    /// how a run with nothing but failures came to look like a successful one.
    /// </summary>
    [Fact]
    public void IndexResult_ReportsWhyTheFirstFailureHappened()
    {
        var result = new IndexResult { Discovered = 3, Failed = 3, FirstFailure = "persisting notepad: no such column: features" };

        Assert.Contains("no such column: features", result.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the profiles table as an earlier version declared it - without the columns added
    /// since - and stamps the older version into the metadata.
    /// </summary>
    private void WritePreviousSchema(string recordedVersion = "1")
    {
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE schema_info (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE entities (id TEXT PRIMARY KEY, display_name TEXT);
            CREATE TABLE profiles (
                entity_id TEXT PRIMARY KEY,
                summary   TEXT NOT NULL,
                generator TEXT NOT NULL
            );
            CREATE VIRTUAL TABLE entities_fts USING fts5(entity_id UNINDEXED, display_name);
            INSERT INTO schema_info (key, value) VALUES ('schema_version', $version);
            """;
        cmd.Parameters.AddWithValue("$version", recordedVersion);
        cmd.ExecuteNonQuery();
    }

    private static Entity Make(string source, string target, string name) => new()
    {
        Id = source + ":" + target,
        Kind = EntityKind.Application,
        DisplayName = name,
        LaunchKind = LaunchKind.AppsFolder,
        LaunchTarget = target,
        Source = source,
    };

    private sealed class FakeCollector(string source, IReadOnlyList<Entity> entities) : IEntityCollector
    {
        public string Source => source;
        public bool IsSupported => true;

        public async IAsyncEnumerable<Entity> CollectAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var entity in entities)
                yield return entity;
        }
    }

    private sealed class FakeProfiler : IEntityProfiler
    {
        public Task<(IReadOnlyList<EnrichmentDocument> Documents, SynthesizedProfile Profile)> ProfileAsync(
            Entity entity, bool allowNetwork, CancellationToken cancellationToken = default) =>
            Task.FromResult<(IReadOnlyList<EnrichmentDocument>, SynthesizedProfile)>((
                [],
                new SynthesizedProfile
                {
                    EntityId = entity.Id,
                    Summary = entity.DisplayName,
                    Features = "Interface labels: Physical Memory History.",
                    Generator = "test",
                }));
    }

    private sealed class FakeEmbeddings : IEmbeddingModel
    {
        public int Dimensions => 4;
        public string ModelId => "test-model";

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(_ => new[] { 1f, 0f, 0f, 0f })]);

        public void Dispose()
        {
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

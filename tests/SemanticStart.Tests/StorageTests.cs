using Microsoft.Data.Sqlite;
using SemanticStart.Core.Model;
using SemanticStart.Core.Storage;

namespace SemanticStart.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "StorageTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SqliteIndexStore_RoundTripsSearchesPurgesUsageAndWipesOnModelChange()
    {
        var dbPath = Path.Combine(_directory, "index.sqlite");
        var vectorPath = Path.Combine(_directory, "vectors.bin");

        using var store = new SqliteIndexStore(dbPath, vectorPath);
        await store.InitializeAsync("model-a", 384);

        var expectedVectors = new Dictionary<string, float[]>();
        for (var i = 0; i < 50; i++)
        {
            var id = $"test:entity-{i:00}";
            var displayName = i == 17 ? "Microsoft Word" : $"Synthetic App {i:00}";
            var vector = CreateVector(i, 384);
            expectedVectors[id] = vector;

            await store.UpsertAsync(
                CreateEntity(id, displayName, $"hash-{i}"),
                [
                    new EnrichmentDocument
                    {
                        EntityId = id,
                        Provider = "local",
                        IsOnline = false,
                        Text = $"Local documentation for {displayName}."
                    },
                    new EnrichmentDocument
                    {
                        EntityId = id,
                        Provider = "online",
                        IsOnline = true,
                        Text = $"Online documentation for {displayName}."
                    }
                ],
                new SynthesizedProfile
                {
                    EntityId = id,
                    Summary = displayName == "Microsoft Word" ? "Create and edit word processing documents." : $"Launch synthetic app {i}.",
                    Tasks = displayName == "Microsoft Word" ? ["write documents", "edit word files"] : [$"do synthetic task {i}"],
                    Synonyms = displayName == "Microsoft Word" ? ["word processor", "office"] : [$"synthetic-{i}"],
                    Category = "Productivity",
                    Generator = "test"
                },
                vector);
        }

        var all = await store.GetAllAsync();
        Assert.Equal(50, all.Count);

        var matrix = await store.GetVectorMatrixAsync();
        Assert.Equal(50 * 384, matrix.Length);
        foreach (var indexed in all)
        {
            Assert.NotNull(indexed.VectorOrdinal);
            var expected = expectedVectors[indexed.Entity.Id];
            var offset = indexed.VectorOrdinal.Value * 384;
            Assert.Equal(expected[0], matrix[offset], precision: 6);
            Assert.Equal(expected[127], matrix[offset + 127], precision: 6);
            Assert.Equal(expected[383], matrix[offset + 383], precision: 6);
        }

        var lexical = await store.SearchLexicalAsync("word", 10);
        Assert.Contains(lexical, hit => hit.EntityId == "test:entity-17");

        var exception = await Record.ExceptionAsync(() => store.SearchLexicalAsync("foo\" OR *:(", 10));
        Assert.Null(exception);

        await store.RecordLaunchAsync("test:entity-17");
        await store.RecordLaunchAsync("test:entity-17");
        var usage = await store.GetUsageStatsAsync();
        Assert.Equal(2, usage["test:entity-17"].LaunchCount);

        Assert.Equal(100, CountDocuments(dbPath));
        await store.PurgeOnlineContentAsync();
        Assert.Equal(50, CountDocuments(dbPath));
        Assert.Equal(0, CountOnlineDocuments(dbPath));

        await store.InitializeAsync("model-b", 384);
        Assert.Equal(0, await store.CountAsync());
        Assert.Empty(await store.GetVectorMatrixAsync());

        usage = await store.GetUsageStatsAsync();
        Assert.Equal(2, usage["test:entity-17"].LaunchCount);
    }

    [Fact]
    public async Task SqliteIndexStore_ReadOnlyOpenSupportsTargetedReadsWithoutRebuilding()
    {
        var dbPath = Path.Combine(_directory, "readonly.sqlite");
        var vectorPath = Path.Combine(_directory, "readonly-vectors.bin");
        var entity = CreateEntity("test:readonly", "Read Only App", "hash");

        using (var writer = new SqliteIndexStore(dbPath, vectorPath))
        {
            await writer.InitializeAsync("model-a", 3);
            await writer.UpsertAsync(
                entity,
                [
                    new EnrichmentDocument
                    {
                        EntityId = entity.Id,
                        Provider = "local-test",
                        IsOnline = false,
                        Text = "Read-only document."
                    }
                ],
                new SynthesizedProfile
                {
                    EntityId = entity.Id,
                    Summary = "Tests read-only access.",
                    Generator = "test"
                },
                [1, 0, 0]);
        }

        using (var incompatible = new SqliteIndexStore(dbPath, vectorPath))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => incompatible.OpenReadOnlyAsync("model-b", 3));
        }

        using var reader = new SqliteIndexStore(dbPath, vectorPath);
        await reader.OpenReadOnlyAsync("model-a", 3);

        var loaded = await reader.GetByIdAsync(entity.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Read Only App", loaded.Entity.DisplayName);
        Assert.Equal("Tests read-only access.", loaded.Profile?.Summary);

        var documents = await reader.GetDocumentsForEntityAsync(entity.Id);
        var document = Assert.Single(documents);
        Assert.Equal("local-test", document.Provider);
        Assert.Equal("Read-only document.", document.Text);
        Assert.Equal(1, await reader.CountAsync());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static Entity CreateEntity(string id, string displayName, string contentHash) => new()
    {
        Id = id,
        Kind = EntityKind.Application,
        DisplayName = displayName,
        LaunchKind = LaunchKind.Executable,
        LaunchTarget = $"{displayName}.exe",
        Publisher = "Contoso",
        Source = "test",
        RawMetadata = new Dictionary<string, string> { ["name"] = displayName },
        ContentHash = contentHash
    };

    private static float[] CreateVector(int seed, int dimensions)
    {
        var random = new Random(seed);
        var vector = new float[dimensions];
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)random.NextDouble();
        return vector;
    }

    private static int CountDocuments(string dbPath)
        => ExecuteScalar(dbPath, "SELECT COUNT(*) FROM documents;");

    private static int CountOnlineDocuments(string dbPath)
        => ExecuteScalar(dbPath, "SELECT COUNT(*) FROM documents WHERE is_online = 1;");

    private static int ExecuteScalar(string dbPath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}

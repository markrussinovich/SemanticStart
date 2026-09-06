using Microsoft.Data.Sqlite;
using SemanticStart.Core.Model;
using SemanticStart.Core.Storage;

namespace SemanticStart.Tests;

/// <summary>
/// The overlay searches on every keystroke, so a user never sees the answer to one query - they
/// see the answers to every prefix of it in turn. A result that appears, disappears and comes back
/// as a word is finished reads as broken even when the final answer is right, and it was reported
/// exactly that way: the results changed between "process", "processe" and "processes".
///
/// The lexical arm is the half of the engine that can be held to this exactly, because the porter
/// tokenizer folds inflections of a word to one stem. These tests pin that down. The vector arm
/// cannot be: cosine is not comparable across queries and a half-typed word genuinely depresses
/// it, which is why the surfacing floors that read raw cosine were the source of the churn, and
/// why the corpus asserts typing stability end to end through RelevanceCase.StableTrailingCharacters.
/// </summary>
public sealed class TypingStabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "TypingStabilityTests",
        Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("list process", "list processes")]
    [InlineData("list processe", "list processes")]
    [InlineData("running process", "running processes")]
    [InlineData("edit file", "edit files")]
    public async Task LexicalArm_AnswersInflectionsOfTheSameWordIdentically(string typed, string completed)
    {
        using var store = await CreateStoreAsync();

        var whileTyping = await store.SearchLexicalAsync(typed, 20);
        var finished = await store.SearchLexicalAsync(completed, 20);

        Assert.Equal(
            finished.Select(h => h.EntityId).ToArray(),
            whileTyping.Select(h => h.EntityId).ToArray());
    }

    /// <summary>
    /// The entity whose capability text is a sentence inside a paragraph has to be retrievable at
    /// all, not merely ranked below the one whose whole description is the query. This is the
    /// evidence the corroboration clause needs; without it there is nothing for the vector arm to
    /// agree with, and the reported failure was that Task Manager surfaced for one spelling of
    /// "list processes" and not the others.
    /// </summary>
    [Theory]
    [InlineData("list process")]
    [InlineData("list processe")]
    [InlineData("list processes")]
    public async Task LexicalArm_RetrievesAParagraphDescription_ForEveryPrefix(string query)
    {
        using var store = await CreateStoreAsync();

        var hits = await store.SearchLexicalAsync(query, 20);

        Assert.Contains(hits, h => h.EntityId == "test:paragraph");
    }

    private async Task<SqliteIndexStore> CreateStoreAsync()
    {
        var store = new SqliteIndexStore(
            Path.Combine(_directory, "index.sqlite"),
            Path.Combine(_directory, "vectors.bin"));

        await store.InitializeAsync("model-a", 8);

        // A terse entity, of the kind BM25 rewards most: its entire summary is the query.
        await UpsertAsync(store, "test:terse", "Tasklist", "List running processes and services.");

        // The same capability stated inside a paragraph, which is how a documented app describes
        // itself. This is the shape that was being lost.
        await UpsertAsync(
            store,
            "test:paragraph",
            "Task Manager",
            "Manage running apps and view system performance.",
            "It provides information about computer performance and running software, including "
                + "names of running processes, CPU and GPU load, commit charge, I/O details, "
                + "logged-in users, and Windows services. It can also be used to set process "
                + "priorities, start and stop services, and forcibly terminate processes.");

        // Entities that claim only the common word, to keep the query from being trivially unique.
        await UpsertAsync(store, "test:common-1", "PipeList", "Lists open named pipes.");
        await UpsertAsync(store, "test:common-2", "LogonSessions", "Lists logon session information.");
        await UpsertAsync(store, "test:files", "Text Editor", "Create and edit files of plain text.");

        return store;
    }

    private static Task UpsertAsync(
        SqliteIndexStore store,
        string id,
        string displayName,
        string summary,
        string? details = null)
        => store.UpsertAsync(
            new Entity
            {
                Id = id,
                Kind = EntityKind.Application,
                DisplayName = displayName,
                LaunchKind = LaunchKind.Executable,
                LaunchTarget = $"{displayName}.exe",
                Source = "test",
                RawMetadata = new Dictionary<string, string>(),
                ContentHash = id,
            },
            [],
            new SynthesizedProfile
            {
                EntityId = id,
                Summary = summary,
                Details = details,
                Tasks = [],
                Synonyms = [],
                Category = "Test",
                Generator = "test",
            },
            new float[8]);

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
}

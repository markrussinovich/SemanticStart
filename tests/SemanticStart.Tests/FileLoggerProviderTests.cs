using Microsoft.Extensions.Logging;
using SemanticStart.App;

namespace SemanticStart.Tests;

/// <summary>
/// The log file is what the README tells a user to send when something goes wrong, so its line
/// format is a contract, not an implementation detail - and a logger that can throw would turn a
/// handled failure into an unhandled one at the exact moment the app is already in trouble.
/// </summary>
public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("semanticstart-log-").FullName;

    private string LogPath => Path.Combine(_directory, "app.log");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The format the app has always written. Changing it silently would strand every existing
    /// log and any grep anyone has saved.
    /// </summary>
    [Fact]
    public void EntriesKeepTheTimestampLevelMessageFormat()
    {
        using var provider = new FileLoggerProvider(LogPath);
        provider.CreateLogger("SemanticStart.App.Example").LogInformation("Index built.");

        var line = File.ReadAllLines(LogPath).Single();

        Assert.Contains("[INFO] Index built.", line, StringComparison.Ordinal);
        Assert.True(
            DateTimeOffset.TryParse(line[..line.IndexOf(" [", StringComparison.Ordinal)], out _),
            $"The entry did not start with a parseable timestamp: {line}");
    }

    /// <summary>
    /// The app's own lines stay uncluttered, while anything hosted inside it - the MCP server, the
    /// hosting stack - is attributed, because for those the category is the only clue to what
    /// produced the line.
    /// </summary>
    [Fact]
    public void ForeignCategoriesAreNamedAndTheAppsOwnAreNot()
    {
        using var provider = new FileLoggerProvider(LogPath);
        provider.CreateLogger("SemanticStart.App.Example").LogInformation("Ours.");
        provider.CreateLogger("ModelContextProtocol.Server").LogInformation("Theirs.");

        var lines = File.ReadAllLines(LogPath);

        Assert.EndsWith("[INFO] Ours.", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("[INFO] ModelContextProtocol.Server: Theirs.", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// An exception reaches the file in full. A logged error that records only its message loses
    /// the stack trace, which is the part that makes the report actionable.
    /// </summary>
    [Fact]
    public void ErrorsCarryTheExceptionIntoTheFile()
    {
        using var provider = new FileLoggerProvider(LogPath);
        provider.CreateLogger("SemanticStart.App.Example")
            .LogError(new InvalidOperationException("device gone"), "Dictation failed.");

        var text = File.ReadAllText(LogPath);

        Assert.Contains("[ERROR] Dictation failed.", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("device gone", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Below the minimum level nothing is written at all - not an empty line, not a file. This is
    /// what keeps the hosting stack's trace output out of the MCP sink.
    /// </summary>
    [Fact]
    public void EntriesBelowTheMinimumLevelAreNotWritten()
    {
        using var provider = new FileLoggerProvider(LogPath, LogLevel.Information);
        var logger = provider.CreateLogger("SemanticStart.App.Example");

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        logger.LogTrace("Noise.");

        Assert.False(File.Exists(LogPath), "A filtered-out entry still created the log file.");
    }

    /// <summary>
    /// Logging must never be the thing that takes the app down. An unwritable path is the ordinary
    /// way that happens: a locked file, a redirected profile, a full disk.
    /// </summary>
    [Fact]
    public void AnUnwritablePathDoesNotThrow()
    {
        using var provider = new FileLoggerProvider(Path.Combine(_directory, "missing", "app.log"));

        provider.CreateLogger("SemanticStart.App.Example").LogError("Still running.");
    }
}

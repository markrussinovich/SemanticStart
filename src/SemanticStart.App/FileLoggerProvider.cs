using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using SemanticStart.Core;

namespace SemanticStart.App;

/// <summary>
/// Writes log entries to <c>%LOCALAPPDATA%\SemanticStart\logs\app.log</c>.
///
/// <para>
/// This exists because <c>Microsoft.Extensions.Logging</c> ships no file provider: the abstraction
/// is the <see cref="ILogger"/> the rest of the app talks to, and a sink still has to be supplied.
/// A tray app has no console to log to and no service host to collect its output, so a file is the
/// only place a user can be asked to look after something went wrong.
/// </para>
/// <para>
/// Deliberately plain. Appending a line under a lock is enough for an app that logs tens of lines
/// per session, and a background writer queue would add a thread, a shutdown flush and a dropped
/// message policy to defend against a cost nothing here is paying. Nothing is held open either, so
/// a crash cannot lose a buffered tail - which is the case where the log matters most.
/// </para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const string AppCategoryPrefix = nameof(SemanticStart) + ".";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly LogLevel _minimumLevel;

    public FileLoggerProvider(string path, LogLevel minimumLevel = LogLevel.Trace)
    {
        _path = path;
        _minimumLevel = minimumLevel;
    }

    /// <summary>The log file <see cref="Create"/> writes to.</summary>
    public static string DefaultPath => Path.Combine(AppPaths.LogDirectory, "app.log");

    /// <summary>A provider pointed at the app's own log file.</summary>
    public static FileLoggerProvider Create(LogLevel minimumLevel = LogLevel.Trace) =>
        new(DefaultPath, minimumLevel);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    /// <summary>
    /// Level tags, chosen so that the lines this app has always written stay byte for byte the
    /// same and an old log and a new one can be read together.
    /// </summary>
    internal static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => level.ToString().ToUpperInvariant(),
    };

    private bool IsEnabled(LogLevel level) => level >= _minimumLevel && level != LogLevel.None;

    /// <summary>
    /// Appends one entry. Failing to log must never take the app down with it, so a write failure
    /// falls back to the debugger rather than propagating into whatever was being logged about.
    /// </summary>
    private void Write(LogLevel level, string categoryName, string message, Exception? exception)
    {
        var line = new StringBuilder($"{DateTimeOffset.Now:u} [{Tag(level)}] ");

        // The app's own categories are its type names and add nothing to a line that already says
        // what happened; everything else - MCP server, hosting - is worth attributing.
        if (!categoryName.StartsWith(AppCategoryPrefix, StringComparison.Ordinal))
            line.Append(categoryName).Append(": ");

        line.Append(message);

        if (exception is not null)
            line.Append(": ").Append(exception);

        line.Append(Environment.NewLine);

        try
        {
            lock (_gate)
            {
                File.AppendAllText(_path, line.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"{Tag(level)}: {message} ({ex.Message})");
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            if (!provider.IsEnabled(logLevel))
                return;

            provider.Write(logLevel, categoryName, formatter(state, exception), exception);
        }
    }
}

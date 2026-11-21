using Microsoft.Extensions.Logging;
using SemanticStart.Core;

namespace SemanticStart.App;

/// <summary>
/// The app's logging entry point: a thin, statically reachable facade over
/// <c>Microsoft.Extensions.Logging</c>.
///
/// <para>
/// The sink is an <see cref="ILoggerProvider"/> (<see cref="FileLoggerProvider"/>), the factory is
/// a real <see cref="ILoggerFactory"/>, and anything that can take a dependency should take an
/// <see cref="ILogger{TCategoryName}"/> from <see cref="CreateLogger{T}"/> rather than call this
/// type. What this type adds is reach: the app has no container, and the places that need to log
/// most - a static WinForms tray callback, an unhandled-exception handler, a window's code-behind
/// constructed by WPF - have nowhere for a logger to be injected from. A static accessor is the
/// alternative to those sites not logging at all.
/// </para>
/// <para>
/// It is also the reason the log file is never half-configured: <see cref="Initialize"/> is called
/// before anything else in <c>App.OnStartup</c>, and until it is, writes go to a provider pointed
/// at the default path rather than being dropped.
/// </para>
/// </summary>
internal static class Log
{
    private static ILoggerFactory _factory = CreateFactory();
    private static ILogger _default = _factory.CreateLogger(nameof(SemanticStart));

    /// <summary>
    /// The factory behind every logger in the app, including the MCP host's. Exposed so a
    /// component that takes <see cref="ILogger"/> can be given one without this type being in its
    /// signature.
    /// </summary>
    public static ILoggerFactory Factory => _factory;

    /// <summary>
    /// Rebuilds the factory once the data directory is known to exist. Safe to call more than
    /// once; the previous factory is disposed.
    /// </summary>
    public static void Initialize()
    {
        AppPaths.EnsureCreated();

        var previous = _factory;
        _factory = CreateFactory();
        _default = _factory.CreateLogger(nameof(SemanticStart));
        previous.Dispose();
    }

    /// <summary>A logger categorised by <typeparamref name="T"/>, as constructor injection would give.</summary>
    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();

    public static void Info(string message) => _default.LogInformation("{Message}", message);

    /// <summary>
    /// A timing or step record rather than an event anyone asked for. Separated from
    /// <see cref="Info"/> only by its level, so that a reader grepping the log for what the app
    /// did is not wading through spans, and a reader profiling it can find nothing else.
    /// </summary>
    public static void Trace(string message) => _default.LogTrace("{Message}", message);

    public static void Error(Exception ex, string message) => _default.LogError(ex, "{Message}", message);

    /// <summary>
    /// Shuts the factory down at exit so providers that buffer get the chance to flush. The file
    /// provider does not buffer, but a second provider added later would, and discovering that at
    /// the point one is added is worse than paying for it now.
    /// </summary>
    public static void Shutdown() => _factory.Dispose();

    private static ILoggerFactory CreateFactory() =>
        LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(FileLoggerProvider.Create());
        });
}

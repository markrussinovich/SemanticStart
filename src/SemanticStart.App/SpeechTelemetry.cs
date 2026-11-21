using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using SemanticStart.Core.Speech;

namespace SemanticStart.App;

/// <summary>
/// Joins the speech library's diagnostics to the application's log.
///
/// <para>
/// <c>SemanticStart.Core</c> has never logged: it reports progress through <c>IProgress</c> and
/// failure through exceptions, and nothing in it knows where the log file is. Dictation does not
/// change that. It emits <see cref="Activity"/> spans and
/// <see cref="SpeechDiagnostics.Reported"/> events, and this class - which lives in the app, next
/// to <see cref="Log"/> - is the only thing that turns them into lines in <c>app.log</c>.
/// </para>
/// <para>
/// Activities cost nothing when nobody listens, so a build with no listener pays for none of
/// this; this listener is the one subscriber, and it writes each completed span as a single line
/// with its duration and tags. That makes the latency questions - how long the model took to
/// load, how long until the first partial, why a session ended - answerable from the same file as
/// everything else, and it leaves the source open to a real profiler (<c>dotnet-trace</c>,
/// OpenTelemetry) attaching to <see cref="SpeechDiagnostics.ActivitySourceName"/> without the app
/// changing.
/// </para>
/// </summary>
internal sealed class SpeechTelemetry : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ILogger _logger;
    private bool _disposed;

    private SpeechTelemetry(ILogger logger)
    {
        _logger = logger;

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SpeechDiagnostics.ActivitySourceName,

            // AllDataAndRecorded rather than AllData: sampled in, so that an external profiler
            // attached at the same time sees these spans as recorded rather than filtering them.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped,
        };

        ActivitySource.AddActivityListener(_listener);
        SpeechDiagnostics.Reported += OnReported;
    }

    /// <summary>Starts forwarding speech diagnostics to the log. Called once, at startup.</summary>
    public static SpeechTelemetry Install(ILogger? logger = null) =>
        new(logger ?? Log.CreateLogger<SpeechTelemetry>());

    private void OnActivityStopped(Activity activity)
    {
        var line = new StringBuilder()
            .Append(activity.OperationName)
            .Append(' ')
            .Append(activity.Duration.TotalMilliseconds.ToString("0.#", CultureInfo.InvariantCulture))
            .Append("ms");

        if (activity.Status == ActivityStatusCode.Error)
            line.Append(" status=error(").Append(activity.StatusDescription ?? "unknown").Append(')');

        foreach (var tag in activity.TagObjects.Where(tag => tag.Value is not null))
            line.Append(' ').Append(tag.Key).Append('=').Append(Format(tag.Value));

        _logger.LogTrace("{Span}", line.ToString());
    }

    private void OnReported(SpeechDiagnosticEvent report)
    {
        if (report.Exception is { } ex)
            _logger.LogError(ex, "speech/{Operation}: {Message}", report.Operation, report.Message);
        else if (report.IsError)
            _logger.LogInformation("speech/{Operation}: {Message}", report.Operation, report.Message);
        else
            _logger.LogTrace("speech/{Operation}: {Message}", report.Operation, report.Message);
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        SpeechDiagnostics.Reported -= OnReported;
        _listener.Dispose();
        _disposed = true;
    }
}

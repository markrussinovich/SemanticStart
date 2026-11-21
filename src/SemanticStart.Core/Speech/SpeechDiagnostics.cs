using System.Diagnostics;

namespace SemanticStart.Core.Speech;

/// <summary>
/// The one place dictation says what it is doing.
///
/// <para>
/// Two faces, because the feature has two observability problems that are not the same problem.
/// Traces answer "where did the time go": a dictated query crosses a microphone, a VAD, a
/// transducer and a search, and the latency the user complains about belongs to exactly one of
/// them. <see cref="Source"/> is an ordinary <see cref="ActivitySource"/>, so a profiler,
/// <c>dotnet-trace</c>, or the app's own listener can sample it without this assembly knowing they
/// exist, and it costs nothing at all when nobody is listening.
/// </para>
/// <para>
/// <see cref="Reported"/> answers the other one: something went wrong that the caller will never
/// see. A microphone that fails to stop, a temp file that cannot be deleted, a provider rejected
/// during startup - none of these reach a <c>catch</c> anywhere up the stack, and a
/// <c>catch { }</c> with a comment is precisely how they stay invisible. Everything this library
/// swallows is announced here instead, and the app writes it to the same log as everything else.
/// </para>
/// <para>
/// No transcript text, file contents, or audio is ever put on either face. Dictation is local by
/// construction and a log file that quotes what was said would undo that on disk; lengths and
/// durations answer the operational questions without recording what the user said.
/// </para>
/// </summary>
public static class SpeechDiagnostics
{
    /// <summary>The trace source name, for listeners that subscribe by name.</summary>
    public const string ActivitySourceName = "SemanticStart.Speech";

    /// <summary>Loading the speech model and constructing the recognizer.</summary>
    public const string EngineStartActivity = "speech.engine.start";

    /// <summary>Making sure the model files are on disk, downloading them if not.</summary>
    public const string ModelEnsureActivity = "speech.model.ensure";

    /// <summary>One model file download.</summary>
    public const string ModelDownloadActivity = "speech.model.download";

    /// <summary>One dictation turn, from the hotkey to the final transcript.</summary>
    public const string ListenActivity = "speech.dictation.listen";

    /// <summary>One microphone session.</summary>
    public const string CaptureActivity = "speech.capture";

    public static readonly ActivitySource Source = new(ActivitySourceName, "1.0.0");

    /// <summary>
    /// Raised for anything this library handles internally and would otherwise discard. Subscribers
    /// must not throw: a diagnostic that breaks the path it is reporting on is worse than no
    /// diagnostic, so exceptions from handlers are caught and dropped here.
    /// </summary>
    public static event Action<SpeechDiagnosticEvent>? Reported;

    /// <summary>Starts a trace span, or returns null when nothing is listening.</summary>
    public static Activity? StartActivity(string name) => Source.StartActivity(name, ActivityKind.Internal);

    /// <summary>Reports something that went right and is worth timing or counting.</summary>
    public static void Report(string operation, string message) =>
        Publish(new SpeechDiagnosticEvent(operation, message, null, IsError: false));

    /// <summary>
    /// Reports a failure that is being handled rather than thrown. The exception is carried whole
    /// so the log keeps its stack: a swallowed COM error with only its message left is a failure
    /// nobody can act on.
    /// </summary>
    public static void ReportFailure(string operation, string message, Exception? exception = null)
    {
        if (Activity.Current is { } activity)
        {
            activity.SetTag("speech.failure", operation);
            if (exception is not null)
                activity.AddEvent(new ActivityEvent(operation, tags: new ActivityTagsCollection
                {
                    ["exception.type"] = exception.GetType().FullName,
                    ["exception.message"] = exception.Message,
                }));
        }

        Publish(new SpeechDiagnosticEvent(operation, message, exception, IsError: true));
    }

    private static void Publish(SpeechDiagnosticEvent report)
    {
        try
        {
            Reported?.Invoke(report);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"A speech diagnostics handler threw: {ex}");
        }
    }
}

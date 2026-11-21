using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

/// <summary>
/// Diagnostics sit on the dictation path, which means they can break it. Everything else about
/// them - that a record keeps the exception it was handed, that an <c>ActivityListener</c> is
/// given the tags that were set - is either the framework's behaviour or a property assignment,
/// and is not pinned here. What is pinned is the one way this instrumentation could cost the user
/// a working feature.
/// </summary>
public sealed class SpeechDiagnosticsTests
{
    [Fact]
    public void AThrowingSubscriberDoesNotCostTheUserTheirDictation()
    {
        void Throws(SpeechDiagnosticEvent _) => throw new InvalidOperationException("bad handler");

        SpeechDiagnostics.Reported += Throws;
        try
        {
            // Both of these are called from inside a microphone session and a model download. A
            // handler that throws must not take either down: a diagnostic that breaks the path it
            // reports on is worse than no diagnostic at all.
            SpeechDiagnostics.Report("model.download", "Downloading.");
            SpeechDiagnostics.ReportFailure("capture.stop", "Stopping the microphone failed.", new IOException("gone"));
        }
        finally
        {
            SpeechDiagnostics.Reported -= Throws;
        }
    }
}

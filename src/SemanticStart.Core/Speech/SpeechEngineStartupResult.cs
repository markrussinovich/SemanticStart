namespace SemanticStart.Core.Speech;

public sealed record SpeechEngineStartupResult(
    ISpeechTranscriber? Transcriber,
    SpeechProviderMetadata? Metadata,
    IReadOnlyList<SpeechProviderRejection> Rejected)
{
    public bool IsAvailable => Transcriber is not null;

    /// <summary>
    /// One sentence naming the first thing that went wrong, for the status line. Null when an
    /// engine was loaded.
    /// </summary>
    public string? FailureReason => IsAvailable
        ? null
        : Rejected.Count > 0
            ? Rejected[0].Reason
            : "No speech engine is configured.";
}

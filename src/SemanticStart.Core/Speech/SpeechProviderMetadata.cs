namespace SemanticStart.Core.Speech;

/// <summary>
/// What a provider is and what it can do. These capability flags are what the selector and the UI
/// read, so a second provider that behaves differently - batch-only, or with no model to download
/// because Windows supplies it - needs no new branches anywhere else.
/// </summary>
public sealed record SpeechProviderMetadata(
    string Id,
    string DisplayName,
    string ModelId,
    string Language,
    bool SupportsPartialResults,
    bool RequiresModelDownload,
    long ApproximateDownloadBytes)
{
    /// <summary>Every provider in this app runs on the local machine; see <see cref="ISpeechTranscriber"/>.</summary>
    public bool RunsLocally => true;
}

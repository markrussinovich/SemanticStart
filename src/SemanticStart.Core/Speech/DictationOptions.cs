namespace SemanticStart.Core.Speech;

/// <summary>Tuning for one dictation session.</summary>
public sealed record DictationOptions
{
    public TimeSpan TrailingSilence { get; init; } =
        TimeSpan.FromMilliseconds(SpeechEndpointDetector.DefaultTrailingSilenceMilliseconds);

    public float SpeechThreshold { get; init; } = SpeechEndpointDetector.DefaultSpeechThreshold;

    /// <summary>How long a single dictated query may run before it is cut off.</summary>
    public TimeSpan MaximumUtterance { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait for the user to start speaking at all.</summary>
    public TimeSpan SilenceBeforeSpeechTimeout { get; init; } = TimeSpan.FromSeconds(8);
}

namespace SemanticStart.Core.Speech;

/// <summary>
/// Decides when a spoken query has ended, from a sequence of per-frame speech probabilities.
///
/// <para>
/// Separated from the model that produces those probabilities so the rule can be exercised
/// directly: endpointing is where dictation feels fast or broken, and the failure modes - cutting
/// a speaker off mid-sentence, or sitting there after they have finished - are both timing
/// behaviour rather than model behaviour.
/// </para>
/// <para>
/// The rule is deliberately plain. Speech has started once one frame crosses the threshold; the
/// utterance ends once <see cref="TrailingSilence"/> of continuous non-speech has followed
/// speech. Silence before anyone speaks does not end anything - it is a user who has not started
/// yet - which is why the session, not this class, owns the overall "nobody said anything"
/// timeout.
/// </para>
/// </summary>
public sealed class SpeechEndpointDetector
{
    /// <summary>
    /// How much silence ends an utterance, by default.
    ///
    /// <para>
    /// 220 ms is deliberately shorter than a conversational pause. This is a search box, not a
    /// dictaphone: queries are a few words long, said in one breath, and the entire perceived
    /// latency of the feature is dominated by this number. Longer values are safer for speakers
    /// who pause mid-phrase, which is why it is configurable.
    /// </para>
    /// </summary>
    public const int DefaultTrailingSilenceMilliseconds = 220;

    /// <summary>
    /// Above this probability a frame counts as speech. Silero's own examples use 0.5; it is kept
    /// here rather than in the model wrapper because it is part of the same decision.
    /// </summary>
    public const float DefaultSpeechThreshold = 0.5f;

    private readonly float _threshold;
    private TimeSpan _silenceSinceSpeech;

    public SpeechEndpointDetector(TimeSpan trailingSilence, float speechThreshold = DefaultSpeechThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trailingSilence, TimeSpan.Zero);

        TrailingSilence = trailingSilence;
        _threshold = speechThreshold;
    }

    public SpeechEndpointDetector()
        : this(TimeSpan.FromMilliseconds(DefaultTrailingSilenceMilliseconds))
    {
    }

    public TimeSpan TrailingSilence { get; }

    /// <summary>Whether any frame so far has been speech.</summary>
    public bool HasSpeechStarted { get; private set; }

    /// <summary>Whether the utterance has ended. Latches: a finished utterance stays finished.</summary>
    public bool IsEndpointReached { get; private set; }

    /// <summary>
    /// Feeds one frame. Returns true at the moment the endpoint is reached, and false afterwards,
    /// so a caller can act on the transition without tracking it themselves.
    /// </summary>
    public bool Accept(float speechProbability, TimeSpan frameDuration)
    {
        if (IsEndpointReached)
            return false;

        if (speechProbability >= _threshold)
        {
            HasSpeechStarted = true;
            _silenceSinceSpeech = TimeSpan.Zero;
            return false;
        }

        if (!HasSpeechStarted)
            return false;

        _silenceSinceSpeech += frameDuration;
        if (_silenceSinceSpeech < TrailingSilence)
            return false;

        IsEndpointReached = true;
        return true;
    }

    public void Reset()
    {
        HasSpeechStarted = false;
        IsEndpointReached = false;
        _silenceSinceSpeech = TimeSpan.Zero;
    }
}

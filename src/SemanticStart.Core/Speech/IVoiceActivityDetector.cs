namespace SemanticStart.Core.Speech;

/// <summary>
/// Whether audio is speech, frame by frame.
///
/// <para>
/// Behind an interface for the same reason the microphone is: endpointing timing is worth testing
/// on a machine that has neither a model file nor a sound card, and the rule that consumes these
/// probabilities lives in <see cref="SpeechEndpointDetector"/>.
/// </para>
/// </summary>
public interface IVoiceActivityDetector : IDisposable
{
    /// <summary>How many 16 kHz samples one call to <see cref="Process"/> expects.</summary>
    int FrameSamples { get; }

    /// <summary>How much audio one frame covers.</summary>
    TimeSpan FrameDuration { get; }

    /// <summary>The probability, in [0, 1], that the frame is speech.</summary>
    float Process(ReadOnlySpan<float> frame);

    /// <summary>Forgets the current utterance. Called before each dictation session.</summary>
    void Reset();
}

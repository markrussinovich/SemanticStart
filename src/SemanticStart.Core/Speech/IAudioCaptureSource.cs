namespace SemanticStart.Core.Speech;

/// <summary>
/// The microphone, as everything above it needs to see it: a stream of 16 kHz mono float buffers
/// and nothing else.
///
/// <para>
/// An interface rather than the WASAPI class directly, because every consumer of audio in this
/// feature - endpointing, the transcriber, the level meter, and their tests - can be exercised
/// from recorded samples, and a build server has no microphone.
/// </para>
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    /// <summary>Sample rate of the buffers yielded by <see cref="CaptureAsync"/>.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Opens the device and yields captured audio until cancelled. Failures to open are thrown as
    /// <see cref="MicrophoneUnavailableException"/> rather than ending the stream quietly: a
    /// dictation session that produces no words has to be able to say why.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync(CancellationToken cancellationToken = default);
}

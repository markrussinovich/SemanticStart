namespace SemanticStart.Core.Speech;

/// <summary>
/// Turns microphone audio into query text, entirely on this machine.
///
/// <para>
/// The local-only rule is not an implementation detail that a later change may trade away for a
/// better word error rate. The product promise is that a query never leaves the machine, and
/// spoken audio is a query - a richer one than the text, because it also carries who is speaking
/// and whatever else is audible in the room. Three Windows speech facilities are therefore
/// excluded on purpose, for the same reason the low-level keyboard hook is excluded in
/// <c>ActivationManager</c>: each is easy to reach for, and each would quietly break the promise.
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>Windows.Media.SpeechRecognition</c>. Free-form dictation there needs a
///     <c>SpeechRecognitionTopicConstraint</c>, which is gated on the "Online speech recognition"
///     privacy setting and sends the audio to Microsoft's service. Its constrained grammar modes
///     do stay local, but they only recognise a fixed word list, which is the opposite of
///     describing an intent in your own words.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>System.Speech</c>/SAPI. Local, but the pre-neural desktop engine: accuracy on free
///     speech is far below what anyone who has used phone dictation expects, and the shared
///     desktop recognizer is no longer a surface to build on for current Windows 11.
///     </description>
///   </item>
///   <item>
///     <description>
///     Win+H Voice Typing. It has no API at all: driving it means synthesising keystrokes into
///     whichever window has focus and reading the text back out of our own box, which is another
///     app's feature wearing our UI.
///     </description>
///   </item>
/// </list>
/// <para>
/// Implementations receive 16 kHz mono float samples in [-1, 1] and return transcripts as they
/// become available. The enumeration ends after the audio does; the last item yielded is the
/// <see cref="FinalTranscript"/>.
/// </para>
/// </summary>
public interface ISpeechTranscriber : IDisposable
{
    SpeechProviderMetadata Metadata { get; }

    /// <summary>
    /// Streams transcripts for one utterance. <paramref name="audio"/> yields 16 kHz mono float
    /// buffers; when it completes, the transcriber flushes and yields a
    /// <see cref="FinalTranscript"/>.
    /// </summary>
    IAsyncEnumerable<SpeechTranscript> TranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<float>> audio,
        CancellationToken cancellationToken = default);
}

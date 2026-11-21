using System.Runtime.CompilerServices;
using SherpaOnnx;

namespace SemanticStart.Core.Speech;

/// <summary>
/// The streaming NVIDIA Parakeet (NeMo) transducer from sherpa-onnx (Apache-2.0 export code; the
/// model weights are NVIDIA Open Model License - see <see cref="SpeechModelOptions"/>), running on
/// the CPU.
///
/// <para>
/// Streaming is the whole reason for this engine rather than a batch one. Results arrive while
/// the user is still speaking, so the search box fills in as the sentence is said and the only
/// wait the user experiences is the trailing-silence window - not the length of what they said.
/// </para>
/// <para>
/// Decoding is synchronous, CPU-bound work performed on whichever thread enumerates the result
/// stream. That thread must never be the UI thread; <c>DictationSession</c> is what guarantees it.
/// </para>
/// </summary>
public sealed class SherpaOnnxSpeechTranscriber : ISpeechTranscriber
{
    private readonly OnlineRecognizer _recognizer;
    private bool _disposed;

    public SherpaOnnxSpeechTranscriber(SpeechModelFiles files, SpeechProviderMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(files);
        Metadata = metadata;
        _recognizer = new OnlineRecognizer(CreateConfig(files));
    }

    /// <summary>
    /// Builds the native recognizer config for the streaming Parakeet Unified model.
    ///
    /// <para>
    /// sherpa-onnx auto-detects this exact model from metadata it reads out of
    /// <see cref="SpeechModelFiles.DecoderPath"/> (a <c>streaming_model_type</c> field the export
    /// script writes) and, on a match, routes construction to its dedicated
    /// <c>OnlineRecognizerTransducerNeMoParakeetUnifiedImpl</c> rather than the generic transducer
    /// path the previous Zipformer model used - see
    /// <c>sherpa-onnx/csrc/online-recognizer-impl.cc</c> in sherpa-onnx 1.13.4. No separate
    /// <c>ModelType</c> string needs to be set here as a result: the encoder/decoder/joiner/tokens
    /// paths below are everything the native side needs to pick the right implementation.
    /// </para>
    /// <para>
    /// That dedicated implementation only implements greedy decoding - requesting
    /// <c>modified_beam_search</c> against it is a fatal error on the native side
    /// (<c>SHERPA_ONNX_EXIT</c>), not a recoverable one - so unlike the Zipformer model this
    /// replaces, there is no beam width to tune here. Parakeet's accuracy comes from the acoustic
    /// model rather than from keeping alternative hypotheses, which is also why its output already
    /// carries natural casing and punctuation rather than the shouted, unpunctuated LibriSpeech
    /// style <see cref="SpeechTextFormatter"/> was written to clean up.
    /// </para>
    /// </summary>
    internal static OnlineRecognizerConfig CreateConfig(SpeechModelFiles files)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = MonoFloatResampler.TargetSampleRate;
        // Overridden to 128 by the native recognizer once it reads the model's own feature
        // dimension (NeMo's Conformer front end uses 128 mel bins, not the 80 the Zipformer model
        // used); set explicitly here anyway so this config is correct to read on its own.
        config.FeatConfig.FeatureDim = 128;
        config.ModelConfig.Transducer.Encoder = files.EncoderPath;
        config.ModelConfig.Transducer.Decoder = files.DecoderPath;
        config.ModelConfig.Transducer.Joiner = files.JoinerPath;
        config.ModelConfig.Tokens = files.TokensPath;
        config.ModelConfig.Provider = "cpu";

        // Two threads: enough to keep decoding ahead of real time on a laptop core, few enough
        // that a dictation session does not make the rest of the machine stutter. This runs on a
        // user's foreground machine while they are waiting to search, not on a transcription box.
        config.ModelConfig.NumThreads = Environment.ProcessorCount >= 8 ? 4 : 2;
        // The only decoding method the native Parakeet Unified implementation supports; see the
        // remarks above.
        config.DecodingMethod = "greedy_search";

        // The recognizer's own endpointing is off: Silero decides when the utterance has ended,
        // because that decision is shared with the level meter and the listening indicator and is
        // tuned by a setting the user can see. Two endpoint rules running at once would race.
        config.EnableEndpoint = 0;

        return config;
    }

    public SpeechProviderMetadata Metadata { get; }

    public async IAsyncEnumerable<SpeechTranscript> TranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<float>> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(audio);

        using var stream = _recognizer.CreateStream();
        var lastText = string.Empty;

        await foreach (var buffer in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (buffer.Length == 0)
                continue;

            stream.AcceptWaveform(MonoFloatResampler.TargetSampleRate, buffer.ToArray());
            while (_recognizer.IsReady(stream))
                _recognizer.Decode(stream);

            var text = SpeechTextFormatter.Format(_recognizer.GetResult(stream).Text);
            if (text.Length == 0 || string.Equals(text, lastText, StringComparison.Ordinal))
                continue;

            lastText = text;
            yield return new PartialTranscript(text);
        }

        // Supply the encoder's right context before flushing, as in sherpa's streaming examples.
        // This is synthetic silence, not an extra wait with the microphone open.
        cancellationToken.ThrowIfCancellationRequested();
        stream.AcceptWaveform(MonoFloatResampler.TargetSampleRate,
            new float[MonoFloatResampler.TargetSampleRate * 6 / 10]);
        stream.InputFinished();
        while (_recognizer.IsReady(stream))
            _recognizer.Decode(stream);

        // An utterance that produced no words yields nothing at all rather than an empty final:
        // an empty transcript is not a result, and downstream it would clear or re-space a query
        // the user never spoke into.
        var final = SpeechTextFormatter.Format(_recognizer.GetResult(stream).Text);
        if (final.Length > 0)
            yield return new FinalTranscript(final);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _recognizer.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Makes engine output look like something a person typed into a search box.
///
/// <para>
/// Parakeet, unlike the LibriSpeech-trained Zipformer this app previously used, emits its own
/// casing and punctuation directly - it does not need this step to turn "FREE UP DISK SPACE" into
/// something readable. The all-upper-case fallback below is kept anyway as a defensive case for
/// any provider (present or future) that does emit shouted, unpunctuated text: search is
/// case-insensitive, so this is purely about what the user reads back in an editable query box.
/// </para>
/// </summary>
internal static class SpeechTextFormatter
{
    public static string Format(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var trimmed = text.Trim();

        // Only fold case when the engine has given us nothing but upper case. A provider whose
        // output is already properly cased - Parakeet, and Windows AI Speech - is passed through
        // untouched.
        var hasLower = trimmed.Any(char.IsLower);
        return hasLower ? trimmed : trimmed.ToLower(System.Globalization.CultureInfo.CurrentCulture);
    }
}

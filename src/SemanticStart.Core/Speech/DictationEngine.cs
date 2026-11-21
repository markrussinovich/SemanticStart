using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SemanticStart.Core.Speech;

/// <summary>
/// One dictation utterance, from microphone to text: capture, endpointing, and transcription
/// wired together, with everything that touches hardware behind an interface.
///
/// <para>
/// The engine is long-lived and warm. Its transcriber holds the loaded model, which is the
/// expensive thing to create, so the app builds one at startup and calls
/// <see cref="ListenAsync"/> per press of the dictation hotkey. Nothing in that path loads a
/// model.
/// </para>
/// </summary>
public sealed class DictationEngine : IDisposable
{
    private readonly ISpeechTranscriber _transcriber;
    private readonly IVoiceActivityDetector _voiceActivity;
    private readonly Func<IAudioCaptureSource> _microphoneFactory;
    private bool _disposed;

    public DictationEngine(
        ISpeechTranscriber transcriber,
        IVoiceActivityDetector voiceActivity,
        Func<IAudioCaptureSource> microphoneFactory)
    {
        _transcriber = transcriber;
        _voiceActivity = voiceActivity;
        _microphoneFactory = microphoneFactory;
    }

    public SpeechProviderMetadata Metadata => _transcriber.Metadata;

    /// <summary>
    /// Listens until the user stops speaking, the caller cancels, or a timeout fires, reporting
    /// transcripts and input level as they happen.
    ///
    /// <para>
    /// Throws <see cref="MicrophoneUnavailableException"/> if the device cannot be opened, so a
    /// session that produces no text always has a reason attached to it.
    /// </para>
    /// </summary>
    public async Task ListenAsync(
        DictationOptions options,
        Action<SpeechTranscript> onTranscript,
        Action<float>? onLevel = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onTranscript);

        _voiceActivity.Reset();

        // The span every latency question is asked of: the two numbers a user feels are how long
        // until words appear and how long after they stop talking the box settles. Both are
        // recorded as tags rather than logged lines so that sampling a hundred sessions is a
        // query, not a parse. Lengths only - what was said never leaves the machine's memory.
        using var activity = SpeechDiagnostics.StartActivity(SpeechDiagnostics.ListenActivity);
        activity?.SetTag("speech.provider", _transcriber.Metadata.Id);
        activity?.SetTag("speech.trailing_silence_ms", options.TrailingSilence.TotalMilliseconds);

        var turn = Stopwatch.StartNew();
        var partials = 0;
        var finalLength = 0;
        TimeSpan? firstPartial = null;

        using var microphone = _microphoneFactory();

        // Ending the audio enumeration is what makes the transcriber flush, so the endpoint has to
        // stop the microphone too rather than merely stop reading it.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var audio = EndpointedAudioAsync(microphone, options, onLevel, stop, cancellationToken);

        try
        {
            await foreach (var transcript in _transcriber.TranscribeAsync(audio, cancellationToken).ConfigureAwait(false))
            {
                switch (transcript)
                {
                    case PartialTranscript partial:
                        partials++;
                        firstPartial ??= turn.Elapsed;
                        activity?.SetTag("speech.partial_chars", partial.Text.Length);
                        break;
                    case FinalTranscript final:
                        finalLength = final.Text.Length;
                        break;
                }

                onTranscript(transcript);
            }
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
        finally
        {
            activity?.SetTag("speech.partial_count", partials);
            activity?.SetTag("speech.final_chars", finalLength);
            activity?.SetTag("speech.first_partial_ms", firstPartial?.TotalMilliseconds);
            activity?.SetTag("speech.turn_ms", turn.Elapsed.TotalMilliseconds);
            activity?.SetTag("speech.cancelled", cancellationToken.IsCancellationRequested);
        }
    }

    /// <summary>
    /// Passes captured audio through to the transcriber, and stops it once the voice activity
    /// detector says the utterance has ended.
    ///
    /// <para>
    /// The detector is fed fixed-size frames that the microphone's packet size has no relation to,
    /// so samples are accumulated here rather than in the capture wrapper: how much audio arrives
    /// at a time is the device's business, and how much the model wants is the model's.
    /// </para>
    /// </summary>
    private async IAsyncEnumerable<ReadOnlyMemory<float>> EndpointedAudioAsync(
        IAudioCaptureSource microphone,
        DictationOptions options,
        Action<float>? onLevel,
        CancellationTokenSource stop,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = new SpeechEndpointDetector(options.TrailingSilence, options.SpeechThreshold);
        var frame = new float[_voiceActivity.FrameSamples];
        var filled = 0;
        var elapsed = TimeSpan.Zero;

        await foreach (var buffer in microphone.CaptureAsync(stop.Token).ConfigureAwait(false))
        {
            if (buffer.Length == 0)
                continue;

            onLevel?.Invoke(MeasureLevel(buffer.Span));
            yield return buffer;

            elapsed += TimeSpan.FromSeconds((double)buffer.Length / microphone.SampleRate);

            var source = buffer.Span;
            var consumed = 0;
            while (consumed < source.Length)
            {
                var take = Math.Min(frame.Length - filled, source.Length - consumed);
                source.Slice(consumed, take).CopyTo(frame.AsSpan(filled));
                filled += take;
                consumed += take;

                if (filled < frame.Length)
                    continue;

                filled = 0;
                if (endpoint.Accept(_voiceActivity.Process(frame), _voiceActivity.FrameDuration))
                {
                    Activity.Current?.SetTag("speech.end_reason", "endpoint");
                    await stop.CancelAsync().ConfigureAwait(false);
                    yield break;
                }
            }

            // Two ways for a session to end without anyone having finished a sentence: nobody
            // spoke at all, and somebody is still going. Both are bounded, because a microphone
            // that is never closed is a microphone the user has forgotten is open.
            var expired = endpoint.HasSpeechStarted
                ? elapsed >= options.MaximumUtterance
                : elapsed >= options.SilenceBeforeSpeechTimeout;

            if (expired || cancellationToken.IsCancellationRequested)
            {
                // Why a session ended is the first question asked of a dictation that "did not
                // work": a timeout, a cancel and a clean endpoint look identical from outside.
                Activity.Current?.SetTag(
                    "speech.end_reason",
                    expired ? (endpoint.HasSpeechStarted ? "max_utterance" : "no_speech") : "cancelled");

                if (expired && !endpoint.HasSpeechStarted)
                {
                    SpeechDiagnostics.Report(
                        "dictation.no_speech",
                        $"No speech was detected within {options.SilenceBeforeSpeechTimeout.TotalSeconds:0.#}s; the microphone was closed.");
                }

                await stop.CancelAsync().ConfigureAwait(false);
                yield break;
            }
        }
    }

    /// <summary>
    /// Root mean square of a buffer, scaled so ordinary speech fills the meter.
    ///
    /// <para>
    /// RMS rather than peak: the meter exists to answer "is it hearing me", and a peak meter
    /// answers that question with a single click of a keyboard. The 0.2 full-scale reference is
    /// roughly conversational speech about a foot from a laptop microphone.
    /// </para>
    /// </summary>
    internal static float MeasureLevel(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
            return 0f;

        var sum = 0.0;
        foreach (var sample in samples)
            sum += sample * (double)sample;

        var rms = Math.Sqrt(sum / samples.Length);
        return (float)Math.Clamp(rms / 0.2, 0.0, 1.0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _transcriber.Dispose();
        _voiceActivity.Dispose();
        _disposed = true;
    }
}

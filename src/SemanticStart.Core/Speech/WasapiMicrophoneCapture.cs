using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SemanticStart.Core.Speech;

/// <summary>
/// The default recording device, opened through WASAPI in shared mode and delivered as 16 kHz
/// mono float.
///
/// <para>
/// Shared mode, not exclusive: exclusive mode would take the microphone away from whatever else
/// is using it, and a launcher that breaks the call you are on to let you search is not a trade
/// anyone would make. The cost is that the device dictates the format - typically 48 kHz stereo
/// float - which <see cref="MonoFloatResampler"/> converts.
/// </para>
/// <para>
/// The buffer is asked to be short and the client is event-driven, because this audio is on the
/// latency path the whole feature is judged by: every millisecond of buffering is a millisecond
/// added to both the first partial result and the final one. Windows rounds the request up to the
/// engine's own period, so this is a floor rather than a guarantee.
/// </para>
/// <para>
/// Every way this can fail ends in a <see cref="MicrophoneUnavailableException"/> carrying a
/// reason. Dictation that silently produces no words is indistinguishable from dictation that is
/// broken, and the most common cause - the global desktop-app microphone switch being off - is
/// invisible unless it is named.
/// </para>
/// </summary>
public sealed class WasapiMicrophoneCapture : IAudioCaptureSource
{
    /// <summary>
    /// Requested capture period, passed to <see cref="WasapiCapture"/>'s
    /// <c>audioBufferMillisecondsLength</c> and from there to <c>IAudioClient::Initialize</c> as
    /// <c>hnsBufferDuration</c>. 20 ms is short enough not to dominate the latency budget and long
    /// enough to survive an ordinary scheduling hiccup without dropping packets.
    ///
    /// <para>
    /// It is a request, not a setting. In shared mode the audio engine rounds up to its own period
    /// - documented as typically 10 ms on current Windows, which is why asking for less than that
    /// buys nothing - so the effective buffer is max(request, engine period). The value that is
    /// actually in force is the device mix format recorded on the capture span.
    /// </para>
    /// </summary>
    private const int BufferMilliseconds = 20;

    private bool _disposed;

    public int SampleRate => MonoFloatResampler.TargetSampleRate;

    public async IAsyncEnumerable<ReadOnlyMemory<float>> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var activity = SpeechDiagnostics.StartActivity(SpeechDiagnostics.CaptureActivity);

        using var device = OpenDefaultDevice();
        using var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: BufferMilliseconds);

        var format = capture.WaveFormat;
        var resampler = new MonoFloatResampler(format.SampleRate, format.Channels);

        // The device's own mix format, not ours: when capture misbehaves this is the first thing
        // anyone reading a trace needs, and it is different on every machine.
        activity?.SetTag("audio.device.sample_rate", format.SampleRate);
        activity?.SetTag("audio.device.channels", format.Channels);
        activity?.SetTag("audio.device.bits_per_sample", format.BitsPerSample);
        activity?.SetTag("audio.device.encoding", format.Encoding.ToString());

        // Both counters are written from WASAPI's capture thread and read from whichever thread
        // enumerates this method, so every access goes through Interlocked. They are locals rather
        // than fields because they belong to one capture session - a second CaptureAsync on the
        // same instance would otherwise share them - and a local captured by a closure is a field
        // on a compiler-generated class, so Interlocked applies to it exactly as it would to one
        // declared by hand. The handlers below are callbacks: NAudio raises DataAvailable on the
        // capture thread, and the itemDropped callback runs on whichever thread wrote the item
        // that displaced an older one, which is that same thread.
        var captured = 0L;
        var dropped = 0L;

        // Dropping the oldest buffer beats blocking the audio thread: WASAPI's callback runs on a
        // real-time thread and stalling it glitches capture for every app on the machine. A
        // consumer far enough behind to hit this bound has already lost the utterance anyway.
        //
        // A dropped buffer is still audio the user spoke that the transcriber never saw, and it
        // cannot be thrown - the audio thread has nobody to throw to - so it is counted instead and
        // reported with the trace, so a transcript with a hole in it has an explanation.
        var channel = Channel.CreateBounded<ReadOnlyMemory<float>>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = true,
            },
            itemDropped: _ => Interlocked.Increment(ref dropped));

        void OnDataAvailable(object? sender, WaveInEventArgs args)
        {
            if (args.BytesRecorded <= 0)
                return;

            var samples = WasapiCaptureInterop.ToFloatSamples(args.Buffer, args.BytesRecorded, format);
            var converted = resampler.Resample(samples);
            if (converted.Length == 0)
                return;

            Interlocked.Add(ref captured, converted.Length);

            // TryWrite cannot fail for a full channel here - DropOldest evicts instead, and that
            // eviction is what itemDropped counts. It can still return false once the channel has
            // been completed, which happens when the capture thread stops while a final buffer is
            // in flight. That buffer is audio the user spoke that nothing will ever transcribe, so
            // it is counted with the rest rather than disappearing.
            if (!channel.Writer.TryWrite(converted))
                Interlocked.Increment(ref dropped);
        }

        void OnRecordingStopped(object? sender, StoppedEventArgs args)
        {
            // NAudio catches everything the capture thread throws, including anything raised from
            // the handler above, and delivers it here instead of anywhere a caller can see it.
            // Completing the channel with it is what turns it back into a thrown exception.
            if (args.Exception is { } ex)
                SpeechDiagnostics.ReportFailure("capture.thread", "The microphone capture thread stopped with an error.", ex);

            channel.Writer.TryComplete(args.Exception is { } failure ? WasapiCaptureInterop.Describe(failure) : null);
        }

        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += OnRecordingStopped;

        try
        {
            try
            {
                capture.StartRecording();
            }
            catch (Exception ex)
            {
                throw WasapiCaptureInterop.Describe(ex);
            }

            await foreach (var buffer in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return buffer;
        }
        finally
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;

            var droppedBuffers = Interlocked.Read(ref dropped);
            activity?.SetTag("audio.samples_captured", Interlocked.Read(ref captured));
            activity?.SetTag("audio.buffers_dropped", droppedBuffers);

            if (droppedBuffers > 0)
            {
                SpeechDiagnostics.Report(
                    "capture.dropped",
                    $"The transcriber fell behind the microphone and {droppedBuffers} buffer(s) were dropped.");
            }

            try
            {
                capture.StopRecording();
            }
            catch (Exception ex)
            {
                // Stopping a device that has already gone away does not fail the session - it is
                // ending either way - but it is still a native call that failed, and a COM error
                // that nobody ever hears about is how a whole class of device bugs stays invisible.
                SpeechDiagnostics.ReportFailure("capture.stop", "Stopping the microphone failed.", ex);
            }
        }
    }

    public void Dispose() => _disposed = true;

    private static MMDevice OpenDefaultDevice()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // The communications endpoint rather than the multimedia one: it is what Windows
            // points voice at, so it follows the headset the user actually speaks into, and it
            // carries the endpoint's own voice processing.
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                throw new MicrophoneUnavailableException(
                    MicrophoneFailure.NoDevice,
                    MicrophoneUnavailableException.DescribeFailure(MicrophoneFailure.NoDevice));
            }

            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (MicrophoneUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WasapiCaptureInterop.Describe(ex);
        }
    }
}

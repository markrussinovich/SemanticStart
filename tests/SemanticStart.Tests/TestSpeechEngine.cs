using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

/// <summary>
/// A transcriber that replays a scripted sequence of transcripts, so the parts of the speech stack
/// that are pure decision - selection, endpointing, the shape of the transcript stream - can be
/// exercised on a machine with no microphone, no ONNX runtime and no network.
/// </summary>
internal sealed class TestSpeechTranscriber : ISpeechTranscriber
{
    private readonly IReadOnlyList<SpeechTranscript> _script;

    internal TestSpeechTranscriber(SpeechProviderMetadata metadata, params SpeechTranscript[] script)
    {
        Metadata = metadata;
        _script = script;
    }

    public SpeechProviderMetadata Metadata { get; }

    /// <summary>Every buffer the caller handed over, in order, so the audio path can be asserted on.</summary>
    internal List<float[]> Received { get; } = [];

    internal bool IsDisposed { get; private set; }

    public async IAsyncEnumerable<SpeechTranscript> TranscribeAsync(
        IAsyncEnumerable<ReadOnlyMemory<float>> audio,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var next = 0;

        await foreach (var buffer in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            Received.Add(buffer.ToArray());

            // Partials are released as the audio arrives, the way a streaming engine does, so a
            // consumer that only reads after the stream completes fails the test rather than
            // passing it by accident.
            if (next < _script.Count && !_script[next].IsFinal)
                yield return _script[next++];
        }

        // The contract is that the transcriber flushes once the audio ends, and that the last item
        // yielded is the final transcript.
        for (; next < _script.Count; next++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return _script[next];
        }
    }

    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// A provider whose availability, load behaviour and metadata are all set by the test, which is
/// what makes the selector's preference order and its rejection bookkeeping observable.
/// </summary>
internal sealed class TestSpeechTranscriberProvider : ISpeechTranscriberProvider
{
    private readonly SpeechProviderAvailability _availability;
    private readonly Func<ISpeechTranscriber>? _create;

    internal TestSpeechTranscriberProvider(
        string id,
        SpeechProviderAvailability? availability = null,
        Func<ISpeechTranscriber>? create = null,
        bool supportsPartialResults = true,
        bool requiresModelDownload = false)
    {
        Metadata = new SpeechProviderMetadata(
            Id: id,
            DisplayName: id + " engine",
            ModelId: id + "-model",
            Language: "en-US",
            SupportsPartialResults: supportsPartialResults,
            RequiresModelDownload: requiresModelDownload,
            ApproximateDownloadBytes: 0);

        _availability = availability ?? SpeechProviderAvailability.Available;
        _create = create;
    }

    public SpeechProviderMetadata Metadata { get; }

    internal int AvailabilityChecks { get; private set; }

    internal int Creations { get; private set; }

    internal IProgress<double>? ObservedProgress { get; private set; }

    public SpeechProviderAvailability CheckAvailability()
    {
        AvailabilityChecks++;
        return _availability;
    }

    public Task<ISpeechTranscriber> CreateAsync(
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        Creations++;
        ObservedProgress = downloadProgress;
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<ISpeechTranscriber>(_create is null
            ? new TestSpeechTranscriber(Metadata)
            : _create());
    }
}

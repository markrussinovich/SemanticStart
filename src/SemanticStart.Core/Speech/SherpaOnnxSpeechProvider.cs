using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SemanticStart.Core.Speech;

/// <summary>
/// The sherpa-onnx streaming NVIDIA Parakeet (NeMo) model, as the engine selector sees it: what it
/// is, whether this machine can run it, and how to load it.
///
/// <para>
/// Availability is answered without loading anything, because it is asked at startup for every
/// provider. Three things can make it no: the wrong platform, a processor architecture the native
/// runtime is not published for, and a model that has not been downloaded yet in a session where
/// downloading has not been agreed to.
/// </para>
/// </summary>
public sealed class SherpaOnnxSpeechProvider : ISpeechTranscriberProvider
{
    public const string ProviderId = "sherpa-onnx-streaming-parakeet";

    private readonly SpeechModelBootstrapper _models;
    private readonly bool _allowDownload;

    public SherpaOnnxSpeechProvider(SpeechModelBootstrapper? models = null, bool allowDownload = true)
    {
        _models = models ?? new SpeechModelBootstrapper();
        _allowDownload = allowDownload;

        // Built here rather than as an initializer because the model identity and download size
        // now come from the bootstrapper's options, which an override file can change.
        Metadata = new SpeechProviderMetadata(
            ProviderId,
            "sherpa-onnx streaming Parakeet",
            _models.ModelId,
            "en-US",
            SupportsPartialResults: true,
            RequiresModelDownload: true,
            _models.Options.ApproximateDownloadBytes);
    }

    public SpeechProviderMetadata Metadata { get; }

    public SpeechProviderAvailability CheckAvailability()
    {
        if (!OperatingSystem.IsWindows())
            return SpeechProviderAvailability.Unavailable("Dictation is only supported on Windows.");

        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return SpeechProviderAvailability.Unavailable(
                $"No speech engine is published for {RuntimeInformation.ProcessArchitecture}.");
        }

        if (!_allowDownload && !_models.IsDownloaded)
        {
            return SpeechProviderAvailability.Unavailable(
                "The speech model has not been downloaded yet. Turn on dictation in Settings to download it.");
        }

        return SpeechProviderAvailability.Available;
    }

    public async Task<ISpeechTranscriber> CreateAsync(
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = SpeechDiagnostics.StartActivity(SpeechDiagnostics.EngineStartActivity);
        activity?.SetTag("speech.provider", ProviderId);
        activity?.SetTag("speech.model", _models.ModelId);

        var files = await _models.EnsureAsync(downloadProgress, cancellationToken).ConfigureAwait(false);

        // Constructing the recognizer opens three ONNX sessions and is the slow part of startup,
        // which is exactly why it happens here, once, rather than on the dictation hotkey. The
        // span around it is how "the tray icon took ages to come up" gets attributed.
        var loading = Stopwatch.StartNew();
        try
        {
            var transcriber = await Task.Run(
                () => (ISpeechTranscriber)new SherpaOnnxSpeechTranscriber(files, Metadata),
                cancellationToken).ConfigureAwait(false);

            activity?.SetTag("speech.model_load_ms", loading.Elapsed.TotalMilliseconds);
            return transcriber;
        }
        catch (Exception ex)
        {
            // Loading the model is a native call into sherpa-onnx, and a failure there is usually
            // a missing or mismatched onnxruntime rather than anything about the audio. Recording
            // the type before it is rethrown keeps that distinction in the trace.
            activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
            throw;
        }
    }
}

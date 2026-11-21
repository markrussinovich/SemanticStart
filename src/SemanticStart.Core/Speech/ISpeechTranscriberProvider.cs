namespace SemanticStart.Core.Speech;

/// <summary>
/// Creates a transcriber, and answers whether it could run here at all before anything is loaded.
///
/// <para>
/// There is one provider today. The indirection exists anyway because the second one is already
/// designed - Windows AI Speech on Windows 11 24H2+, which needs an OS version check and package
/// identity this build does not have - and retrofitting selection later would mean changing
/// startup, settings, and the overlay at the same time as adding an engine.
/// </para>
/// </summary>
public interface ISpeechTranscriberProvider
{
    SpeechProviderMetadata Metadata { get; }

    /// <summary>
    /// Whether this provider can be used on this machine, without loading it. Cheap by contract:
    /// this runs at startup for every registered provider.
    /// </summary>
    SpeechProviderAvailability CheckAvailability();

    /// <summary>
    /// Loads the engine, downloading its model if necessary. Called once at startup so that
    /// pressing the dictation hotkey never waits for a model to load.
    /// </summary>
    Task<ISpeechTranscriber> CreateAsync(
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default);
}

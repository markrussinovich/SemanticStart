namespace SemanticStart.Core.Speech;

/// <summary>
/// Picks the speech engine to use, in preference order, and records why the others were passed
/// over.
///
/// <para>
/// With one provider this is nearly a no-op, and that is deliberate: the selection point exists
/// before the second engine does, so adding Windows AI Speech later is a new
/// <see cref="ISpeechTranscriberProvider"/> in the list rather than a change to startup. The
/// rejections are kept rather than discarded because "dictation is unavailable" with no reason is
/// the failure mode that costs a user an evening.
/// </para>
/// </summary>
public sealed class SpeechEngineSelector(IReadOnlyList<ISpeechTranscriberProvider> providers)
{
    public IReadOnlyList<ISpeechTranscriberProvider> Providers { get; } = providers;

    /// <summary>
    /// The first provider that reports itself usable, without loading anything.
    /// </summary>
    public SpeechEngineChoice Choose()
    {
        var rejected = new List<SpeechProviderRejection>();

        foreach (var provider in Providers)
        {
            var availability = provider.CheckAvailability();
            if (availability.IsAvailable)
                return new SpeechEngineChoice(provider, rejected);

            rejected.Add(new SpeechProviderRejection(
                provider.Metadata,
                availability.Reason ?? $"{provider.Metadata.DisplayName} is not available on this machine."));
        }

        return new SpeechEngineChoice(null, rejected);
    }

    /// <summary>
    /// Chooses and loads the engine. Failing to load is treated as the provider being unavailable
    /// - a model that will not open is indistinguishable, from the user's side, from one that is
    /// not there - so a later provider still gets its turn.
    /// </summary>
    public async Task<SpeechEngineStartupResult> StartAsync(
        IProgress<double>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        var rejected = new List<SpeechProviderRejection>();

        foreach (var provider in Providers)
        {
            var availability = provider.CheckAvailability();
            if (!availability.IsAvailable)
            {
                rejected.Add(new SpeechProviderRejection(
                    provider.Metadata,
                    availability.Reason ?? $"{provider.Metadata.DisplayName} is not available on this machine."));
                continue;
            }

            try
            {
                var transcriber = await provider.CreateAsync(downloadProgress, cancellationToken).ConfigureAwait(false);
                return new SpeechEngineStartupResult(transcriber, provider.Metadata, rejected);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The exception is kept whole on the rejection rather than reduced to its message,
                // and reported here as well, so that a provider that failed to load leaves a stack
                // in the log even though the app carries on to the next one as if nothing happened.
                SpeechDiagnostics.ReportFailure(
                    "engine.load",
                    $"{provider.Metadata.DisplayName} failed to load and was skipped.",
                    ex);

                rejected.Add(new SpeechProviderRejection(provider.Metadata, ex.Message, ex));
            }
        }

        return new SpeechEngineStartupResult(null, null, rejected);
    }
}

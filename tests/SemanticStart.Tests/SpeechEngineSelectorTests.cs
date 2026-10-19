using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

/// <summary>
/// The selector is the one place that decides whether dictation works at all, and the reason it
/// keeps rejections is that "dictation is unavailable" with no explanation is unactionable. These
/// tests cover both halves: the preference order, and what is left behind when a provider is
/// passed over.
/// </summary>
public sealed class SpeechEngineSelectorTests
{
    [Fact]
    public void ChooseTakesTheFirstAvailableProvider()
    {
        var preferred = new TestSpeechTranscriberProvider("preferred");
        var fallback = new TestSpeechTranscriberProvider("fallback");
        var selector = new SpeechEngineSelector([preferred, fallback]);

        var choice = selector.Choose();

        Assert.True(choice.IsAvailable);
        Assert.Same(preferred, choice.Provider);
        Assert.Empty(choice.Rejected);

        // Availability is answered without loading anything, which is what lets this run at
        // startup for every provider.
        Assert.Equal(0, preferred.Creations);
        Assert.Equal(0, fallback.AvailabilityChecks);
    }

    [Fact]
    public void ChooseRecordsWhyEarlierProvidersWerePassedOver()
    {
        var missing = new TestSpeechTranscriberProvider(
            "windows-ai",
            SpeechProviderAvailability.Unavailable("Windows 11 24H2 or later is required."));
        var usable = new TestSpeechTranscriberProvider("sherpa-onnx");
        var selector = new SpeechEngineSelector([missing, usable]);

        var choice = selector.Choose();

        Assert.Same(usable, choice.Provider);
        var rejection = Assert.Single(choice.Rejected);
        Assert.Equal("windows-ai", rejection.Metadata.Id);
        Assert.Equal("Windows 11 24H2 or later is required.", rejection.Reason);
        Assert.Null(rejection.Exception);
    }

    /// <summary>
    /// A provider that says no without saying why still has to produce a sentence, because the
    /// rejection list is what the settings page shows.
    /// </summary>
    [Fact]
    public void AnUnavailableProviderWithNoReasonGetsAGeneratedOne()
    {
        var silent = new TestSpeechTranscriberProvider("silent", new SpeechProviderAvailability(false));
        var selector = new SpeechEngineSelector([silent]);

        var choice = selector.Choose();

        Assert.False(choice.IsAvailable);
        Assert.Null(choice.Provider);
        Assert.Equal("silent engine is not available on this machine.", Assert.Single(choice.Rejected).Reason);
    }

    [Fact]
    public void ChooseReportsNoProviderWhenNoneAreAvailable()
    {
        var first = new TestSpeechTranscriberProvider("first", SpeechProviderAvailability.Unavailable("no model"));
        var second = new TestSpeechTranscriberProvider("second", SpeechProviderAvailability.Unavailable("no runtime"));
        var selector = new SpeechEngineSelector([first, second]);

        var choice = selector.Choose();

        Assert.False(choice.IsAvailable);
        Assert.Null(choice.Provider);
        Assert.Equal(["no model", "no runtime"], choice.Rejected.Select(r => r.Reason));
    }

    [Fact]
    public async Task StartLoadsTheFirstAvailableProviderAndReportsItsMetadata()
    {
        var unavailable = new TestSpeechTranscriberProvider("unavailable", SpeechProviderAvailability.Unavailable("no model"));
        var usable = new TestSpeechTranscriberProvider("sherpa-onnx");
        var selector = new SpeechEngineSelector([unavailable, usable]);
        var progress = new Progress<double>(_ => { });

        var result = await selector.StartAsync(progress);

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Transcriber);
        Assert.Equal("sherpa-onnx", result.Metadata!.Id);
        Assert.Null(result.FailureReason);
        Assert.Equal(0, unavailable.Creations);

        // The download progress has to reach the provider, or the settings page shows a bar that
        // never moves during the one download the user was asked to authorise.
        Assert.Same(progress, usable.ObservedProgress);
    }

    /// <summary>
    /// A model that will not open is, from the user's side, indistinguishable from one that is not
    /// there, so the next provider still gets its turn and the exception is kept for the log.
    /// </summary>
    [Fact]
    public async Task AProviderThatFailsToLoadIsTreatedAsUnavailable()
    {
        var broken = new TestSpeechTranscriberProvider(
            "broken",
            create: () => throw new SpeechModelDownloadException("The encoder was truncated."));
        var fallback = new TestSpeechTranscriberProvider("fallback");
        var selector = new SpeechEngineSelector([broken, fallback]);

        var result = await selector.StartAsync();

        Assert.True(result.IsAvailable);
        Assert.Equal("fallback", result.Metadata!.Id);

        var rejection = Assert.Single(result.Rejected);
        Assert.Equal("broken", rejection.Metadata.Id);
        Assert.Equal("The encoder was truncated.", rejection.Reason);
        Assert.IsType<SpeechModelDownloadException>(rejection.Exception);
    }

    [Fact]
    public async Task StartReportsAFailureWhenNoProviderCanBeUsed()
    {
        var unavailable = new TestSpeechTranscriberProvider("unavailable", SpeechProviderAvailability.Unavailable("no model"));
        var broken = new TestSpeechTranscriberProvider("broken", create: () => throw new InvalidOperationException("no runtime"));
        var selector = new SpeechEngineSelector([unavailable, broken]);

        var result = await selector.StartAsync();

        Assert.False(result.IsAvailable);
        Assert.Null(result.Transcriber);
        Assert.Null(result.Metadata);
        Assert.Equal(["no model", "no runtime"], result.Rejected.Select(r => r.Reason));

        // The status line gets the first thing that went wrong, not a generic apology.
        Assert.Equal("no model", result.FailureReason);
    }

    [Fact]
    public async Task StartWithNoProvidersSaysSoRatherThanBlamingOne()
    {
        var result = await new SpeechEngineSelector([]).StartAsync();

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Rejected);
        Assert.Equal("No speech engine is configured.", result.FailureReason);
    }

    /// <summary>
    /// Cancellation is the user closing settings mid-download. It has to surface rather than be
    /// filed as one more provider that did not work.
    /// </summary>
    [Fact]
    public async Task CancellationDuringLoadIsNotRecordedAsARejection()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var selector = new SpeechEngineSelector([new TestSpeechTranscriberProvider("sherpa-onnx")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => selector.StartAsync(cancellationToken: cancellation.Token));
    }
}

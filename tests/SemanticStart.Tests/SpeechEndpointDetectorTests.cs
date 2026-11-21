using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

/// <summary>
/// Endpointing decides when a spoken query is searched, so both failure modes are visible to the
/// user rather than only to a log: ending early cuts a phrase in half, and ending late leaves the
/// overlay listening to a room that has gone quiet. Both are timing rules, which is exactly what
/// these tests drive - frames are fed with explicit durations, so nothing here waits on a clock.
/// </summary>
public sealed class SpeechEndpointDetectorTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(20);

    private const float Speech = 0.9f;
    private const float Silence = 0.1f;

    /// <summary>
    /// A user who has not started talking yet is not a user who has finished. The overall "nobody
    /// said anything" timeout belongs to the session, so silence here has to stay inert however
    /// long it runs.
    /// </summary>
    [Fact]
    public void SilenceBeforeAnyoneSpeaksNeverEndsTheUtterance()
    {
        var detector = new SpeechEndpointDetector();

        for (var i = 0; i < 200; i++)
            Assert.False(detector.Accept(Silence, Frame));

        Assert.False(detector.HasSpeechStarted);
        Assert.False(detector.IsEndpointReached);
    }

    /// <summary>
    /// Eleven 20 ms frames are 220 ms. The tenth must not fire and the eleventh must, because a
    /// detector that is one frame early or late is the difference between a natural-feeling pause
    /// and a clipped word.
    /// </summary>
    [Fact]
    public void TheEndpointFiresOnTheFrameThatCompletesTheDefaultWindow()
    {
        var detector = new SpeechEndpointDetector();

        Assert.False(detector.Accept(Speech, Frame));
        Assert.True(detector.HasSpeechStarted);

        for (var i = 0; i < 10; i++)
            Assert.False(detector.Accept(Silence, Frame));

        Assert.True(detector.Accept(Silence, Frame));
        Assert.True(detector.IsEndpointReached);
    }

    [Fact]
    public void TheEndpointFiresOnTheFrameThatCompletesAConfiguredWindow()
    {
        var detector = new SpeechEndpointDetector(TimeSpan.FromMilliseconds(500));
        var frame = TimeSpan.FromMilliseconds(10);

        detector.Accept(Speech, frame);

        for (var i = 0; i < 49; i++)
            Assert.False(detector.Accept(Silence, frame));

        Assert.True(detector.Accept(Silence, frame));
    }

    /// <summary>
    /// The transition is reported once so a caller can act on it without tracking it. Feeding a
    /// finished detector is not an error - audio keeps arriving until capture is torn down - but
    /// it must not produce a second endpoint and so a second search.
    /// </summary>
    [Fact]
    public void TheEndpointIsReportedOnceAndThenLatches()
    {
        var detector = new SpeechEndpointDetector();
        detector.Accept(Speech, Frame);

        var fired = 0;
        for (var i = 0; i < 100; i++)
        {
            if (detector.Accept(Silence, Frame))
                fired++;
        }

        Assert.Equal(1, fired);
        Assert.True(detector.IsEndpointReached);

        // Speech after the endpoint does not reopen the utterance; only Reset does.
        Assert.False(detector.Accept(Speech, Frame));
        Assert.True(detector.IsEndpointReached);
    }

    /// <summary>
    /// "Visual... studio" is one query, not two. A gap shorter than the window has to leave the
    /// utterance open, and the silence already counted has to be discarded rather than accumulated
    /// across the words that follow.
    /// </summary>
    [Fact]
    public void GapsShorterThanTheWindowDoNotEndTheUtterance()
    {
        var detector = new SpeechEndpointDetector();

        for (var word = 0; word < 4; word++)
        {
            Assert.False(detector.Accept(Speech, Frame));

            // 200 ms of silence: one frame short of the window, four times over.
            for (var i = 0; i < 10; i++)
                Assert.False(detector.Accept(Silence, Frame));
        }

        Assert.False(detector.IsEndpointReached);

        // The speaker has genuinely stopped this time. The 200 ms already counted is discarded by
        // the frame of speech before it, so the full window has to run again from zero.
        Assert.False(detector.Accept(Speech, Frame));
        for (var i = 0; i < 10; i++)
            Assert.False(detector.Accept(Silence, Frame));

        Assert.True(detector.Accept(Silence, Frame));
    }

    [Fact]
    public void ResetRestoresTheInitialState()
    {
        var detector = new SpeechEndpointDetector();
        detector.Accept(Speech, Frame);
        while (!detector.IsEndpointReached)
            detector.Accept(Silence, Frame);

        detector.Reset();

        Assert.False(detector.HasSpeechStarted);
        Assert.False(detector.IsEndpointReached);
        Assert.Equal(TimeSpan.FromMilliseconds(220), detector.TrailingSilence);

        // Silence accumulated before the reset must not carry over into the next utterance.
        for (var i = 0; i < 50; i++)
            Assert.False(detector.Accept(Silence, Frame));

        detector.Accept(Speech, Frame);
        for (var i = 0; i < 10; i++)
            Assert.False(detector.Accept(Silence, Frame));

        Assert.True(detector.Accept(Silence, Frame));
    }

    /// <summary>
    /// The threshold is inclusive, matching Silero's own examples: a frame sitting exactly on 0.5
    /// counts as speech.
    /// </summary>
    [Theory]
    [InlineData(SpeechEndpointDetector.DefaultSpeechThreshold, true)]
    [InlineData(0.49f, false)]
    public void TheThresholdDecidesWhetherAFrameCountsAsSpeech(float probability, bool expected)
    {
        var detector = new SpeechEndpointDetector();

        detector.Accept(probability, Frame);

        Assert.Equal(expected, detector.HasSpeechStarted);
    }
}

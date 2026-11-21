using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

/// <summary>
/// The capture device decides the format and the model decides the format it accepts, and neither
/// negotiates. Everything between them is this class, which is why it is tested at sample level:
/// a resampler that is quietly wrong produces audio that still plays and words that never match.
/// </summary>
public sealed class MonoFloatResamplerTests
{
    /// <summary>
    /// What a 48 kHz stereo device delivers in 10 ms, which is the typical shared-mode packet.
    /// </summary>
    private const int FramesPer10Ms = 480;

    [Fact]
    public void SixteenKilohertzMonoPassesThroughUntouched()
    {
        var resampler = new MonoFloatResampler(MonoFloatResampler.TargetSampleRate, channels: 1);
        var source = Ramp(320);

        Assert.True(resampler.IsPassThrough);

        var output = resampler.Resample(source);

        Assert.Equal(source, output);

        // The buffer is copied rather than aliased: the caller owns the capture buffer and reuses
        // it for the next packet.
        Assert.NotSame(source, output);
    }

    /// <summary>
    /// Stereo is averaged, not truncated to the left channel. A microphone array whose channels
    /// differ would otherwise lose half the signal.
    /// </summary>
    [Fact]
    public void StereoIsDownmixedByAveragingTheChannels()
    {
        var resampler = new MonoFloatResampler(MonoFloatResampler.TargetSampleRate, channels: 2);

        // Every frame carries the same pair, so the expected mono value is the same everywhere and
        // the assertion does not depend on the sub-sample delay interpolation introduces.
        var source = new float[200];
        for (var frame = 0; frame < source.Length / 2; frame++)
        {
            source[(frame * 2) + 0] = 0.5f;
            source[(frame * 2) + 1] = -0.1f;
        }

        var output = resampler.Resample(source);

        Assert.Equal(100, output.Length);
        Assert.All(output, sample => Assert.Equal(0.2f, sample, 5));
    }

    /// <summary>
    /// A packet whose length is not a whole number of frames has its tail dropped, rather than
    /// shifting left and right by one sample for the rest of the session.
    /// </summary>
    [Fact]
    public void ATrailingPartialFrameIsIgnored()
    {
        var resampler = new MonoFloatResampler(MonoFloatResampler.TargetSampleRate, channels: 2);

        var output = resampler.Resample(new float[9]);

        Assert.Equal(4, output.Length);
    }

    [Fact]
    public void AnEmptyBufferProducesNothing()
    {
        var resampler = new MonoFloatResampler(48_000, channels: 2);

        Assert.Empty(resampler.Resample([]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void FortyEightKilohertzProducesAThirdAsManySamples(int channels)
    {
        var resampler = new MonoFloatResampler(48_000, channels);

        var output = resampler.Resample(Ramp(FramesPer10Ms * channels));

        // One sample of slack: the interpolation phase can land either side of the buffer edge.
        Assert.InRange(output.Length, (FramesPer10Ms / 3) - 1, (FramesPer10Ms / 3) + 1);
    }

    /// <summary>
    /// Ten seconds of 48 kHz audio must still be ten seconds at 16 kHz. The phase carried between
    /// buffers is what keeps it there: dropping it would round every packet up and stretch the
    /// utterance, which is the kind of drift that only shows up in the long queries.
    /// </summary>
    [Fact]
    public void TheSampleRateHoldsOverManyBuffers()
    {
        var resampler = new MonoFloatResampler(48_000, channels: 2);
        var total = 0;

        for (var packet = 0; packet < 1000; packet++)
            total += resampler.Resample(Ramp(FramesPer10Ms * 2)).Length;

        Assert.InRange(total, MonoFloatResampler.TargetSampleRate * 10 - 1, MonoFloatResampler.TargetSampleRate * 10 + 1);
    }

    /// <summary>
    /// Buffer boundaries are invisible in the result: converting one long buffer and converting
    /// the same audio in packets must give the same samples, or every seam is a click the model
    /// hears as a consonant.
    /// </summary>
    [Fact]
    public void ConvertingInPacketsMatchesConvertingInOneGo()
    {
        var audio = Ramp(FramesPer10Ms * 4);

        var wholeAtOnce = new MonoFloatResampler(48_000, channels: 1).Resample(audio);

        var streamed = new List<float>();
        var streaming = new MonoFloatResampler(48_000, channels: 1);
        for (var offset = 0; offset < audio.Length; offset += FramesPer10Ms)
            streamed.AddRange(streaming.Resample(audio.AsSpan(offset, FramesPer10Ms)));

        Assert.Equal(wholeAtOnce.Length, streamed.Count);
        for (var i = 0; i < wholeAtOnce.Length; i++)
            Assert.Equal(wholeAtOnce[i], streamed[i], 5);
    }

    /// <summary>
    /// A constant signal stays constant across the seam. Interpolation that lost the previous
    /// buffer's last frame would dip to zero at the start of every packet - a 100 Hz buzz at
    /// 10 ms packets.
    /// </summary>
    [Fact]
    public void TheSeamBetweenBuffersDoesNotDip()
    {
        var resampler = new MonoFloatResampler(48_000, channels: 1);
        var constant = new float[FramesPer10Ms];
        Array.Fill(constant, 0.75f);

        resampler.Resample(constant);
        var second = resampler.Resample(constant);

        Assert.NotEmpty(second);
        Assert.All(second, sample => Assert.Equal(0.75f, sample, 5));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(-1, 1)]
    [InlineData(48_000, 0)]
    public void ImpossibleFormatsAreRejected(int sampleRate, int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonoFloatResampler(sampleRate, channels));
    }

    /// <summary>A rising ramp in [-1, 1], so a shifted or duplicated sample is visible.</summary>
    private static float[] Ramp(int length)
    {
        var samples = new float[length];
        for (var i = 0; i < length; i++)
            samples[i] = (i % 200 / 100f) - 1f;

        return samples;
    }
}

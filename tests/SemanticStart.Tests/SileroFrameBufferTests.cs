using SemanticStart.Core.Speech;
using SileroFrameBuffer = SemanticStart.Core.Speech.SileroVoiceActivityDetector.SileroFrameBuffer;

namespace SemanticStart.Tests;

public sealed class SileroFrameBufferTests
{
    [Fact]
    public void FirstFrameHasZeroContextAndTheWhole512SampleFrame()
    {
        var frames = new SileroFrameBuffer();
        var frame = Enumerable.Range(1, 512).Select(i => (float)i).ToArray();

        var input = frames.Accept(frame);

        Assert.Equal(2, input.Dimensions.Length);
        Assert.Equal(1, input.Dimensions[0]);
        Assert.Equal(576, input.Dimensions[1]);
        Assert.All(input.Buffer.Span[..64].ToArray(), sample => Assert.Equal(0f, sample));
        Assert.Equal(frame, input.Buffer.Span[64..].ToArray());
    }

    [Fact]
    public void EachFrameStartsWithThePreviousFramesLast64Samples()
    {
        var frames = new SileroFrameBuffer();
        var first = Enumerable.Range(1, 512).Select(i => (float)i).ToArray();
        var second = Enumerable.Repeat(0.25f, 512).ToArray();

        var firstInput = frames.Accept(first);
        var secondInput = frames.Accept(second);
        var thirdInput = frames.Accept(new float[512]);

        Assert.Equal(first[^64..], secondInput.Buffer.Span[..64].ToArray());
        Assert.Equal(second, secondInput.Buffer.Span[64..].ToArray());
        Assert.Equal(second[^64..], thirdInput.Buffer.Span[..64].ToArray());
        Assert.Equal(first, firstInput.Buffer.Span[64..].ToArray());
    }

    [Fact]
    public void ContextIsCopiedRatherThanAliasedToTheCaptureBuffer()
    {
        var frames = new SileroFrameBuffer();
        var frame = Enumerable.Repeat(0.5f, 512).ToArray();
        frames.Accept(frame);
        Array.Clear(frame);

        var next = frames.Accept(frame);

        Assert.All(next.Buffer.Span[..64].ToArray(), sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void ResetDoesNotCarryThePreviousUtteranceIntoTheNext()
    {
        var frames = new SileroFrameBuffer();
        frames.Accept(Enumerable.Repeat(1f, 512).ToArray());
        frames.Reset();

        var input = frames.Accept(new float[512]);

        Assert.All(input.Buffer.ToArray(), sample => Assert.Equal(0f, sample));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(511)]
    [InlineData(513)]
    [InlineData(576)]
    public void InvalidFramesDoNotCorruptTheContext(int length)
    {
        var frames = new SileroFrameBuffer();
        frames.Accept(Enumerable.Repeat(0.5f, 512).ToArray());

        Assert.Throws<ArgumentException>(() => frames.Accept(new float[length]));

        var input = frames.Accept(new float[512]);
        Assert.All(input.Buffer.Span[..64].ToArray(), sample => Assert.Equal(0.5f, sample));
    }
}

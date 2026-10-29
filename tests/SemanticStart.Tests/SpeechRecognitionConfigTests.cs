using SemanticStart.Core.Speech;

namespace SemanticStart.Tests;

public sealed class SpeechRecognitionConfigTests
{
    /// <summary>
    /// The native Parakeet Unified recognizer only implements greedy decoding; asking it for
    /// <c>modified_beam_search</c> is a fatal (process-exiting) error on the native side rather
    /// than a recoverable one, so this is pinned as a regression guard rather than a preference.
    /// </summary>
    [Fact]
    public void StreamingRecognitionUsesGreedyDecodingWithEndpointingDisabled()
    {
        var files = new SpeechModelFiles("encoder", "decoder", "joiner", "tokens", "vad");

        var config = SherpaOnnxSpeechTranscriber.CreateConfig(files);

        Assert.Equal("greedy_search", config.DecodingMethod);
        Assert.Equal(0, config.EnableEndpoint);
        Assert.Equal(16000, config.FeatConfig.SampleRate);
        Assert.Equal(128, config.FeatConfig.FeatureDim);
        Assert.Equal(files.EncoderPath, config.ModelConfig.Transducer.Encoder);
        Assert.Equal(files.DecoderPath, config.ModelConfig.Transducer.Decoder);
        Assert.Equal(files.JoinerPath, config.ModelConfig.Transducer.Joiner);
        Assert.Equal(files.TokensPath, config.ModelConfig.Tokens);
    }
}

namespace SemanticStart.Core.Speech;

public sealed record SpeechModelFiles(
    string EncoderPath,
    string DecoderPath,
    string JoinerPath,
    string TokensPath,
    string VadPath);

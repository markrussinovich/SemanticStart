namespace SemanticStart.Core.Speech;

public sealed record SpeechProviderRejection(SpeechProviderMetadata Metadata, string Reason, Exception? Exception = null);

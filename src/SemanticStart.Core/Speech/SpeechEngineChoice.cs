namespace SemanticStart.Core.Speech;

public sealed record SpeechEngineChoice(ISpeechTranscriberProvider? Provider, IReadOnlyList<SpeechProviderRejection> Rejected)
{
    public bool IsAvailable => Provider is not null;
}

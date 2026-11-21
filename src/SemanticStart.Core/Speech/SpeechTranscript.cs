namespace SemanticStart.Core.Speech;

/// <summary>
/// One reading of what has been said so far.
///
/// <para>
/// Partial and final are different kinds of claim, not the same claim with a flag set: a partial
/// may still be revised by the words that follow, so it is worth showing but not worth acting on,
/// while a final is what the engine will stand behind. They are separate types so that a consumer
/// which forgets the distinction fails to compile rather than launching on half a word.
/// </para>
/// </summary>
public abstract record SpeechTranscript(string Text)
{
    public abstract bool IsFinal { get; }
}

/// <summary>Text the engine may still revise while the user keeps speaking.</summary>
public sealed record PartialTranscript(string Text) : SpeechTranscript(Text)
{
    public override bool IsFinal => false;
}

/// <summary>The settled text for an utterance. Nothing follows it in the same stream.</summary>
public sealed record FinalTranscript(string Text) : SpeechTranscript(Text)
{
    public override bool IsFinal => true;
}

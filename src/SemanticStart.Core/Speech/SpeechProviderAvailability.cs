namespace SemanticStart.Core.Speech;

/// <summary>Whether a provider can be used, and if not, something the user can be told.</summary>
public sealed record SpeechProviderAvailability(bool IsAvailable, string? Reason = null)
{
    public static SpeechProviderAvailability Available { get; } = new(true);

    public static SpeechProviderAvailability Unavailable(string reason) => new(false, reason);
}

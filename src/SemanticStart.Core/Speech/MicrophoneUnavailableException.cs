namespace SemanticStart.Core.Speech;

/// <summary>
/// Thrown when the microphone cannot be opened or kept open, carrying the reason in a form the
/// overlay can turn into an instruction.
/// </summary>
public sealed class MicrophoneUnavailableException : Exception
{
    /// <summary>
    /// The exact wording of the Windows setting that governs microphone access for desktop apps.
    /// Named in full because "check your privacy settings" sends a user to a page with a dozen
    /// switches, and the one that matters is below a list of Store apps that do not include us.
    /// </summary>
    public const string DesktopMicrophoneSettingName = "Let desktop apps access your microphone";

    public MicrophoneUnavailableException(MicrophoneFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public MicrophoneFailure Failure { get; }

    /// <summary>The message to show in the overlay for a given failure.</summary>
    public static string DescribeFailure(MicrophoneFailure failure) => failure switch
    {
        MicrophoneFailure.NoDevice =>
            "No microphone was found. Connect one, or enable it under Settings > System > Sound > Input.",
        MicrophoneFailure.AccessDenied =>
            $"Windows blocked microphone access. Turn on \"{DesktopMicrophoneSettingName}\" under Settings > Privacy & security > Microphone.",
        _ => "The microphone could not be started; see the log for details.",
    };
}

namespace SemanticStart.Core.Speech;

/// <summary>Why the microphone could not be opened, in the terms the user can act on.</summary>
public enum MicrophoneFailure
{
    /// <summary>No capture device is present or enabled.</summary>
    NoDevice,

    /// <summary>
    /// Windows refused access. For an unpackaged desktop app there is no per-app microphone
    /// permission on current stable Windows, only the global switch, so the message names it.
    /// </summary>
    AccessDenied,

    /// <summary>The device is there and permitted, but the audio client failed.</summary>
    DeviceError,
}

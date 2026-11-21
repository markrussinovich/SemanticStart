using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SemanticStart.Core.Speech;

/// <summary>
/// The native-boundary part of microphone capture: the HRESULTs WASAPI reports and the raw bytes
/// it hands over. Separated from <see cref="WasapiMicrophoneCapture"/> so the capture class is the
/// session lifecycle and nothing else, and so both mappings can be exercised without a device.
/// </summary>
internal static class WasapiCaptureInterop
{
    // HRESULTs, in the two families that reach us here. The first three are Win32 error codes
    // wrapped by HRESULT_FROM_WIN32 (facility 7); the fourth is one of the audio client's own
    // AUDCLNT_E_* codes (facility 0x889).
    //
    //   0x80070005  E_ACCESSDENIED        - HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED). What
    //                                       IAudioClient::Initialize returns when the global
    //                                       microphone switch is off for desktop apps.
    //   0x80070490  ERROR_NOT_FOUND       - HRESULT_FROM_WIN32(ERROR_NOT_FOUND). Returned by
    //                                       IMMDeviceEnumerator::GetDefaultAudioEndpoint when
    //                                       there is no endpoint for the requested role.
    //   0x88890004  AUDCLNT_E_DEVICE_INVALIDATED - the endpoint was unplugged, disabled, or its
    //                                       format changed underneath an initialised client.
    //
    // See "Audio Client Error Codes" and IMMDeviceEnumerator::GetDefaultAudioEndpoint in the
    // Windows Core Audio APIs documentation on Microsoft Learn.
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int NotFound = unchecked((int)0x80070490);
    private const int AudioClientDeviceInvalidated = unchecked((int)0x88890004);

    /// <summary>
    /// Turns a WASAPI failure into something the user can act on. The HRESULTs above are the ones
    /// that distinguish "you have not allowed this" from "there is nothing to record with", which
    /// are the two cases with different fixes.
    /// </summary>
    internal static MicrophoneUnavailableException Describe(Exception exception)
    {
        if (exception is MicrophoneUnavailableException microphone)
            return microphone;

        var failure = exception switch
        {
            UnauthorizedAccessException => MicrophoneFailure.AccessDenied,
            COMException { ErrorCode: AccessDenied } => MicrophoneFailure.AccessDenied,
            COMException { ErrorCode: NotFound } => MicrophoneFailure.NoDevice,
            COMException { ErrorCode: AudioClientDeviceInvalidated } => MicrophoneFailure.NoDevice,
            ArgumentException => MicrophoneFailure.NoDevice,
            _ => MicrophoneFailure.DeviceError,
        };

        return new MicrophoneUnavailableException(
            failure,
            MicrophoneUnavailableException.DescribeFailure(failure),
            exception);
    }

    /// <summary>
    /// Reads one capture buffer as floats. Shared mode hands over the audio engine's mix format,
    /// which is float on every machine seen so far but is not promised to be, so 16-bit PCM is
    /// handled rather than assumed away.
    /// </summary>
    internal static float[] ToFloatSamples(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32)
        {
            var samples = new float[bytesRecorded / sizeof(float)];
            Buffer.BlockCopy(buffer, 0, samples, 0, samples.Length * sizeof(float));
            return samples;
        }

        if (format.BitsPerSample == 16)
        {
            var samples = new float[bytesRecorded / sizeof(short)];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BitConverter.ToInt16(buffer, i * sizeof(short)) / 32768f;

            return samples;
        }

        throw new MicrophoneUnavailableException(
            MicrophoneFailure.DeviceError,
            $"The microphone's format ({format.Encoding}, {format.BitsPerSample}-bit) is not supported.");
    }
}

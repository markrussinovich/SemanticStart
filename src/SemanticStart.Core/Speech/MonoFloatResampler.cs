namespace SemanticStart.Core.Speech;

/// <summary>
/// Converts whatever the capture device produces into the one format the speech stack accepts:
/// 16 kHz mono float.
///
/// <para>
/// A shared-mode WASAPI client hands over the audio engine's mix format, which on a typical
/// machine is 48 kHz stereo float and is not negotiable from the app side. The conversion is a
/// channel average followed by linear interpolation, kept here rather than taken from a media
/// framework for two reasons: it is a few lines for this fixed, downward, ratio, and it can be
/// tested on a machine with no audio stack at all.
/// </para>
/// <para>
/// Linear interpolation without a low-pass filter aliases frequencies above 8 kHz back down into
/// the band. That is acceptable precisely here: the model is trained on 16 kHz telephony-band
/// speech, the energy that aliases is consonant hiss well above the formants that carry the
/// words, and the alternative - a polyphase filter - costs latency in the path this feature is
/// being measured on.
/// </para>
/// <para>
/// Instances carry the interpolation phase across buffers, so one instance belongs to one capture
/// session. Reusing it across sessions would splice the end of one utterance onto the start of
/// the next.
/// </para>
/// </summary>
public sealed class MonoFloatResampler
{
    public const int TargetSampleRate = 16_000;

    private readonly int _sourceSampleRate;
    private readonly int _channels;

    /// <summary>Position, in source frames, of the next output sample relative to this buffer.</summary>
    private double _position;

    /// <summary>The last frame of the previous buffer, so interpolation spans the seam.</summary>
    private float _previous;
    private bool _hasPrevious;

    public MonoFloatResampler(int sourceSampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceSampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);

        _sourceSampleRate = sourceSampleRate;
        _channels = channels;
    }

    /// <summary>Whether the source already is 16 kHz mono, in which case samples pass through.</summary>
    public bool IsPassThrough => _sourceSampleRate == TargetSampleRate && _channels == 1;

    /// <summary>
    /// Converts one buffer of interleaved source samples. <paramref name="source"/> is expected to
    /// hold whole frames; a trailing partial frame is ignored rather than shifting every channel
    /// by one for the rest of the session.
    /// </summary>
    public float[] Resample(ReadOnlySpan<float> source)
    {
        var frames = source.Length / _channels;
        if (frames == 0)
            return [];

        if (IsPassThrough)
            return source[..frames].ToArray();

        var ratio = (double)_sourceSampleRate / TargetSampleRate;
        var output = new List<float>((int)(frames / ratio) + 2);

        while (_position < frames)
        {
            var index = (int)Math.Floor(_position);
            var fraction = _position - index;

            // Interpolating backwards, between the previous frame and this one, is what lets a
            // buffer be converted the moment it arrives: interpolating forwards would need the
            // frame after the last one, which is in a packet the device has not delivered yet.
            // The cost is a delay of up to one input frame - exactly one when the rates already
            // match and no interpolation is needed at all - which nothing downstream can observe.
            var left = index == 0
                ? _hasPrevious ? _previous : FrameAt(source, 0)
                : FrameAt(source, index - 1);

            // index < frames is guaranteed by the loop condition, so this frame is in this buffer.
            var right = FrameAt(source, index);

            output.Add((float)(left + ((right - left) * fraction)));
            _position += ratio;
        }

        _previous = FrameAt(source, frames - 1);
        _hasPrevious = true;
        _position -= frames;
        return [.. output];
    }

    private float FrameAt(ReadOnlySpan<float> source, int frame)
    {
        if (_channels == 1)
            return source[frame];

        var start = frame * _channels;
        var sum = 0f;
        for (var channel = 0; channel < _channels; channel++)
            sum += source[start + channel];

        return sum / _channels;
    }
}

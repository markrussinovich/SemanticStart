using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SemanticStart.Core.Speech;

/// <summary>
/// Silero VAD, run on the ONNX Runtime the app already loads for embeddings.
///
/// <para>
/// Knowing when the user has stopped talking is what makes dictation feel finished rather than
/// abandoned, and it cannot be answered by loudness: a quiet room has a noise floor, and any
/// energy threshold that survives a fan is tripped by a keyboard. Silero is a 2 MB model that
/// answers "is this frame speech" directly, and reusing the runtime already in the process means
/// it costs one more session rather than another native dependency.
/// </para>
/// <para>
/// The model is the v5 graph: each 512-sample frame at 16 kHz is preceded by 64 context samples,
/// along with a carried recurrent state, and returns one speech probability. Both context and
/// recurrent state belong to one utterance and are cleared by <see cref="Reset"/>.
/// </para>
/// </summary>
public sealed class SileroVoiceActivityDetector : IVoiceActivityDetector
{
    /// <summary>The v5 model's fixed frame size at 16 kHz: 512 samples, 32 ms.</summary>
    public const int FrameSampleCount = 512;

    private const int StateDimensions = 128;

    private readonly InferenceSession _session;
    private readonly long[] _sampleRate = [MonoFloatResampler.TargetSampleRate];
    private readonly SileroFrameBuffer _frames = new();
    private float[] _state = new float[2 * StateDimensions];
    private bool _disposed;

    public SileroVoiceActivityDetector(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Silero VAD model was not found.", modelPath);

        // One thread and sequential execution: the work is a 2 MB model on 32 ms of audio every
        // 32 ms, so scheduling it across cores costs more than it saves and competes with the
        // recognizer, which is the part of the pipeline that is actually latency-bound.
        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
        };

        _session = new InferenceSession(modelPath, sessionOptions);
    }

    public int FrameSamples => FrameSampleCount;

    /// <summary>How much audio one frame covers: 32 ms.</summary>
    public TimeSpan FrameDuration { get; } =
        TimeSpan.FromSeconds((double)FrameSampleCount / MonoFloatResampler.TargetSampleRate);

    /// <summary>
    /// The probability that <paramref name="frame"/> is speech. The frame must be exactly
    /// <see cref="FrameSampleCount"/> samples; the model's shape is fixed and a short frame would be
    /// silently misread as quieter speech.
    /// </summary>
    public float Process(ReadOnlySpan<float> frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var input = _frames.Accept(frame);

        var state = new DenseTensor<float>(_state, [2, 1, StateDimensions]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("state", state),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(_sampleRate, [1])),
        };

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);

        // Named rather than positional, and with no fallback: the other output is the recurrent
        // state, and reading a state value as a speech probability would not fail - it would
        // quietly endpoint at the wrong moments for the rest of the session.
        var probability = results.FirstOrDefault(result => result.Name == "output")
            ?? throw new InvalidOperationException(
                "The Silero VAD model has no 'output' tensor, so it is not the expected v5 model.");

        if (results.FirstOrDefault(result => result.Name == "stateN") is { } nextState)
            _state = [.. nextState.AsTensor<float>()];

        return probability.AsTensor<float>().First();
    }

    /// <summary>Forgets the current utterance. Call before each dictation session.</summary>
    public void Reset()
    {
        _state = new float[2 * StateDimensions];
        _frames.Reset();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _session.Dispose();
        _disposed = true;
    }

    /// <summary>The v5.1 ONNX wrapper's 64-sample lookback at 16 kHz, independent of model inference.</summary>
    internal sealed class SileroFrameBuffer
    {
        internal const int ContextSamples = 64;
        private readonly float[] _context = new float[ContextSamples];

        internal DenseTensor<float> Accept(ReadOnlySpan<float> frame)
        {
            if (frame.Length != SileroVoiceActivityDetector.FrameSampleCount)
                throw new ArgumentException(
                    $"Silero VAD expects frames of exactly {SileroVoiceActivityDetector.FrameSampleCount} samples.",
                    nameof(frame));

            var samples = new float[ContextSamples + frame.Length];
            _context.CopyTo(samples, 0);
            frame.CopyTo(samples.AsSpan(ContextSamples));
            frame[^ContextSamples..].CopyTo(_context);
            return new DenseTensor<float>(samples, [1, samples.Length]);
        }

        internal void Reset() => Array.Clear(_context);
    }
}

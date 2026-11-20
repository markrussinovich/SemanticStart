using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SemanticStart.Core.Embeddings;

internal sealed class OnnxTextScorer : IDisposable
{
    private readonly InferenceSession _session;
    private readonly bool _hasTokenTypeIds;
    private bool _disposed;

    public OnnxTextScorer(string modelPath, int intraOpNumThreads = 2)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intraOpNumThreads);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("ONNX embedding model was not found.", modelPath);

        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Min(intraOpNumThreads, Environment.ProcessorCount),
            InterOpNumThreads = 1,
        };
        sessionOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
        sessionOptions.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");

        _session = new InferenceSession(modelPath, sessionOptions);
        var inputMetadata = _session.InputMetadata;
        if (!inputMetadata.ContainsKey("input_ids") || !inputMetadata.ContainsKey("attention_mask"))
        {
            throw new InvalidOperationException(
                "The ONNX embedding model must declare input_ids and attention_mask inputs.");
        }

        _hasTokenTypeIds = inputMetadata.ContainsKey("token_type_ids");
    }

    public bool HasTokenTypeIds => _hasTokenTypeIds;

    public HiddenStateBatch Score(TokenizedBatch tokens, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (tokens.BatchSize == 0)
            return HiddenStateBatch.Empty;

        var dimensions = new[] { tokens.BatchSize, tokens.SequenceLength };
        var inputIds = new DenseTensor<long>(dimensions);
        var attentionMask = new DenseTensor<long>(dimensions);
        var tokenTypeIds = _hasTokenTypeIds ? new DenseTensor<long>(dimensions) : null;

        for (var batch = 0; batch < tokens.BatchSize; batch++)
        {
            var offset = batch * tokens.SequenceLength;
            for (var token = 0; token < tokens.SequenceLength; token++)
            {
                var index = offset + token;
                inputIds[batch, token] = tokens.InputIds[index];
                attentionMask[batch, token] = tokens.AttentionMask[index];
                if (tokenTypeIds is not null && tokens.TokenTypeIds is not null)
                    tokenTypeIds[batch, token] = tokens.TokenTypeIds[index];
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };

        if (tokenTypeIds is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
        var output = results.FirstOrDefault(result => result.Name == "last_hidden_state")
            ?? results.FirstOrDefault()
            ?? throw new InvalidOperationException("The ONNX embedding model returned no outputs.");
        var tensor = output.AsTensor<float>();

        if (tensor.Dimensions.Length != 3
            || tensor.Dimensions[0] != tokens.BatchSize
            || tensor.Dimensions[1] < tokens.SequenceLength
            || tensor.Dimensions[2] <= 0)
        {
            throw new InvalidOperationException(
                $"Expected hidden states with shape [{tokens.BatchSize}, at least {tokens.SequenceLength}, hidden], " +
                $"got [{string.Join(", ", tensor.Dimensions.ToArray())}].");
        }

        var hiddenDimensions = tensor.Dimensions[2];
        var values = new float[checked(tokens.BatchSize * tokens.SequenceLength * hiddenDimensions)];
        var indexInOutput = 0;
        for (var batch = 0; batch < tokens.BatchSize; batch++)
        {
            for (var token = 0; token < tokens.SequenceLength; token++)
            {
                for (var dimension = 0; dimension < hiddenDimensions; dimension++)
                    values[indexInOutput++] = tensor[batch, token, dimension];
            }
        }

        return new HiddenStateBatch(values, tokens.BatchSize, tokens.SequenceLength, hiddenDimensions);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _session.Dispose();
        _disposed = true;
    }
}

internal readonly record struct HiddenStateBatch(
    float[] Values,
    int BatchSize,
    int SequenceLength,
    int Dimensions)
{
    public static HiddenStateBatch Empty => new([], 0, 0, 0);
}

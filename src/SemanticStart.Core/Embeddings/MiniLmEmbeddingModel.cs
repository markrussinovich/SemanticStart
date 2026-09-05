using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using SemanticStart.Core.Abstractions;

namespace SemanticStart.Core.Embeddings;

public sealed class MiniLmEmbeddingModel : IEmbeddingModel
{
    private const int MaxSequenceLength = 256;
    private const int ExpectedDimensions = 384;

    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _hasInputIds;
    private readonly bool _hasAttentionMask;
    private readonly bool _hasTokenTypeIds;
    private bool _disposed;

    public MiniLmEmbeddingModel(string modelPath, string vocabPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("Model path is required.", nameof(modelPath));
        }

        if (string.IsNullOrWhiteSpace(vocabPath))
        {
            throw new ArgumentException("Vocabulary path is required.", nameof(vocabPath));
        }

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("ONNX embedding model was not found.", modelPath);
        }

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException("Embedding model vocabulary was not found.", vocabPath);
        }

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        _session = new InferenceSession(modelPath, sessionOptions);
        _tokenizer = BertTokenizer.Create(
            vocabPath,
            new BertOptions
            {
                LowerCaseBeforeTokenization = true,
                ApplyBasicTokenization = true,
                SplitOnSpecialTokens = true,
                SeparatorToken = "[SEP]",
                PaddingToken = "[PAD]",
                ClassificationToken = "[CLS]",
                MaskingToken = "[MASK]",
                IndividuallyTokenizeCjk = true,
                RemoveNonSpacingMarks = true,
            });

        _hasInputIds = _session.InputMetadata.ContainsKey("input_ids");
        _hasAttentionMask = _session.InputMetadata.ContainsKey("attention_mask");
        _hasTokenTypeIds = _session.InputMetadata.ContainsKey("token_type_ids");

        if (!_hasInputIds || !_hasAttentionMask)
        {
            throw new InvalidOperationException("The ONNX embedding model must declare input_ids and attention_mask inputs.");
        }
    }

    public int Dimensions => ExpectedDimensions;

    public string ModelId => "all-MiniLM-L6-v2";

    public Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (texts is null)
        {
            throw new ArgumentNullException(nameof(texts));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>());
        }

        return Task.Run(() => EmbedCore(texts, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<float[]> EmbedCore(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        var encoded = new long[texts.Count][];
        int sequenceLength = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoded[i] = Encode(texts[i] ?? string.Empty);
            sequenceLength = Math.Max(sequenceLength, encoded[i].Length);
        }

        sequenceLength = Math.Max(sequenceLength, 1);
        var dimensions = new[] { texts.Count, sequenceLength };
        var inputIds = new DenseTensor<long>(dimensions);
        var attentionMask = new DenseTensor<long>(dimensions);
        var tokenTypeIds = _hasTokenTypeIds ? new DenseTensor<long>(dimensions) : null;

        for (int batch = 0; batch < encoded.Length; batch++)
        {
            long[] ids = encoded[batch];
            for (int token = 0; token < ids.Length; token++)
            {
                inputIds[batch, token] = ids[token];
                attentionMask[batch, token] = 1;
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask),
        };

        // Some all-MiniLM exports omit token_type_ids; feed it only when the model declares it.
        if (_hasTokenTypeIds && tokenTypeIds is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(inputs);
        DisposableNamedOnnxValue output = results.FirstOrDefault(result => result.Name == "last_hidden_state")
            ?? results.First();
        Tensor<float> lastHiddenState = output.AsTensor<float>();
        return MeanPoolAndNormalize(lastHiddenState, attentionMask, texts.Count, sequenceLength);
    }

    private long[] Encode(string text)
    {
        IReadOnlyList<int> ids = _tokenizer.EncodeToIds(
            text,
            MaxSequenceLength,
            addSpecialTokens: true,
            out _,
            out _,
            considerPreTokenization: true,
            considerNormalization: true);

        var result = new long[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            result[i] = ids[i];
        }

        return result;
    }

    private static IReadOnlyList<float[]> MeanPoolAndNormalize(
        Tensor<float> lastHiddenState,
        Tensor<long> attentionMask,
        int batchSize,
        int sequenceLength)
    {
        if (lastHiddenState.Dimensions.Length != 3 || lastHiddenState.Dimensions[2] != ExpectedDimensions)
        {
            throw new InvalidOperationException(
                $"Expected last_hidden_state with shape [batch, sequence, {ExpectedDimensions}], got [{string.Join(", ", lastHiddenState.Dimensions.ToArray())}].");
        }

        var vectors = new float[batchSize][];
        for (int batch = 0; batch < batchSize; batch++)
        {
            var vector = new float[ExpectedDimensions];
            int tokenCount = 0;

            for (int token = 0; token < sequenceLength; token++)
            {
                if (attentionMask[batch, token] == 0)
                {
                    continue;
                }

                tokenCount++;
                for (int dimension = 0; dimension < ExpectedDimensions; dimension++)
                {
                    vector[dimension] += lastHiddenState[batch, token, dimension];
                }
            }

            // Sentence-transformers all-MiniLM uses attention-mask mean pooling, then L2 normalization.
            float scale = 1.0f / Math.Max(tokenCount, 1);
            float sumSquares = 0;
            for (int dimension = 0; dimension < ExpectedDimensions; dimension++)
            {
                vector[dimension] *= scale;
                sumSquares += vector[dimension] * vector[dimension];
            }

            float norm = MathF.Sqrt(sumSquares);
            if (norm > 0)
            {
                float inverseNorm = 1.0f / norm;
                for (int dimension = 0; dimension < ExpectedDimensions; dimension++)
                {
                    vector[dimension] *= inverseNorm;
                }
            }

            vectors[batch] = vector;
        }

        return vectors;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Dispose();
        _disposed = true;
    }
}

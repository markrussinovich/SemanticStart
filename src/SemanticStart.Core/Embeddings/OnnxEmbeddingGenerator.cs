using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace SemanticStart.Core.Embeddings;

/// <summary>
/// Microsoft.Extensions.AI embedding provider backed by a local ONNX sentence-transformer model.
/// The tokenizer, ONNX scorer, and pooling stages stay replaceable inside this facade while the
/// rest of the application depends only on IEmbeddingGenerator.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string DefaultModelId = "all-MiniLM-L6-v2";
    public const int DefaultDimensions = 384;

    private readonly Tokenizer _tokenizer;
    private readonly OnnxTextScorer _scorer;
    private readonly EmbeddingGeneratorMetadata _metadata;
    private readonly string _modelId;
    private readonly int _dimensions;
    private bool _disposed;

    /// <summary>Creates a provider using the default uncased BERT tokenizer and the supplied vocabulary.</summary>
    public OnnxEmbeddingGenerator(
        string modelPath,
        string vocabPath,
        string modelId = DefaultModelId,
        int dimensions = DefaultDimensions)
        : this(modelPath, CreateDefaultTokenizer(vocabPath), modelId, dimensions)
    {
    }

    /// <summary>Creates a provider using a caller-supplied tokenizer compatible with the ONNX model.</summary>
    /// <remarks>
    /// Custom tokenizers must return complete model-input sequences, including required special tokens,
    /// within a 256-token budget. Batches use right padding with ID zero and single-sequence token-type
    /// IDs of zero. Stock <see cref="BertTokenizer"/> instances retain BERT special-token handling.
    /// The caller owns the tokenizer and must ensure it is thread-safe for concurrent generation calls.
    /// Changing tokenization changes embeddings: rebuild existing indexes and use a distinct model ID
    /// when the effective encoding changes.
    /// </remarks>
    public OnnxEmbeddingGenerator(
        string modelPath,
        Tokenizer tokenizer,
        string modelId = DefaultModelId,
        int dimensions = DefaultDimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        _modelId = modelId;
        _dimensions = dimensions;
        _tokenizer = tokenizer;
        _scorer = new OnnxTextScorer(modelPath);

        _metadata = new EmbeddingGeneratorMetadata(
            providerName: "ONNX Runtime",
            defaultModelId: _modelId,
            defaultModelDimensions: _dimensions);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(values);
        ValidateOptions(options);

        var texts = values as IReadOnlyList<string> ?? values.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0)
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>());

        return Task.Run(() => GenerateCore(texts, cancellationToken), cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : null;
    }

    private GeneratedEmbeddings<Embedding<float>> GenerateCore(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var tokenized = _tokenizer.CreateOnnxBatch(texts, _scorer.HasTokenTypeIds, cancellationToken);
        var hiddenStates = _scorer.Score(tokenized, cancellationToken);
        var vectors = EmbeddingPooling.MeanPoolAndNormalize(hiddenStates, tokenized);

        var generated = new GeneratedEmbeddings<Embedding<float>>(vectors.Count);
        foreach (var vector in vectors)
        {
            if (vector.Length != _dimensions)
            {
                throw new InvalidOperationException(
                    $"ONNX embedding model returned {vector.Length} dimensions; expected {_dimensions}.");
            }

            generated.Add(new Embedding<float>(vector.AsMemory())
            {
                ModelId = _modelId,
            });
        }

        return generated;
    }

    private static BertTokenizer CreateDefaultTokenizer(string vocabPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabPath);
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException("Embedding model vocabulary was not found.", vocabPath);

        return BertTokenizer.Create(
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
    }

    private void ValidateOptions(EmbeddingGenerationOptions? options)
    {
        if (options?.ModelId is { } modelId
            && !string.Equals(modelId, _modelId, StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"This embedding generator serves model '{_modelId}', not '{modelId}'.");
        }

        if (options?.Dimensions is { } dimensions && dimensions != _dimensions)
        {
            throw new NotSupportedException(
                $"This embedding generator serves {_dimensions} dimensions, not {dimensions}.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _scorer.Dispose();
        _disposed = true;
    }
}

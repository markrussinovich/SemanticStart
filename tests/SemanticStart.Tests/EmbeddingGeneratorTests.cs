using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using SemanticStart.Core.Embeddings;

namespace SemanticStart.Tests;

public sealed class EmbeddingGeneratorTests
{
    private const string FixtureModelId = "tokenizer-fixture";
    // The fixture maps each input token ID to [id, 1]; the provider applies attention masks and pooling.
    private static string FixtureModelPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tokenizer-embedding.onnx");

    [Fact]
    public async Task GenerateVectorsAsync_CopiesNativeEmbeddings()
    {
        using var generator = new FakeEmbeddingGenerator(text => [text.Length, 1f, 0f, 0f]);

        var vectors = await generator.GenerateVectorsAsync(["a", "long"], 4);

        Assert.Equal(2, vectors.Count);
        Assert.Equal([1f, 1f, 0f, 0f], vectors[0]);
        Assert.Equal([4f, 1f, 0f, 0f], vectors[1]);
    }

    [Fact]
    public async Task GenerateVectorsAsync_RejectsUnexpectedDimensions()
    {
        using var generator = new FakeEmbeddingGenerator(_ => [1f, 2f]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => generator.GenerateVectorsAsync(["text"], 4));

        Assert.Contains("expected 4", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetRequiredMetadata_RejectsProvidersWithoutMetadata()
    {
        using var generator = new MetadataLessEmbeddingGenerator();

        var exception = Assert.Throws<InvalidOperationException>(
            () => generator.GetRequiredMetadata());

        Assert.Contains(nameof(EmbeddingGeneratorMetadata), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MeanPoolAndNormalize_UsesOnlyAttentionTokens()
    {
        var hiddenStates = new HiddenStateBatch(
            Values:
            [
                1f, 0f,
                100f, 100f,
                3f, 4f,
            ],
            BatchSize: 1,
            SequenceLength: 3,
            Dimensions: 2);
        var tokens = new TokenizedBatch(
            InputIds: [1, 2, 3],
            AttentionMask: [1, 0, 1],
            TokenTypeIds: null,
            BatchSize: 1,
            SequenceLength: 3);

        var vectors = EmbeddingPooling.MeanPoolAndNormalize(hiddenStates, tokens);

        var vector = Assert.Single(vectors);
        Assert.InRange(vector[0], 0.7071f, 0.7072f);
        Assert.InRange(vector[1], 0.7071f, 0.7072f);
    }

    [Theory]
    [InlineData(10, 20, 30)]
    [InlineData(5, 7, 9)]
    public async Task GenerateAsync_InjectedTokenizer_UsesCustomEncodingWithoutVocabulary(int first, int second, int shortId)
    {
        using var custom = new RecordingTokenizer(text => text == "long" ? [first, second] : [shortId]);
        Tokenizer tokenizer = custom;
        using var generator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 2);

        var embeddings = await generator.GenerateAsync(["long", "short"]);

        Assert.Equal(2, embeddings.Count);
        AssertNormalizedVector((first + second) / 2f, embeddings[0].Vector);
        AssertNormalizedVector(shortId, embeddings[1].Vector);
        Assert.All(embeddings, embedding => Assert.Equal(FixtureModelId, embedding.ModelId));
        Assert.Equal(new[] { ("long", 256, true, true), ("short", 256, true, true) }, custom.Calls);
        var metadata = generator.GetRequiredMetadata();
        Assert.Equal(FixtureModelId, metadata.DefaultModelId);
        Assert.Equal(2, metadata.DefaultModelDimensions);
        Assert.Equal("ONNX Runtime", metadata.ProviderName);
    }

    [Fact]
    public async Task GenerateAsync_DefaultAndInjectedBert_PreserveBaselineEmbeddings()
    {
        using var vocab = new TestBertVocabulary();
        using var defaultGenerator = new OnnxEmbeddingGenerator(FixtureModelPath, vocab.Path, FixtureModelId, dimensions: 2);
        Tokenizer tokenizer = vocab.CreateTokenizer();
        using var injectedGenerator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 2);
        string[] texts =
        [
            "", " \t\r\n ", "hello world", "HELLO, worlds!", "CAF\u00c9",
            "\u4e2d\u6587", "unlisted", "[MASK]", null!,
            string.Join(' ', Enumerable.Repeat("hello", 400)),
        ];
        float[] expectedMeans = [2.5f, 2.5f, 4f, 43f / 7f, 14f / 3f, 6.5f, 2f, 3f, 2.5f, 1275f / 256f];

        var defaultEmbeddings = await defaultGenerator.GenerateAsync(texts);
        var injectedEmbeddings = await injectedGenerator.GenerateAsync(texts);

        Assert.Equal(texts.Length, defaultEmbeddings.Count);
        Assert.Equal(texts.Length, injectedEmbeddings.Count);
        for (var i = 0; i < texts.Length; i++)
        {
            AssertNormalizedVector(expectedMeans[i], defaultEmbeddings[i].Vector);
            Assert.Equal(defaultEmbeddings[i].Vector.ToArray(), injectedEmbeddings[i].Vector.ToArray());
        }
    }

    [Fact]
    public async Task GenerateAsync_EmptyInput_DoesNotInvokeInjectedTokenizer()
    {
        using var tokenizer = new RecordingTokenizer(_ => [10]);
        using var generator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 2);

        var embeddings = await generator.GenerateAsync([]);

        Assert.Empty(embeddings);
        Assert.Empty(tokenizer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerateAsync_CanceledBeforeWork_DoesNotInvokeInjectedTokenizer(bool emptyInput)
    {
        using var tokenizer = new RecordingTokenizer(_ => [10]);
        using var generator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            generator.GenerateAsync(emptyInput ? [] : ["text"], cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(tokenizer.Calls);
    }

    [Theory]
    [InlineData("different-model", 2)]
    [InlineData(null, 3)]
    public async Task GenerateAsync_UnsupportedOptions_FailsBeforeTokenizing(string? modelId, int dimensions)
    {
        using var tokenizer = new RecordingTokenizer(_ => [10]);
        using var generator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 2);
        var options = new EmbeddingGenerationOptions { ModelId = modelId, Dimensions = dimensions };

        await Assert.ThrowsAsync<NotSupportedException>(() => generator.GenerateAsync(["text"], options));

        Assert.Empty(tokenizer.Calls);
    }

    [Fact]
    public async Task GenerateAsync_UnexpectedModelDimensions_Throws()
    {
        using var tokenizer = new RecordingTokenizer(_ => [10]);
        using var generator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 3);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync(["text"]));

        Assert.Contains("returned 2 dimensions; expected 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_DoesNotTakeOwnershipOfInjectedTokenizer()
    {
        using var tokenizer = new RecordingTokenizer(_ => [10]);
        using var generator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 2);

        generator.Dispose();
        generator.Dispose();

        Assert.False(tokenizer.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => generator.GenerateAsync(["text"]));
        Assert.Empty(tokenizer.Calls);
        using var anotherGenerator = new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, FixtureModelId, dimensions: 2);
        var embeddings = await anotherGenerator.GenerateAsync(["text"]);
        AssertNormalizedVector(10f, Assert.Single(embeddings).Vector);
    }

    [Fact]
    public void Constructor_NullTokenizer_Throws()
    {
        Assert.Throws<ArgumentNullException>("tokenizer", () =>
            new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer: null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_InvalidModelPath_Throws(string? modelPath)
    {
        using var vocab = new TestBertVocabulary();
        using var tokenizer = new RecordingTokenizer(_ => [10]);

        var injected = Assert.ThrowsAny<ArgumentException>(() => new OnnxEmbeddingGenerator(modelPath!, tokenizer));
        var defaulted = Assert.ThrowsAny<ArgumentException>(() => new OnnxEmbeddingGenerator(modelPath!, vocab.Path));

        Assert.Equal("modelPath", injected.ParamName);
        Assert.Equal("modelPath", defaulted.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_InvalidVocabularyPath_Throws(string? vocabPath)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            new OnnxEmbeddingGenerator(FixtureModelPath, vocabPath: vocabPath!));

        Assert.Equal("vocabPath", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_InvalidModelId_Throws(string? modelId)
    {
        using var vocab = new TestBertVocabulary();
        using var tokenizer = new RecordingTokenizer(_ => [10]);

        var injected = Assert.ThrowsAny<ArgumentException>(() =>
            new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, modelId!));
        var defaulted = Assert.ThrowsAny<ArgumentException>(() =>
            new OnnxEmbeddingGenerator(FixtureModelPath, vocab.Path, modelId!));

        Assert.Equal("modelId", injected.ParamName);
        Assert.Equal("modelId", defaulted.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonpositiveDimensions_Throws(int dimensions)
    {
        using var vocab = new TestBertVocabulary();
        using var tokenizer = new RecordingTokenizer(_ => [10]);

        Assert.Throws<ArgumentOutOfRangeException>("dimensions", () =>
            new OnnxEmbeddingGenerator(FixtureModelPath, tokenizer, dimensions: dimensions));
        Assert.Throws<ArgumentOutOfRangeException>("dimensions", () =>
            new OnnxEmbeddingGenerator(FixtureModelPath, vocab.Path, dimensions: dimensions));
    }

    [Fact]
    public void Constructor_MissingFiles_ReportsTheMissingPath()
    {
        using var vocab = new TestBertVocabulary();
        using var tokenizer = new RecordingTokenizer(_ => [10]);
        var missingPath = vocab.Path + ".missing";

        var injectedModel = Assert.Throws<FileNotFoundException>(() => new OnnxEmbeddingGenerator(missingPath, tokenizer));
        var defaultModel = Assert.Throws<FileNotFoundException>(() => new OnnxEmbeddingGenerator(missingPath, vocab.Path));
        var vocabulary = Assert.Throws<FileNotFoundException>(() => new OnnxEmbeddingGenerator(FixtureModelPath, missingPath));

        Assert.Equal(missingPath, injectedModel.FileName);
        Assert.Equal(missingPath, defaultModel.FileName);
        Assert.Equal(missingPath, vocabulary.FileName);
        Assert.Contains("model", injectedModel.Message, StringComparison.Ordinal);
        Assert.Contains("vocabulary", vocabulary.Message, StringComparison.Ordinal);
    }

    private static void AssertNormalizedVector(float meanId, ReadOnlyMemory<float> vector)
    {
        var norm = MathF.Sqrt(meanId * meanId + 1f);
        Assert.Equal(2, vector.Length);
        Assert.InRange(vector.Span[0], meanId / norm - 0.000001f, meanId / norm + 0.000001f);
        Assert.InRange(vector.Span[1], 1f / norm - 0.000001f, 1f / norm + 0.000001f);
    }

    private sealed class FakeEmbeddingGenerator(Func<string, float[]> createVector) : TestEmbeddingGenerator(createVector);

    private sealed class MetadataLessEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
                generated.Add(new Embedding<float>(new[] { value.Length, 1f }.AsMemory()));

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

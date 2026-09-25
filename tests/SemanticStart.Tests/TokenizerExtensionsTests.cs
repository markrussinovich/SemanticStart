using Microsoft.ML.Tokenizers;
using SemanticStart.Core.Embeddings;

namespace SemanticStart.Tests;

public sealed class TokenizerExtensionsTests
{
    [Theory]
    [InlineData("", new long[] { 2, 3 })]
    [InlineData(" \t\r\n ", new long[] { 2, 3 })]
    [InlineData("hello world", new long[] { 2, 5, 6, 3 })]
    [InlineData("HELLO, worlds!", new long[] { 2, 5, 12, 6, 7, 8, 3 })]
    [InlineData("CAF\u00c9", new long[] { 2, 9, 3 })]
    [InlineData("\u4e2d\u6587", new long[] { 2, 10, 11, 3 })]
    [InlineData("unlisted", new long[] { 2, 1, 3 })]
    [InlineData("[MASK]", new long[] { 2, 4, 3 })]
    public void CreateOnnxBatch_Bert_PreservesDefaultEncoding(string text, long[] expected)
    {
        using var vocab = new TestBertVocabulary();
        Tokenizer tokenizer = vocab.CreateTokenizer();

        var batch = tokenizer.CreateOnnxBatch([text], false, CancellationToken.None);

        Assert.Equal(expected, batch.InputIds);
        Assert.Equal(Enumerable.Repeat(1L, expected.Length), batch.AttentionMask);
        Assert.Null(batch.TokenTypeIds);
        Assert.Equal(1, batch.BatchSize);
        Assert.Equal(expected.Length, batch.SequenceLength);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateOnnxBatch_Bert_PadsUnequalSequencesAndTreatsNullAsEmpty(bool includeTokenTypeIds)
    {
        using var vocab = new TestBertVocabulary();
        Tokenizer tokenizer = vocab.CreateTokenizer();

        var batch = tokenizer.CreateOnnxBatch(["hello world", "hello", null!], includeTokenTypeIds, CancellationToken.None);

        Assert.Equal(new long[] { 2, 5, 6, 3, 2, 5, 3, 0, 2, 3, 0, 0 }, batch.InputIds);
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 0, 0 }, batch.AttentionMask);
        Assert.Equal(3, batch.BatchSize);
        Assert.Equal(4, batch.SequenceLength);
        if (includeTokenTypeIds)
            Assert.Equal(new long[12], batch.TokenTypeIds);
        else
            Assert.Null(batch.TokenTypeIds);
    }

    [Theory]
    [InlineData(253)]
    [InlineData(254)]
    [InlineData(255)]
    [InlineData(400)]
    public void CreateOnnxBatch_Bert_ReservesSpaceForSpecialTokensAtLimit(int contentTokens)
    {
        using var vocab = new TestBertVocabulary();
        Tokenizer tokenizer = vocab.CreateTokenizer();
        var text = string.Join(' ', Enumerable.Repeat("hello", contentTokens));
        long[] expected = [2, .. Enumerable.Repeat(5L, Math.Min(contentTokens, 254)), 3];

        var batch = tokenizer.CreateOnnxBatch([text], false, CancellationToken.None);

        Assert.Equal(expected, batch.InputIds);
        Assert.Equal(Enumerable.Repeat(1L, expected.Length), batch.AttentionMask);
        Assert.Equal(expected.Length, batch.SequenceLength);
        Assert.Equal(1, batch.BatchSize);
    }

    [Fact]
    public void CreateOnnxBatch_InjectedBert_PreservesItsNormalizationSettings()
    {
        using var vocab = new TestBertVocabulary();
        Tokenizer tokenizer = vocab.CreateTokenizer(lowerCase: false);

        var batch = tokenizer.CreateOnnxBatch(["HELLO"], false, CancellationToken.None);

        Assert.Equal(new long[] { 2, 1, 3 }, batch.InputIds);
        Assert.Equal(new long[] { 1, 1, 1 }, batch.AttentionMask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateOnnxBatch_CustomTokenizer_UsesVirtualHookAndPreservesIds(bool includeTokenTypeIds)
    {
        using var custom = new RecordingTokenizer(text => text == "CAF\u00c9" ? [90, 0, 91] : [40]);
        Tokenizer tokenizer = custom;

        var batch = tokenizer.CreateOnnxBatch(["CAF\u00c9", "\u4e2d"], includeTokenTypeIds, CancellationToken.None);

        Assert.Equal(new long[] { 90, 0, 91, 40, 0, 0 }, batch.InputIds);
        Assert.Equal(new long[] { 1, 1, 1, 1, 0, 0 }, batch.AttentionMask);
        Assert.Equal(2, batch.BatchSize);
        Assert.Equal(3, batch.SequenceLength);
        Assert.Equal(
            new[] { ("CAF\u00c9", 256, true, true), ("\u4e2d", 256, true, true) },
            custom.Calls);
        if (includeTokenTypeIds)
            Assert.Equal(new long[6], batch.TokenTypeIds);
        else
            Assert.Null(batch.TokenTypeIds);
    }

    [Fact]
    public void CreateOnnxBatch_CustomTokenizer_AllowsExactlyTheTokenBudget()
    {
        int[] ids = [90, .. Enumerable.Repeat(7, 254), 91];
        using var tokenizer = new RecordingTokenizer(_ => ids);

        var batch = tokenizer.CreateOnnxBatch(["text"], false, CancellationToken.None);

        Assert.Equal(ids.Select(id => (long)id), batch.InputIds);
        Assert.Equal(Enumerable.Repeat(1L, 256), batch.AttentionMask);
        Assert.Equal(256, batch.SequenceLength);
    }

    [Fact]
    public void CreateOnnxBatch_CustomTokenizer_RejectsOverBudgetOutputWithoutExposingText()
    {
        const string privateText = "private input";
        using var tokenizer = new RecordingTokenizer(text => text == privateText ? new int[257] : [7]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            tokenizer.CreateOnnxBatch(["valid", privateText, "not reached"], false, CancellationToken.None));

        Assert.Contains("257 tokens for sequence 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("maximum is 256", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(privateText, exception.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "valid", privateText }, tokenizer.Calls.Select(call => call.Text));
    }

    [Fact]
    public void CreateOnnxBatch_EmptyBatch_DoesNotInvokeTokenizer()
    {
        using var tokenizer = new RecordingTokenizer(_ => [7]);

        var batch = tokenizer.CreateOnnxBatch([], true, CancellationToken.None);

        Assert.Empty(batch.InputIds);
        Assert.Empty(batch.AttentionMask);
        Assert.Null(batch.TokenTypeIds);
        Assert.Equal(0, batch.BatchSize);
        Assert.Equal(0, batch.SequenceLength);
        Assert.Empty(tokenizer.Calls);
    }

    [Fact]
    public void CreateOnnxBatch_EmptyEncodings_UsesOneMaskedPaddingPositionPerSequence()
    {
        using var tokenizer = new RecordingTokenizer(_ => []);

        var batch = tokenizer.CreateOnnxBatch([null!, ""], true, CancellationToken.None);

        Assert.Equal(new long[] { 0, 0 }, batch.InputIds);
        Assert.Equal(new long[] { 0, 0 }, batch.AttentionMask);
        Assert.Equal(new long[] { 0, 0 }, batch.TokenTypeIds);
        Assert.Equal(2, batch.BatchSize);
        Assert.Equal(1, batch.SequenceLength);
        Assert.Equal(new[] { "", "" }, tokenizer.Calls.Select(call => call.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateOnnxBatch_CanceledBeforeWork_DoesNotInvokeTokenizer(bool emptyBatch)
    {
        using var tokenizer = new RecordingTokenizer(_ => [7]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(() =>
            tokenizer.CreateOnnxBatch(emptyBatch ? [] : ["text"], false, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(tokenizer.Calls);
    }

    [Fact]
    public void CreateOnnxBatch_CanceledBetweenSequences_StopsBeforeNextEncoding()
    {
        using var cancellation = new CancellationTokenSource();
        using var tokenizer = new RecordingTokenizer(_ =>
        {
            cancellation.Cancel();
            return [7];
        });

        var exception = Assert.Throws<OperationCanceledException>(() =>
            tokenizer.CreateOnnxBatch(["first", "second"], false, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal("first", Assert.Single(tokenizer.Calls).Text);
    }

    [Fact]
    public void CreateOnnxBatch_TokenizerFailure_PropagatesWithoutFallback()
    {
        var failure = new FormatException("Tokenizer failed.");
        using var tokenizer = new RecordingTokenizer(_ => throw failure);

        var exception = Assert.Throws<FormatException>(() =>
            tokenizer.CreateOnnxBatch(["text"], false, CancellationToken.None));

        Assert.Same(failure, exception);
        Assert.Single(tokenizer.Calls);
    }
}

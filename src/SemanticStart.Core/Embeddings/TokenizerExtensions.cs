using Microsoft.ML.Tokenizers;

namespace SemanticStart.Core.Embeddings;

internal static class TokenizerExtensions
{
    private const int MaxSequenceLength = 256;

    public static TokenizedBatch CreateOnnxBatch(
        this Tokenizer tokenizer,
        IReadOnlyList<string> texts,
        bool includeTokenTypeIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();

        if (texts.Count == 0)
            return TokenizedBatch.Empty;

        var encoded = new long[texts.Count][];
        var sequenceLength = 0;
        for (var i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encoded[i] = Encode(tokenizer, texts[i] ?? string.Empty, i);
            sequenceLength = Math.Max(sequenceLength, encoded[i].Length);
        }

        sequenceLength = Math.Max(sequenceLength, 1);
        var elementCount = checked(texts.Count * sequenceLength);
        var inputIds = new long[elementCount];
        var attentionMask = new long[elementCount];
        var tokenTypeIds = includeTokenTypeIds ? new long[elementCount] : null;

        for (var batch = 0; batch < encoded.Length; batch++)
        {
            var ids = encoded[batch];
            var offset = batch * sequenceLength;
            ids.AsSpan().CopyTo(inputIds.AsSpan(offset));
            attentionMask.AsSpan(offset, ids.Length).Fill(1);
        }

        return new TokenizedBatch(
            inputIds,
            attentionMask,
            tokenTypeIds,
            texts.Count,
            sequenceLength);
    }

    private static long[] Encode(Tokenizer tokenizer, string text, int sequenceIndex)
    {
        // BERT hides the base encoding methods; its overload reserves space for [CLS] and [SEP].
        IReadOnlyList<int> ids = tokenizer is BertTokenizer bert
            ? bert.EncodeToIds(
                text,
                MaxSequenceLength,
                addSpecialTokens: true,
                out _,
                out _,
                considerPreTokenization: true,
                considerNormalization: true)
            : tokenizer.EncodeToIds(
                text,
                MaxSequenceLength,
                out _,
                out _,
                considerPreTokenization: true,
                considerNormalization: true);

        if (ids.Count > MaxSequenceLength)
        {
            throw new InvalidOperationException(
                $"Tokenizer returned {ids.Count} tokens for sequence {sequenceIndex}; " +
                $"the maximum is {MaxSequenceLength}, including special tokens.");
        }

        var result = new long[ids.Count];
        for (var i = 0; i < ids.Count; i++)
            result[i] = ids[i];

        return result;
    }
}

internal readonly record struct TokenizedBatch(
    long[] InputIds,
    long[] AttentionMask,
    long[]? TokenTypeIds,
    int BatchSize,
    int SequenceLength)
{
    public static TokenizedBatch Empty => new([], [], null, 0, 0);
}

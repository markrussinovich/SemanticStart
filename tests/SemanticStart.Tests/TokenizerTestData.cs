using System.Buffers;
using Microsoft.ML.Tokenizers;

namespace SemanticStart.Tests;

internal sealed class TestBertVocabulary : IDisposable
{
    public string Path { get; } = System.IO.Path.GetTempFileName();

    public TestBertVocabulary()
    {
        File.WriteAllLines(Path,
        [
            "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]",
            "hello", "world", "##s", "!", "cafe", "\u4e2d", "\u6587", ",",
        ]);
    }

    public BertTokenizer CreateTokenizer(bool lowerCase = true) =>
        BertTokenizer.Create(Path, new BertOptions
        {
            LowerCaseBeforeTokenization = lowerCase,
            ApplyBasicTokenization = true,
            SplitOnSpecialTokens = true,
            SeparatorToken = "[SEP]",
            PaddingToken = "[PAD]",
            ClassificationToken = "[CLS]",
            MaskingToken = "[MASK]",
            IndividuallyTokenizeCjk = true,
            RemoveNonSpacingMarks = true,
        });

    public void Dispose() => File.Delete(Path);
}

internal sealed class RecordingTokenizer(Func<string, IReadOnlyList<int>> encode) : Tokenizer, IDisposable
{
    public List<(string Text, int MaxTokenCount, bool PreTokenization, bool Normalization)> Calls { get; } = [];
    public bool IsDisposed { get; private set; }

    protected override EncodeResults<int> EncodeToIds(string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings)
    {
        var input = text ?? textSpan.ToString();
        Calls.Add((input, settings.MaxTokenCount, settings.ConsiderPreTokenization, settings.ConsiderNormalization));
        return new EncodeResults<int>
        {
            Tokens = encode(input),
            CharsConsumed = input.Length,
        };
    }

    protected override EncodeResults<EncodedToken> EncodeToTokens(
        string? text, ReadOnlySpan<char> textSpan, EncodeSettings settings)
    {
        var input = text ?? textSpan.ToString();
        var result = EncodeToIds(input, default, settings);
        return new EncodeResults<EncodedToken>
        {
            Tokens = result.Tokens.Select(id => new EncodedToken(id, input, 0..input.Length)).ToArray(),
            CharsConsumed = result.CharsConsumed,
        };
    }

    public override OperationStatus Decode(
        IEnumerable<int> ids, Span<char> destination, out int idsConsumed, out int charsWritten) =>
        throw new NotSupportedException("This test tokenizer supports encoding only.");

    public void Dispose() => IsDisposed = true;
}

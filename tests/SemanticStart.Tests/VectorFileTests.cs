using SemanticStart.Core.Storage;

namespace SemanticStart.Tests;

public sealed class VectorFileTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        "VectorFileTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void VectorFile_WritesReadsGrowsAndTruncates()
    {
        var path = Path.Combine(_directory, "vectors.bin");
        var vectors = new VectorFile(path, 3);

        Assert.Empty(vectors.ReadAll());

        vectors.Write(1, [1.0f, 2.0f, 3.0f]);
        Assert.Equal([0.0f, 0.0f, 0.0f, 1.0f, 2.0f, 3.0f], vectors.ReadAll());

        vectors.Write(0, [4.0f, 5.0f, 6.0f]);
        Assert.Equal([4.0f, 5.0f, 6.0f, 1.0f, 2.0f, 3.0f], vectors.ReadAll());

        vectors.Truncate();
        Assert.Empty(vectors.ReadAll());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}

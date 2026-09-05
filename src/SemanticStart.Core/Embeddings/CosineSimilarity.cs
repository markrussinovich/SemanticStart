using System.Numerics;

namespace SemanticStart.Core.Embeddings;

public static class CosineSimilarity
{
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException("Vectors must have the same length.", nameof(b));
        }

        int i = 0;
        float sum = 0;

        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var vectorSum = Vector<float>.Zero;
            for (; i <= a.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                vectorSum += new Vector<float>(a.Slice(i, Vector<float>.Count)) * new Vector<float>(b.Slice(i, Vector<float>.Count));
            }

            sum = Vector.Dot(vectorSum, Vector<float>.One);
        }

        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    public static IReadOnlyList<(int Ordinal, float Score)> TopK(
        ReadOnlySpan<float> query,
        ReadOnlySpan<float> rowMajorMatrix,
        int count,
        int dimensions,
        int k)
    {
        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (k < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k));
        }

        if (query.Length != dimensions)
        {
            throw new ArgumentException("Query vector length must match dimensions.", nameof(query));
        }

        if (rowMajorMatrix.Length < count * dimensions)
        {
            throw new ArgumentException("Matrix span is smaller than count x dimensions.", nameof(rowMajorMatrix));
        }

        int limit = Math.Min(k, count);
        if (limit == 0)
        {
            return Array.Empty<(int Ordinal, float Score)>();
        }

        var best = new (int Ordinal, float Score)[limit];
        Array.Fill(best, (-1, float.NegativeInfinity));

        for (int row = 0; row < count; row++)
        {
            float score = Dot(query, rowMajorMatrix.Slice(row * dimensions, dimensions));
            if (score <= best[^1].Score)
            {
                continue;
            }

            int insertAt = limit - 1;
            while (insertAt > 0 && score > best[insertAt - 1].Score)
            {
                best[insertAt] = best[insertAt - 1];
                insertAt--;
            }

            best[insertAt] = (row, score);
        }

        return best;
    }
}

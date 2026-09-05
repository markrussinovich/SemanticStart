using System.Numerics;
using System.Runtime.CompilerServices;

namespace SemanticStart.Core.Query;

/// <summary>
/// Vector math for the retrieval hot path.
///
/// Deliberately dependency-free and hand-vectorized rather than pulling in a tensor library:
/// the only operation the query path needs is a dot product over a few thousand short rows.
/// Because every stored vector and every query vector is L2-normalized by contract, the dot
/// product *is* the cosine similarity, so no per-row magnitude work is required.
/// </summary>
public static class VectorMath
{
    /// <summary>Dot product of two equal-length spans, using hardware SIMD where available.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors must have the same length.", nameof(b));

        var sum = 0f;
        var i = 0;

        if (Vector.IsHardwareAccelerated && a.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var limit = a.Length - (a.Length % Vector<float>.Count);

            for (; i < limit; i += Vector<float>.Count)
            {
                acc += new Vector<float>(a.Slice(i, Vector<float>.Count))
                       * new Vector<float>(b.Slice(i, Vector<float>.Count));
            }

            sum = Vector.Dot(acc, Vector<float>.One);
        }

        for (; i < a.Length; i++)
            sum += a[i] * b[i];

        return sum;
    }

    /// <summary>Scales a vector to unit length in place. No-op for a zero vector.</summary>
    public static void NormalizeInPlace(Span<float> vector)
    {
        var sumSquares = 0f;
        foreach (var v in vector)
            sumSquares += v * v;

        if (sumSquares <= 0f)
            return;

        var inverse = 1f / MathF.Sqrt(sumSquares);
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= inverse;
    }
}

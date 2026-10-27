using System.Collections;

namespace Switchknife.Facts;

/// <summary>
/// The two compressed forms an embedding is stored in (docs/adr/0003-facts-vector-storage.md): sign bits for the scan,
/// int8 for ranking. Cosine ignores a vector's length, so vectors are made unit length first and no scale is kept.
/// </summary>
internal static class Quantizer
{
    /// <summary>Refuses an empty vector, a NaN or infinity, and the zero vector (it has no direction).</summary>
    public static void Validate(ReadOnlySpan<float> vector, string parameterName)
    {
        if (vector.IsEmpty)
        {
            throw new ArgumentException("An embedding has at least one component.", parameterName);
        }

        var any = false;
        foreach (var x in vector)
        {
            if (!float.IsFinite(x))
            {
                throw new ArgumentException("An embedding holds a NaN or an infinity.", parameterName);
            }

            any |= x != 0;
        }

        if (!any)
        {
            throw new ArgumentException("The zero vector has no direction, so it has no cosine.", parameterName);
        }
    }

    /// <summary>The vector divided by its length.</summary>
    public static float[] Unit(ReadOnlySpan<float> vector)
    {
        double sum = 0;
        foreach (var x in vector)
        {
            sum += (double)x * x;
        }

        var norm = (float)Math.Sqrt(sum);
        var unit = new float[vector.Length];
        for (var i = 0; i < unit.Length; i++)
        {
            unit[i] = vector[i] / norm;
        }

        return unit;
    }

    /// <summary>One bit per component: set when the component is positive.</summary>
    public static BitArray Bits(ReadOnlySpan<float> vector)
    {
        var bits = new BitArray(vector.Length);
        for (var i = 0; i < vector.Length; i++)
        {
            bits[i] = vector[i] > 0;
        }

        return bits;
    }

    /// <summary>The components scaled so the largest magnitude is 127, rounded; each byte holds a signed value.</summary>
    public static byte[] Int8(ReadOnlySpan<float> unit)
    {
        var largest = 0f;
        foreach (var x in unit)
        {
            largest = Math.Max(largest, Math.Abs(x));
        }

        var bytes = new byte[unit.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = unchecked((byte)(sbyte)MathF.Round(unit[i] / largest * 127f));
        }

        return bytes;
    }

    /// <summary>Cosine between a unit query and an int8 vector (any scale).</summary>
    public static double Cosine(ReadOnlySpan<float> unitQuery, ReadOnlySpan<byte> int8)
    {
        if (unitQuery.Length != int8.Length)
        {
            throw new ArgumentException($"A stored vector has {int8.Length} components, the query {unitQuery.Length}.", nameof(int8));
        }

        double dot = 0, norm = 0;
        for (var i = 0; i < int8.Length; i++)
        {
            double q = unchecked((sbyte)int8[i]);
            dot += unitQuery[i] * q;
            norm += q * q;
        }

        return norm == 0 ? 0 : dot / Math.Sqrt(norm);
    }
}

namespace Switchknife.Facts.Tests;

public sealed class QuantizerTests
{
    [Fact]
    public void Sign_bits_are_set_for_positive_components_only()
    {
        var bits = Quantizer.Bits([1f, -1f, 0f, 2f]);

        Assert.Equal([true, false, false, true], bits.Cast<bool>());
    }

    [Fact]
    public void Int8_rounds_each_component_to_within_half_a_step_of_the_scaled_unit_vector()
    {
        var random = new Random(4);
        var unit = Quantizer.Unit(Enumerable.Range(0, 256).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
        var largest = unit.Max(Math.Abs);

        var q = Quantizer.Int8(unit);

        for (var i = 0; i < unit.Length; i++)
        {
            Assert.InRange(Math.Abs(unchecked((sbyte)q[i]) * largest / 127f - unit[i]), 0, largest / 127f / 2 + 1e-6f);
        }

        Assert.Equal(127, q.Max(b => Math.Abs(unchecked((sbyte)b))));
    }

    [Fact]
    public void Cosine_of_a_vector_with_itself_a_perpendicular_one_and_its_opposite_is_one_zero_and_minus_one()
    {
        float[] a = [3, 4, 0, 0];
        var unit = Quantizer.Unit(a);

        Assert.Equal(1, Quantizer.Cosine(unit, Quantizer.Int8(unit)), 2);
        Assert.Equal(0, Quantizer.Cosine(unit, Quantizer.Int8(Quantizer.Unit([-4f, 3f, 0f, 0f]))), 2);
        Assert.Equal(-1, Quantizer.Cosine(unit, Quantizer.Int8(Quantizer.Unit([-3f, -4f, 0f, 0f]))), 2);
    }

    [Fact]
    public void An_empty_non_finite_or_zero_vector_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Quantizer.Validate([], "v"));
        Assert.Throws<ArgumentException>(() => Quantizer.Validate([1f, float.NaN], "v"));
        Assert.Throws<ArgumentException>(() => Quantizer.Validate([1f, float.PositiveInfinity], "v"));
        Assert.Throws<ArgumentException>(() => Quantizer.Validate([0f, 0f], "v"));
        Quantizer.Validate([0f, 1e-20f], "v");
    }

    [Fact]
    public void A_stored_vector_of_another_length_than_the_query_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Quantizer.Cosine([1f, 0f], new byte[3]));
    }
}

using FsCheck.Fluent;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Units;

public class ConversionTests
{
    [Theory]
    [InlineData(1, "lb", "kg", 0.45359237)]
    [InlineData(1, "in", "cm", 2.54)]
    [InlineData(1, "mi", "m", 1609.344)]
    [InlineData(1, "oz", "g", 28.349523125)]
    [InlineData(1, "fl_oz", "ml", 29.5735295625)]
    [InlineData(1, "kcal", "kJ", 4.184)]
    [InlineData(1, "ft", "cm", 30.48)]
    [InlineData(1, "yd", "m", 0.9144)]
    [InlineData(1, "cup", "ml", 236.5882365)]
    [InlineData(1, "tbsp", "ml", 14.78676478125)]
    [InlineData(1, "tsp", "ml", 4.92892159375)]
    [InlineData(100, "mg/dL", "mmol/L", 100 / 18.0156)]
    [InlineData(100, "C", "F", 212)]
    [InlineData(-40, "F", "C", -40)]
    public void Exact_factors(double value, string from, string to, double expected)
    {
        Assert.True(Close(Conversion.Convert(value, Unit.Parse(from), Unit.Parse(to)), expected));
    }

    [Fact]
    public void Exact_pairs_multiply_numerators_first()
    {
        // value × a.num × b.den / (a.den × b.num), in that order (the Phase 0 prototype's doubles).
        Assert.Equal(225 * 0.45359237 * 1 / (1 * 1), Conversion.Convert(225, Unit.Pound, Unit.Kilogram));
        Assert.Equal(70 * 2.54 * 100 / (100 * 1), Conversion.Convert(70, Unit.Inch, Unit.Centimetre));
    }

    [Fact]
    public void Rejects_a_dimension_mismatch()
    {
        Assert.Throws<ArgumentException>(() => Conversion.Convert(1, Unit.Kilogram, Unit.Centimetre));
    }

    [Theory]
    [InlineData(Dimension.Mass)]
    [InlineData(Dimension.Length)]
    [InlineData(Dimension.Volume)]
    [InlineData(Dimension.Energy)]
    [InlineData(Dimension.Glucose)]
    [InlineData(Dimension.Temperature)]
    public void Every_unit_pair_round_trips(Dimension dimension)
    {
        var units = Unit.OfDimension(dimension).ToArray();
        var values = dimension == Dimension.Temperature ? Double(-1e6, 1e6) : Double(0, 1e6);

        Holds(Gen.Zip(values, Gen.Zip(Gen.Elements(units), Gen.Elements(units))), t =>
        {
            var (v, (a, b)) = t;
            var back = Conversion.Convert(Conversion.Convert(v, a, b), b, a);
            // Temperature is affine, so near 0 the error is absolute rather than relative.
            return dimension == Dimension.Temperature ? Math.Abs(back - v) < 1e-9 : Close(back, v);
        });
    }

    [Fact]
    public void Units_parse_from_their_codes()
    {
        foreach (var unit in Unit.All)
        {
            Assert.Same(unit, Unit.Parse(unit.Code));
        }

        Assert.Equal(Unit.All.Count, Unit.All.Select(u => u.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.False(Unit.TryParse("KJ", out _));
        Assert.False(Unit.TryParse(null, out _));
        Assert.Throws<FormatException>(() => Unit.Parse("stone"));
    }

    [Fact]
    public void Every_dimension_has_units_and_every_unit_one_dimension()
    {
        var byDimension = Enum.GetValues<Dimension>().SelectMany(Unit.OfDimension).ToList();

        Assert.All(Enum.GetValues<Dimension>(), d => Assert.NotEmpty(Unit.OfDimension(d)));
        Assert.Equal(Unit.All, byDimension);
    }
}

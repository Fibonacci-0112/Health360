using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Units;

/// <summary>
/// <see cref="UnitCodes"/> mirrors <c>private.convert_unit</c>; CanonicalUnitParityTests compares the two bit for bit
/// against a database. These pin the rules that don't need one.
/// </summary>
public class UnitCodesTests
{
    [Theory]
    [InlineData("lb", "kg")]
    [InlineData("kg", "lb")]
    [InlineData("g", "kg")]
    [InlineData("in", "cm")]
    [InlineData("kJ", "kcal")]
    [InlineData("mg/dL", "mmol/L")]
    [InlineData("F", "C")]
    public void Unit_codes_convert_exactly_as_Conversion_does(string from, string to) =>
        Holds(Double(-1000, 1000), value =>
            UnitCodes.Convert(value, from, to) == Conversion.Convert(value, Unit.Parse(from), Unit.Parse(to)));

    [Fact]
    public void Pace_converts_through_Pace()
    {
        Assert.Equal(300 * 1000 / 1609.344, UnitCodes.Convert(300, UnitCodes.SecondsPerMile, UnitCodes.SecondsPerKm));
        Assert.Equal(300 * 1609.344 / 1000, UnitCodes.Convert(300, UnitCodes.SecondsPerKm, UnitCodes.SecondsPerMile));
    }

    [Theory]
    [InlineData("bpm")]
    [InlineData("W")]
    [InlineData("%")]
    [InlineData("ml/kg/min")]
    public void Fixed_units_convert_only_to_themselves(string code)
    {
        Assert.Equal(61.5, UnitCodes.Convert(61.5, code, code));
        Assert.Null(UnitCodes.Convert(61.5, code, "kg"));
        Assert.Null(UnitCodes.Convert(61.5, "kg", code));
    }

    [Theory]
    [InlineData("kg", "cm")]
    [InlineData("kcal", "mmol/L")]
    [InlineData("s_per_km", "km")]
    [InlineData("unknown", "kg")]
    public void Different_dimensions_and_unknown_codes_do_not_convert(string from, string to) =>
        Assert.Null(UnitCodes.Convert(1, from, to));
}

using FsCheck.Fluent;
using Health360.Domain.Units;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Units;

public class QuantityTests
{
    [Fact]
    public void A_value_shown_in_the_unit_it_was_typed_in_comes_back_exactly()
    {
        var kindAndUnit = Gen.Elements(Enum.GetValues<QuantityKind>())
            .SelectMany(kind => Gen.Elements(Unit.OfDimension(QuantityKinds.DimensionOf(kind)).ToArray()).Select(unit => (kind, unit)));

        Holds(Gen.Zip(kindAndUnit, Double(0, 1e6)), t =>
        {
            var ((kind, unit), v) = t;
            return Quantity.Create(kind, v, unit).DisplayValue(unit) == v;
        });
    }

    [Fact]
    public void Pounds_stay_as_typed_and_show_as_the_right_kg()
    {
        var q = Quantity.Create(QuantityKind.BodyWeight, 225, Unit.Pound);

        Assert.Equal(225, q.DisplayValue(Unit.Pound));
        Assert.Equal(102.0582833, q.Canonical, 6);
        Assert.Equal(q.Canonical, q.DisplayValue(Unit.Kilogram));
    }

    [Fact]
    public void Canonical_units_match_the_column_names_in_the_plan()
    {
        Assert.Equal(177.8, Quantity.Create(QuantityKind.Height, 70, Unit.Inch).Canonical, 10);
        Assert.Equal(1609.344, Quantity.Create(QuantityKind.Distance, 1, Unit.Mile).Canonical);
        Assert.Equal(28.349523125, Quantity.Create(QuantityKind.FoodMass, 1, Unit.Ounce).Canonical, 12);
        Assert.Equal(4.9957, Quantity.Create(QuantityKind.Glucose, 90, Unit.MilligramPerDecilitre).Canonical, 4);
    }

    [Fact]
    public void Every_kind_has_a_canonical_unit_of_its_dimension()
    {
        Assert.All(Enum.GetValues<QuantityKind>(), kind =>
            Assert.Equal(QuantityKinds.DimensionOf(kind), QuantityKinds.CanonicalUnit(kind).Dimension));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Rejects_non_finite_values(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantity.Create(QuantityKind.Load, value, Unit.Kilogram));
    }

    [Fact]
    public void Rejects_a_unit_of_another_dimension()
    {
        Assert.Throws<ArgumentException>(() => Quantity.Create(QuantityKind.Height, 180, Unit.Kilogram));
    }

    [Fact]
    public void The_same_load_logged_in_kg_and_then_in_lb_is_not_a_new_record()
    {
        Holds(Double(0.5, 500), kg =>
        {
            var first = Quantity.Create(QuantityKind.Load, kg, Unit.Kilogram);
            var second = Quantity.Create(QuantityKind.Load, Conversion.Convert(kg, Unit.Kilogram, Unit.Pound), Unit.Pound);
            return !Quantity.CanonicalGreater(second.Canonical, first.Canonical)
                && Quantity.CanonicalEquals(first.Canonical, second.Canonical);
        });
    }

    [Fact]
    public void A_genuinely_heavier_lift_still_counts()
    {
        var previous = Quantity.Create(QuantityKind.Load, 100, Unit.Kilogram);
        var next = Quantity.Create(QuantityKind.Load, 225, Unit.Pound); // 102.06 kg

        Assert.True(Quantity.CanonicalGreater(next.Canonical, previous.Canonical));
    }
}

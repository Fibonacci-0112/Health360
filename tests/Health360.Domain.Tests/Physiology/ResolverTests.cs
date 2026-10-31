using Health360.Domain.Physiology;

namespace Health360.Domain.Tests.Physiology;

public class ResolverTests
{
    private static readonly Estimate Formula = new(1650, EstimateMethods.MifflinStJeor);

    private static Measured M(int id, double value, MeasurementSource source, string date) =>
        new(new Guid(id, 0, 0, new byte[8]), value, source, DateOnly.Parse(date));

    [Fact]
    public void Lab_beats_field_test_beats_device_or_manual_beats_estimate()
    {
        var lab = M(1, 1720, MeasurementSource.Lab, "2025-03-01");
        var field = M(2, 1700, MeasurementSource.FieldTest, "2026-01-01");
        var device = M(3, 1690, MeasurementSource.Device, "2026-06-01");

        var result = Resolver.Resolve([device, field, lab], Formula)!;

        Assert.Equal(1720, result.Value);
        Assert.Equal(SourceTier.Lab, result.Tier);
        Assert.Same(lab, result.Measurement);
        Assert.False(result.IsEstimate);
        Assert.Equal([field, device], result.PassedOver);
        Assert.Same(Formula, result.EstimatePassedOver);
    }

    [Fact]
    public void Device_and_manual_share_a_tier_and_the_newest_wins()
    {
        var manual = M(1, 60, MeasurementSource.Manual, "2026-05-02");
        var device = M(2, 62, MeasurementSource.Device, "2026-05-01");

        Assert.Same(manual, Resolver.Resolve([device, manual], null)!.Measurement);
    }

    [Fact]
    public void On_the_same_day_the_later_entry_wins()
    {
        var first = M(1, 60, MeasurementSource.Lab, "2026-05-01");
        var second = M(2, 61, MeasurementSource.Lab, "2026-05-01");

        Assert.Same(second, Resolver.Resolve([second, first], null)!.Measurement);
    }

    [Fact]
    public void An_old_measured_value_still_beats_the_estimate()
    {
        var old = M(1, 1720, MeasurementSource.Lab, "2019-01-01");

        Assert.Same(old, Resolver.Resolve([old], Formula)!.Measurement);
    }

    [Fact]
    public void Falls_back_to_the_estimate_and_then_to_nothing()
    {
        var result = Resolver.Resolve([], Formula)!;

        Assert.True(result.IsEstimate);
        Assert.Equal(SourceTier.Estimate, result.Tier);
        Assert.Equal(1650, result.Value);
        Assert.Null(Resolver.Resolve([], null));
    }

    [Fact]
    public void Deleting_the_measured_value_falls_back_to_the_next_one_and_then_the_estimate()
    {
        var lab = M(1, 1720, MeasurementSource.Lab, "2025-03-01");
        var device = M(2, 1690, MeasurementSource.Device, "2026-06-01");

        Assert.Same(lab, Resolver.Resolve([lab, device], Formula)!.Measurement);
        Assert.Same(device, Resolver.Resolve([device], Formula)!.Measurement);
        Assert.True(Resolver.Resolve([], Formula)!.IsEstimate);
    }

    [Fact]
    public void A_pinned_measurement_wins_over_the_order()
    {
        var lab = M(1, 1720, MeasurementSource.Lab, "2026-03-01");
        var older = M(2, 1690, MeasurementSource.Device, "2024-06-01");

        var result = Resolver.Resolve([lab, older], Formula, Pin.ToMeasurement(older.Id))!;

        Assert.Same(older, result.Measurement);
        Assert.True(result.Pinned);
        Assert.False(result.PinUnavailable);
        Assert.Equal([lab], result.PassedOver);
    }

    [Fact]
    public void A_pin_to_the_estimate_wins_over_measured_values()
    {
        var device = M(1, 1690, MeasurementSource.Device, "2026-06-01");

        var result = Resolver.Resolve([device], Formula, Pin.ToEstimate)!;

        Assert.True(result.IsEstimate);
        Assert.True(result.Pinned);
        Assert.Equal([device], result.PassedOver);
    }

    [Fact]
    public void A_pin_that_cannot_be_honoured_is_reported_and_the_order_applies()
    {
        var lab = M(1, 1720, MeasurementSource.Lab, "2026-03-01");

        var deleted = Resolver.Resolve([lab], Formula, Pin.ToMeasurement(Guid.Empty))!;
        Assert.Same(lab, deleted.Measurement);
        Assert.False(deleted.Pinned);
        Assert.True(deleted.PinUnavailable);

        var noEstimate = Resolver.Resolve([lab], null, Pin.ToEstimate)!;
        Assert.Same(lab, noEstimate.Measurement);
        Assert.True(noEstimate.PinUnavailable);

        var nothing = Resolver.Resolve([], Formula, Pin.ToMeasurement(Guid.Empty))!;
        Assert.True(nothing.IsEstimate);
        Assert.True(nothing.PinUnavailable);
    }

    [Fact]
    public void With_nothing_left_a_pin_gives_no_resolution()
    {
        // The caller keeps its pin, so a null result tells it the pin couldn't be honoured either.
        Assert.Null(Resolver.Resolve([], null, Pin.ToMeasurement(Guid.Empty)));
        Assert.Null(Resolver.Resolve([], null, Pin.ToEstimate));
    }

    [Theory]
    [InlineData("2025-06-01", "2026-05-31", false)]
    [InlineData("2025-06-01", "2026-06-01", true)]
    public void Retest_after_12_months(string measured, string asOf, bool suggested)
    {
        Assert.Equal(suggested, Retest.IsSuggested(RetestRule.After12Months, DateOnly.Parse(measured), DateOnly.Parse(asOf)));
    }

    [Theory]
    [InlineData(80.0, 84.0, true)] // +5%
    [InlineData(80.0, 76.0, true)] // −5%
    [InlineData(80.0, 83.9, false)]
    [InlineData(null, 90.0, false)]
    public void Rmr_retest_after_a_5_percent_weight_change(double? then, double? now, bool suggested)
    {
        var day = new DateOnly(2026, 1, 1);
        Assert.Equal(suggested, Retest.IsSuggested(RetestRule.After12MonthsOrWeightChange, day, day.AddDays(30), then, now));
    }

    [Fact]
    public void Weight_change_doesnt_flag_metrics_with_the_12_month_rule()
    {
        var day = new DateOnly(2026, 1, 1);
        Assert.False(Retest.IsSuggested(RetestRule.After12Months, day, day.AddDays(30), 80, 100));
    }

    [Fact]
    public void Maintenance_is_rmr_times_pal_and_always_an_estimate()
    {
        var lab = M(1, 1700, MeasurementSource.Lab, "2026-03-01");
        var rmr = Resolver.Resolve([lab], Formula)!;

        var maintenance = MaintenanceEnergy.From(rmr, ActivityLevel.Moderate);

        Assert.Equal(1700 * 1.85, maintenance.Kcal, 9);
        Assert.Equal(1.85, maintenance.Pal);
        Assert.Same(rmr, maintenance.Rmr);
    }

    [Fact]
    public void Entering_and_deleting_a_lab_rmr_changes_maintenance_at_once()
    {
        var lab = M(1, 1900, MeasurementSource.Lab, "2026-03-01");

        var withLab = MaintenanceEnergy.From(Resolver.Resolve([lab], Formula)!, ActivityLevel.Light);
        var withoutLab = MaintenanceEnergy.From(Resolver.Resolve([], Formula)!, ActivityLevel.Light);

        Assert.Equal(1900 * 1.625, withLab.Kcal, 9);
        Assert.Equal(1650 * 1.625, withoutLab.Kcal, 9);
        Assert.True(withoutLab.Rmr.IsEstimate);
    }
}

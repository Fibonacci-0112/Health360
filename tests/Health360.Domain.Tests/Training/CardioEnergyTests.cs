using Health360.Domain.Physiology;
using Health360.Domain.Training;

namespace Health360.Domain.Tests.Training;

public sealed class CardioEnergyTests
{
    private static readonly CardioPhysiology Male = new(80, Sex.Male, 30);
    private static readonly CardioZones Zones = new("Test boundaries",
        [new("1", 0, 120), new("2", 120, 150), new("3", 150, 170), new("4", 170, null)], []);

    private static Resolution Measured(double value) => Resolver.Resolve(
        [new(Guid.Parse("10000000-0000-7000-8000-000000000001"), value, MeasurementSource.Lab, new(2026, 1, 1))], null)!;

    // Independent hand calculations from the two printed Keytel equations, kJ/min / 4.184 * 30 minutes.
    [Theory]
    [InlineData(Sex.Male, 80, 30, null, 150, 308.641276290631, 573.1909416826005)]
    [InlineData(Sex.Female, 60, 40, null, 140, 188.65769598470357, 350.36429254302095)]
    [InlineData(Sex.Male, 80, 30, 50d, 150, 297.0139818355639, 551.5973948374759)]
    [InlineData(Sex.Female, 60, 40, 40d, 140, 180.4102772466539, 335.04765774378586)]
    public void Average_hr_golden_values(Sex sex, double kg, int age, double? vo2, double hr, double low, double high)
    {
        var p = new CardioPhysiology(kg, sex, age, vo2 is { } v ? Measured(v) : null);
        var result = CardioEnergy.Estimate(new("12020", 1800, hr), p);

        Assert.Equal(low, result.Kcal!.Low, 9);
        Assert.Equal(high, result.Kcal.High, 9);
        Assert.Contains("average HR", result.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void Time_in_zones_uses_whole_zone_bounds_before_average_hr()
    {
        var result = CardioEnergy.Estimate(new("12020", 1800, 160, 190, [0, 1200, 600, 0]), Male, Zones);

        Assert.Equal(262.8321582217974, result.Kcal!.Low, 9);
        Assert.Equal(588.8423159655832, result.Kcal.High, 9);
        Assert.Equal("Keytel, time in zones", result.Method);
    }

    [Fact]
    public void No_hr_uses_the_selected_compendium_code()
    {
        var result = CardioEnergy.Estimate(new("12020", 1800), Male);

        // 7.5 MET * 80 kg * 0.5 hours = 300 kcal, +/-30% product range.
        Assert.Equal(new EnergyRange(210, 390), result.Kcal);
        Assert.Equal("2024 Compendium METs", result.Method);
        Assert.Null(result.AerobicSeconds);
    }

    [Fact]
    public void Medication_uses_mets_even_with_hr_and_vo2_but_measured_thresholds_still_work()
    {
        var p = Male with { TakesHeartRateLoweringMedication = true, Vo2Max = Measured(50), ThresholdHr = Measured(160) };
        var result = CardioEnergy.Estimate(new("12020", 1800, 170), p);

        Assert.Equal(new EnergyRange(210, 390), result.Kcal);
        Assert.Equal(1800, result.AnaerobicSeconds);
        Assert.Equal(0, result.AerobicSeconds);
    }

    [Fact]
    public void Time_above_measured_threshold_is_not_a_fuel_or_energy_system_fraction()
    {
        var result = CardioEnergy.Estimate(new("12020", 1800, ZoneSeconds: [0, 1200, 600, 0]),
            Male with { ThresholdHr = Measured(150) }, Zones);

        Assert.Equal(1200, result.AerobicSeconds);
        Assert.Equal(600, result.AnaerobicSeconds);
    }

    [Fact]
    public void A_zone_straddling_threshold_does_not_invent_a_split()
    {
        var result = CardioEnergy.Estimate(new("12020", 1800, ZoneSeconds: [0, 1200, 600, 0]),
            Male with { ThresholdHr = Measured(155) }, Zones);

        Assert.Null(result.AerobicSeconds);
        Assert.Null(result.AnaerobicSeconds);
    }

    [Fact]
    public void Average_hr_intervals_have_wider_energy_range_and_no_time_split()
    {
        var p = Male with { ThresholdHr = Measured(150) };
        var steady = CardioEnergy.Estimate(new("12020", 1800, 150), p);
        var intervals = CardioEnergy.Estimate(new("12020", 1800, 150, Intervals: true), p);

        Assert.True(intervals.Kcal!.Low < steady.Kcal!.Low);
        Assert.True(intervals.Kcal.High > steady.Kcal.High);
        Assert.Null(intervals.AnaerobicSeconds);
    }

    [Fact]
    public void Open_top_zone_without_a_maximum_falls_back_without_guessing_a_ceiling()
    {
        var entry = new CardioEntry("12020", 1800, ZoneSeconds: [0, 0, 0, 1800]);

        Assert.Equal("2024 Compendium METs", CardioEnergy.Estimate(entry, Male, Zones).Method);
        Assert.Equal("Keytel, average HR", CardioEnergy.Estimate(entry with { AverageHr = 180 }, Male, Zones).Method);
        Assert.Equal("Keytel, time in zones", CardioEnergy.Estimate(entry with { MaxHr = 185 }, Male, Zones).Method);
    }

    [Fact]
    public void Missing_weight_keeps_session_loggable_but_energy_unavailable()
    {
        var result = CardioEnergy.Estimate(new("12020", 1800, 150), Male with { WeightKg = null });

        Assert.Null(result.Kcal);
        Assert.Contains(result.Notes, n => n.Contains("body weight", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86401)]
    public void Invalid_duration_is_rejected(double duration) =>
        Assert.Throws<ArgumentException>(() => CardioEnergy.Estimate(new("12020", duration), Male));

    [Fact]
    public void Inconsistent_hr_or_watch_totals_are_rejected()
    {
        CardioEntry[] invalid =
        [
            new("12020", 1800, 180, 150), new("12020", 1800, double.NaN),
            new("12020", 1800, ZoneSeconds: [0, 1200, 500, 0]),
            new("12020", 1800, ZoneSeconds: [0, -1, 1801, 0]),
            new("12020", 1800, ZoneSeconds: [1800]),
            new("12020", 1800, MaxHr: 140, ZoneSeconds: [0, 0, 1800, 0]),
            new("12020", 1800, Rpe: 11), new("12020", 1800, Distance: 2, DistanceUnit: "kg"),
        ];
        foreach (var entry in invalid)
        {
            Assert.NotEmpty(CardioEnergy.Errors(entry, Zones));
        }
    }

    [Fact]
    public void Snapshot_copies_mutable_input_arrays_and_preserves_canonical_distance()
    {
        double[] times = [0, 1200, 600, 0];
        HeartRateZone[] mutable = [.. Zones.Zones];
        var result = CardioEnergy.Estimate(new("12020", 1800, ZoneSeconds: times, Distance: 3, DistanceUnit: "mi"),
            Male, Zones with { Zones = mutable });
        times[1] = 0;
        mutable[1] = new("2", 999, 1000);

        Assert.Equal(1200, result.Entry.ZoneSeconds![1]);
        Assert.Equal(120, result.Zones!.Zones[1].LowerBpm);
        Assert.Equal(4828.032, result.DistanceMetres);
        Assert.Equal((3.0, "mi"), (result.Entry.Distance, result.Entry.DistanceUnit));
    }

    [Fact]
    public void Snapshot_preserves_used_input_provenance_without_passed_over_measurements()
    {
        var used = Measured(50) with { PassedOver = [Measured(40).Measurement!], EstimatePassedOver = new(45, "Other estimate") };
        var result = CardioEnergy.Estimate(new("12020", 1800, 150), Male with { Vo2Max = used }, Zones with { Inputs = [used] });

        Assert.Equal(used.Measurement, result.Physiology.Vo2Max!.Measurement);
        Assert.Empty(result.Physiology.Vo2Max.PassedOver);
        Assert.Null(result.Physiology.Vo2Max.EstimatePassedOver);
        Assert.Empty(result.Zones!.Inputs[0].PassedOver);
    }
}

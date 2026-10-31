using FsCheck.Fluent;
using Health360.Domain.Physiology;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Physiology;

public class HeartRateZoneTests
{
    private static readonly DateOnly Day = new(2026, 3, 1);

    private static Resolution Lab(double bpm) =>
        Resolver.Resolve([new Measured(Guid.NewGuid(), bpm, MeasurementSource.Lab, Day)], null)!;

    private static Resolution Manual(double bpm) =>
        Resolver.Resolve([new Measured(Guid.NewGuid(), bpm, MeasurementSource.Manual, Day)], null)!;

    private static Resolution Estimated(double bpm, string method = "Tanaka") =>
        Resolver.Resolve([], new Estimate(bpm, method))!;

    private static void AssertBoundaries(HeartRateZones zones, params double[] expected)
    {
        Assert.Equal(expected.Length, zones.Boundaries.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Close(zones.Boundaries[i], expected[i]), $"zone {i + 1}: {zones.Boundaries[i]} ≠ {expected[i]}");
        }
    }

    // Hand calculations from the published tables (docs/calculations.md).
    [Fact]
    public void Friel_threshold_zones_match_a_hand_calculation()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(Lthr: Lab(170)))!;

        Assert.Equal(ZoneBasis.Lthr, zones.Basis);
        Assert.Equal("Friel (threshold HR)", zones.Method);
        Assert.Equal(["1", "2", "3", "4", "5a", "5b"], zones.Zones.Select(z => z.Name));
        AssertBoundaries(zones, 0, 132.6, 147.9, 159.8, 170, 173.4); // 170 × 0, .78, .87, .94, 1, 1.02
    }

    [Fact]
    public void Friel_uses_the_same_table_for_run_and_bike()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Bike, new ZoneInputs(Lthr: Manual(160)))!;

        AssertBoundaries(zones, 0, 124.8, 139.2, 150.4, 160, 163.2); // 160 × 0, .78, .87, .94, 1, 1.02
    }

    [Fact]
    public void More_than_102_percent_of_threshold_is_zone_5b()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(Lthr: Lab(170)))!;

        Assert.Equal("5a", zones.Zones[zones.ZoneOf(173.3)!.Value].Name);
        Assert.Equal("5b", zones.Zones[zones.ZoneOf(173.5)!.Value].Name); // just over 102%
    }

    [Fact]
    public void Friel_lab_zones_sit_10_bpm_either_side_of_the_thresholds()
    {
        var zones = HeartRateZoneRules.Derive(
            ZoneSport.Run,
            new ZoneInputs(MaxHr: Lab(190), RestingHr: Manual(50), Lthr: Lab(168), LowerThreshold: Lab(140), UpperThreshold: Lab(165)))!;

        Assert.Equal(ZoneBasis.Lab, zones.Basis);
        Assert.Equal("Friel (LT1 and LT2)", zones.Method);
        Assert.Equal(["1", "2", "3", "4", "5"], zones.Zones.Select(z => z.Name));
        AssertBoundaries(zones, 0, 130, 140, 165, 175); // LT1 − 10, LT1, LT2, LT2 + 10
        Assert.Equal(2, zones.Inputs.Count);
    }

    [Fact]
    public void Friel_max_hr_zones_match_a_hand_calculation()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Estimated(180)))!;

        Assert.Equal(ZoneBasis.PercentOfMax, zones.Basis);
        Assert.Equal("Friel (% of max HR)", zones.Method);
        AssertBoundaries(zones, 0, 131.4, 149.4, 158.4, 165.6); // 180 × 0, .73, .83, .88, .92
    }

    [Fact]
    public void Karvonen_zones_use_garbers_heart_rate_reserve_bands()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), RestingHr: Manual(60)))!;

        Assert.Equal(ZoneBasis.HeartRateReserve, zones.Basis);
        Assert.Equal(["Very light", "Light", "Moderate", "Vigorous", "Near-maximal to maximal"], zones.Zones.Select(z => z.Name));
        AssertBoundaries(zones, 60, 99, 112, 138, 177); // 60 + 130 × 0, .30, .40, .60, .90
    }

    [Theory]
    [InlineData(ZoneSport.Swim)]
    [InlineData(ZoneSport.Row)]
    [InlineData(ZoneSport.Other)]
    [InlineData(ZoneSport.All)]
    public void Other_sports_use_seiler_and_garber_instead_of_friel(ZoneSport sport)
    {
        var lab = HeartRateZoneRules.Derive(sport, new ZoneInputs(LowerThreshold: Lab(140), UpperThreshold: Lab(165)))!;
        Assert.Equal("Three zones from lab thresholds (Seiler)", lab.Method);
        AssertBoundaries(lab, 0, 140, 165);

        var withLthr = HeartRateZoneRules.Derive(sport, new ZoneInputs(MaxHr: Lab(190), RestingHr: Manual(60), Lthr: Lab(170)))!;
        Assert.Equal(ZoneBasis.HeartRateReserve, withLthr.Basis);
        Assert.Equal(sport, withLthr.Sport);

        var max = HeartRateZoneRules.Derive(sport, new ZoneInputs(MaxHr: Estimated(180)))!;
        Assert.Equal("% of max HR", max.Method);
        AssertBoundaries(max, 0, 102.6, 115.2, 138.6, 172.8); // Garber: 180 × 0, .57, .64, .77, .96
    }

    [Fact]
    public void The_best_basis_wins_and_each_falls_back_to_the_next()
    {
        var all = new ZoneInputs(Lab(190), Manual(50), Lab(168), Lab(140), Lab(165));

        Assert.Equal(ZoneBasis.Lab, HeartRateZoneRules.Derive(ZoneSport.Run, all)!.Basis);
        Assert.Equal(ZoneBasis.Lthr, HeartRateZoneRules.Derive(ZoneSport.Run, all with { LowerThreshold = null })!.Basis);
        Assert.Equal(
            ZoneBasis.HeartRateReserve,
            HeartRateZoneRules.Derive(ZoneSport.Run, all with { LowerThreshold = null, UpperThreshold = null, Lthr = null })!.Basis);
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190)))!.Basis);
        Assert.Null(HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs()));
    }

    [Fact]
    public void Lt2_alone_is_not_used_as_threshold_hr()
    {
        // Friel (2025) puts LT2 at the zone 3/4 border and threshold HR at the zone 4/5a border, so they differ.
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), UpperThreshold: Lab(170)))!;

        Assert.Equal(ZoneBasis.PercentOfMax, zones.Basis);
    }

    [Fact]
    public void Estimated_lthr_and_thresholds_never_set_zones()
    {
        var zones = HeartRateZoneRules.Derive(
            ZoneSport.Run,
            new ZoneInputs(MaxHr: Lab(190), Lthr: Estimated(171, "90% of max HR"), LowerThreshold: Estimated(140), UpperThreshold: Estimated(165)))!;

        Assert.Equal(ZoneBasis.PercentOfMax, zones.Basis);
    }

    [Fact]
    public void An_estimated_resting_hr_is_not_used_for_heart_rate_reserve()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), RestingHr: Estimated(60)))!;

        Assert.Equal(ZoneBasis.PercentOfMax, zones.Basis);
    }

    [Fact]
    public void Medication_turns_off_zones_from_an_age_predicted_max_hr_but_not_from_a_measured_one()
    {
        Assert.Null(HeartRateZoneRules.Derive(
            ZoneSport.Run,
            new ZoneInputs(MaxHr: Estimated(180), RestingHr: Manual(55), TakesHeartRateLoweringMedication: true)));

        var measured = HeartRateZoneRules.Derive(
            ZoneSport.Run,
            new ZoneInputs(MaxHr: Lab(160), RestingHr: Manual(55), TakesHeartRateLoweringMedication: true))!;
        Assert.Equal(ZoneBasis.HeartRateReserve, measured.Basis);

        var lthr = HeartRateZoneRules.Derive(
            ZoneSport.Bike,
            new ZoneInputs(MaxHr: Estimated(180), Lthr: Lab(150), TakesHeartRateLoweringMedication: true))!;
        Assert.Equal(ZoneBasis.Lthr, lthr.Basis);
    }

    [Fact]
    public void Inconsistent_inputs_fall_back_instead_of_giving_zones_that_do_not_ascend()
    {
        // LT1 not below LT2: lab zones are skipped.
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), LowerThreshold: Lab(165), UpperThreshold: Lab(165)))!.Basis);

        // LT1 of 10 bpm or less: Friel's zone 2 would start at or below 0.
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), LowerThreshold: Lab(10), UpperThreshold: Lab(165)))!.Basis);

        // Resting HR not below max HR: heart rate reserve is skipped.
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(150), RestingHr: Manual(150)))!.Basis);
    }

    [Fact]
    public void Values_so_large_the_boundaries_overflow_skip_that_basis()
    {
        // 102% of double.MaxValue is infinite, which zone_sets rejects.
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), Lthr: Lab(double.MaxValue)))!.Basis);

        // LT2 + 10 rounds back to LT2 at this size, so the bounds don't ascend.
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), LowerThreshold: Lab(140), UpperThreshold: Lab(double.MaxValue)))!.Basis);

        // The Garber bands stay at or below 100% of their base, so they never overflow.
        var huge = HeartRateZoneRules.Derive(ZoneSport.Swim, new ZoneInputs(MaxHr: Lab(double.MaxValue), RestingHr: Manual(1)))!;
        Assert.Equal(ZoneBasis.HeartRateReserve, huge.Basis);
        Assert.All(huge.Boundaries, b => Assert.True(double.IsFinite(b)));
    }

    [Fact]
    public void Zone_of_follows_the_zone_sets_rule()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), RestingHr: Manual(60)))!;

        Assert.Null(zones.ZoneOf(59.9));
        Assert.Equal(0, zones.ZoneOf(60));
        Assert.Equal(0, zones.ZoneOf(98.99));
        Assert.Equal(1, zones.ZoneOf(99));
        Assert.Equal(3, zones.ZoneOf(176.9));
        Assert.Equal(4, zones.ZoneOf(177));
        Assert.Equal(4, zones.ZoneOf(250));
        Assert.Null(zones.ZoneOf(double.NaN));
        Assert.Null(zones.Zones[^1].UpperBpm);
        Assert.Equal(zones.Zones[1].LowerBpm, zones.Zones[0].UpperBpm);
    }

    [Fact]
    public void Codes_match_the_zone_sets_check_constraints()
    {
        Assert.Equal(["lab", "lthr", "hrr", "max_hr"], Enum.GetValues<ZoneBasis>().Select(HeartRateZoneRules.Code));
        Assert.Equal(["all", "run", "bike", "swim", "row", "other"], Enum.GetValues<ZoneSport>().Select(HeartRateZoneRules.Code));
    }

    [Fact]
    public void Derived_boundaries_always_ascend_and_are_never_negative()
    {
        // What the zone_sets trigger requires for metric hr: 2 to 10 finite, non-negative, strictly ascending values.
        // Mostly plausible heart rates, plus finite values large enough to overflow a percentage band.
        var bpm = Gen.Frequency((9, Double(1, 250)), (1, Gen.Elements(1e307, 1.7e308, double.MaxValue)));
        var gen = Gen.Zip(
            Gen.Zip(bpm, Double(1, 250)),
            Gen.Zip(Gen.Zip(bpm, Double(1, 250)), Gen.Zip(Gen.Elements(Enum.GetValues<ZoneSport>()), Gen.Elements(0, 1, 2, 3, 4))));
        Holds(gen, t =>
        {
            var ((max, resting), ((lthr, lt1), (sport, drop))) = t;
            var inputs = new ZoneInputs(
                Lab(max),
                drop == 1 ? null : Manual(resting),
                drop == 2 ? null : Lab(lthr),
                drop == 3 ? null : Lab(lt1),
                drop == 4 ? null : Lab(lthr));
            var zones = HeartRateZoneRules.Derive(sport, inputs);
            if (zones is null)
            {
                return false;
            }

            var b = zones.Boundaries;
            return b.Count is >= 2 and <= 10
                && b.All(v => double.IsFinite(v) && v >= 0)
                && b.Zip(b.Skip(1)).All(p => p.Second > p.First);
        });
    }
}

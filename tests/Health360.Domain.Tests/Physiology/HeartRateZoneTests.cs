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
    public void Friel_run_zones_match_a_hand_calculation()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(Lthr: Lab(170)))!;

        Assert.Equal(ZoneBasis.Lthr, zones.Basis);
        Assert.Equal(["1", "2", "3", "4", "5a", "5b", "5c"], zones.Zones.Select(z => z.Name));
        AssertBoundaries(zones, 0, 144.5, 153, 161.5, 170, 175.1, 181.9); // 170 × 0, .85, .90, .95, 1, 1.03, 1.07
    }

    [Fact]
    public void Friel_bike_zones_match_a_hand_calculation()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Bike, new ZoneInputs(Lthr: Manual(160)))!;

        AssertBoundaries(zones, 0, 129.6, 144, 150.4, 160, 164.8, 171.2); // 160 × 0, .81, .90, .94, 1, 1.03, 1.07
    }

    [Fact]
    public void Karvonen_zones_use_garbers_heart_rate_reserve_bands()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), RestingHr: Manual(60)))!;

        Assert.Equal(ZoneBasis.HeartRateReserve, zones.Basis);
        Assert.Equal(["Very light", "Light", "Moderate", "Vigorous", "Near-maximal to maximal"], zones.Zones.Select(z => z.Name));
        AssertBoundaries(zones, 60, 99, 112, 138, 177); // 60 + 130 × 0, .30, .40, .60, .90
    }

    [Fact]
    public void Percent_of_max_zones_use_garbers_max_hr_bands()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Estimated(180)))!;

        Assert.Equal(ZoneBasis.PercentOfMax, zones.Basis);
        AssertBoundaries(zones, 0, 102.6, 115.2, 138.6, 172.8); // 180 × 0, .57, .64, .77, .96
    }

    [Fact]
    public void Lab_thresholds_give_three_zones_split_at_them()
    {
        var zones = HeartRateZoneRules.Derive(
            ZoneSport.Run,
            new ZoneInputs(MaxHr: Lab(190), RestingHr: Manual(50), Lthr: Lab(168), LowerThreshold: Lab(140), UpperThreshold: Lab(165)))!;

        Assert.Equal(ZoneBasis.Lab, zones.Basis);
        AssertBoundaries(zones, 0, 140, 165);
        Assert.Equal(2, zones.Inputs.Count);
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
    public void A_measured_lt2_is_taken_as_lthr_when_there_is_no_lthr()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(UpperThreshold: Lab(170)))!;

        Assert.Equal(ZoneBasis.Lthr, zones.Basis);
        AssertBoundaries(zones, 0, 144.5, 153, 161.5, 170, 175.1, 181.9);
    }

    [Fact]
    public void A_measured_lthr_beats_lt2_for_friel_zones()
    {
        var zones = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(Lthr: Manual(170), UpperThreshold: Lab(160)))!;

        Assert.True(Close(zones.Boundaries[4], 170));
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

    [Theory]
    [InlineData(ZoneSport.Swim)]
    [InlineData(ZoneSport.Row)]
    [InlineData(ZoneSport.Other)]
    [InlineData(ZoneSport.All)]
    public void Friel_zones_are_only_for_run_and_bike(ZoneSport sport)
    {
        var zones = HeartRateZoneRules.Derive(sport, new ZoneInputs(MaxHr: Lab(190), RestingHr: Manual(60), Lthr: Lab(170)))!;

        Assert.Equal(ZoneBasis.HeartRateReserve, zones.Basis);
        Assert.Equal(sport, zones.Sport);
    }

    [Fact]
    public void Inconsistent_inputs_fall_back_instead_of_giving_zones_that_do_not_ascend()
    {
        // LT1 not below LT2: lab zones are skipped, and LT2 is taken as LTHR.
        Assert.Equal(
            ZoneBasis.Lthr,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(LowerThreshold: Lab(165), UpperThreshold: Lab(165)))!.Basis);

        // Resting HR not below max HR: heart rate reserve is skipped.
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(150), RestingHr: Manual(150)))!.Basis);
    }

    [Fact]
    public void Values_so_large_the_boundaries_overflow_skip_that_basis()
    {
        // 107% of 1.7e308 is infinite, which zone_sets rejects.
        Assert.Equal(
            ZoneBasis.PercentOfMax,
            HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(190), Lthr: Lab(1.7e308)))!.Basis);

        // Only Friel's 107% band can overflow; the Garber bands stay at or below 100% of the base.
        var huge = HeartRateZoneRules.Derive(ZoneSport.Run, new ZoneInputs(MaxHr: Lab(double.MaxValue), RestingHr: Manual(1)))!;
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

using Health360.Domain.Physiology;
using static Health360.Domain.Tests.Properties;

namespace Health360.Domain.Tests.Physiology;

/// <summary>
/// The profile the body and physiology screens show (PLAN.md §2.8). Expected values are hand calculations from
/// docs/calculations.md, so these also carry the step 1.1 exit criteria: a lab RMR changes the maintenance estimate
/// and deleting it falls back; a measured max HR changes the zones and deleting it falls back.
/// </summary>
public class PhysiologyProfileTests
{
    private static readonly DateOnly Today = new(2026, 10, 31);
    private static int nextId;

    private static MetricValue V(string code, double value, string date, MeasurementSource source = MeasurementSource.Manual) =>
        new(code, new Measured(new Guid(Interlocked.Increment(ref nextId), 0, 0, new byte[8]), value, source, DateOnly.Parse(date)));

    // Male, 30 on 2026-10-31, 180 cm, moderately active: Mifflin-St Jeor at 80 kg is 1781.6 (calculations.md).
    private static PhysiologyInputs Sam(params MetricValue[] values) =>
        new(Today, values, Sex.Male, new DateOnly(1996, 5, 1), 180, ActivityLevel.Moderate);

    private static readonly MetricValue Weighed = V(MetricCodes.Weight, 80, "2026-10-30");

    [Fact]
    public void Without_measurements_RMR_is_Mifflin_St_Jeor_and_maintenance_multiplies_it_by_the_PAL()
    {
        var profile = PhysiologyProfile.Build(Sam(Weighed));

        Assert.Equal(30, profile.AgeYears);
        Assert.Equal(80, profile.WeightKg);
        Assert.True(profile.Rmr!.Resolution.IsEstimate);
        Assert.Equal(EstimateMethods.MifflinStJeor, profile.Rmr.Resolution.Estimate!.Method);
        Assert.True(Close(profile.Rmr.Resolution.Value, 1781.6));
        Assert.True(Close(profile.Maintenance!.Kcal, 1781.6 * 1.85));
        Assert.Equal(1.85, profile.Maintenance.Pal);
    }

    [Fact]
    public void A_lab_RMR_replaces_the_estimate_at_once_and_deleting_it_falls_back()
    {
        var lab = V(MetricCodes.Rmr, 1900, "2026-09-01", MeasurementSource.Lab);
        var device = V(MetricCodes.Rmr, 1850, "2026-10-01", MeasurementSource.Device);

        var withLab = PhysiologyProfile.Build(Sam(Weighed, lab, device));
        Assert.Equal(1900, withLab.Rmr!.Resolution.Value);
        Assert.Equal(SourceTier.Lab, withLab.Rmr.Resolution.Tier);
        Assert.Same(lab.Measured, withLab.Rmr.Resolution.Measurement);
        Assert.Equal(1900 * 1.85, withLab.Maintenance!.Kcal);
        Assert.Same(withLab.Rmr.Resolution, withLab.Maintenance.Rmr);
        Assert.True(Close(withLab.Rmr.Resolution.EstimatePassedOver!.Value, 1781.6));

        // Deleted: the next measured value, then the estimate.
        var withDevice = PhysiologyProfile.Build(Sam(Weighed, device));
        Assert.Equal(1850, withDevice.Maintenance!.Rmr.Value);
        var withNone = PhysiologyProfile.Build(Sam(Weighed));
        Assert.True(withNone.Maintenance!.Rmr.IsEstimate);
    }

    [Fact]
    public void Known_body_fat_switches_the_RMR_estimate_to_Katch_McArdle()
    {
        // 80 kg at 25% fat: fat-free mass 60 kg, so 370 + 21.6 × 60 = 1666.
        var profile = PhysiologyProfile.Build(Sam(Weighed, V(MetricCodes.BodyFatPercent, 25, "2026-10-30")));

        Assert.Equal(60, profile.FatFreeMassKg);
        Assert.Equal(EstimateMethods.KatchMcArdle, profile.Rmr!.Resolution.Estimate!.Method);
        Assert.True(Close(profile.Rmr.Resolution.Value, 1666));
    }

    [Fact]
    public void The_trend_weight_is_the_body_weight_formulas_use()
    {
        // Golden fixture from calculations.md: the trend ends at 79.629222.
        var profile = PhysiologyProfile.Build(Sam(
            V(MetricCodes.Weight, 80.0, "2026-01-01"),
            V(MetricCodes.Weight, 79.6, "2026-01-02"),
            V(MetricCodes.Weight, 80.4, "2026-01-02"),
            V(MetricCodes.Weight, 79.2, "2026-01-03"),
            V(MetricCodes.Weight, 78.9, "2026-01-06"),
            V(MetricCodes.Weight, 79.5, "2026-01-07")));

        Assert.Equal(5, profile.WeightTrend.Count);
        Assert.True(Close(profile.WeightKg!.Value, 79.629222, 1e-9));
        Assert.True(Close(profile.Bmi!.Value, 79.629222 / (1.8 * 1.8), 1e-9));
    }

    [Fact]
    public void Without_weight_birth_date_or_sex_nothing_is_estimated()
    {
        var profile = PhysiologyProfile.Build(new PhysiologyInputs(Today, []));

        Assert.Null(profile.AgeYears);
        Assert.Null(profile.WeightKg);
        Assert.Null(profile.Rmr);
        Assert.Null(profile.Maintenance);
        Assert.Null(profile.MaxHr);
        Assert.Null(profile.ZonesFor(ZoneSport.Run));
    }

    [Fact]
    public void Without_an_activity_level_there_is_no_maintenance_estimate()
    {
        var profile = PhysiologyProfile.Build(Sam(Weighed) with { ActivityLevel = null });

        Assert.NotNull(profile.Rmr);
        Assert.Null(profile.Maintenance);
    }

    [Fact]
    public void A_measured_max_HR_replaces_Tanaka_and_the_zones_follow_it()
    {
        // Age 40: Tanaka 208 − 28 = 180 → Friel % of max: 0, 131.4, 149.4, 158.4, 165.6.
        var forty = Sam() with { BirthDate = new DateOnly(1986, 1, 1) };
        var estimated = PhysiologyProfile.Build(forty);
        Assert.True(estimated.MaxHr!.Resolution.IsEstimate);
        Assert.Equal(180, estimated.MaxHr.Resolution.Value);
        var estimatedZones = estimated.ZonesFor(ZoneSport.Run)!;
        Assert.Equal(ZoneBasis.PercentOfMax, estimatedZones.Basis);
        AssertBoundaries(estimatedZones, 0, 131.4, 149.4, 158.4, 165.6);

        // Measured 190 → 0, 138.7, 157.7, 167.2, 174.8.
        var measured = PhysiologyProfile.Build(forty with { Measurements = [V(MetricCodes.MaxHr, 190, "2026-06-01")] });
        Assert.False(measured.MaxHr!.Resolution.IsEstimate);
        AssertBoundaries(measured.ZonesFor(ZoneSport.Run)!, 0, 138.7, 157.7, 167.2, 174.8);
        Assert.Equal(180, measured.MaxHr.Resolution.EstimatePassedOver!.Value);
    }

    [Fact]
    public void With_the_medication_flag_max_HR_is_not_estimated_and_there_are_no_zones_without_a_measurement()
    {
        var flagged = Sam() with { TakesHeartRateLoweringMedication = true };

        var profile = PhysiologyProfile.Build(flagged);
        Assert.Null(profile.MaxHr);
        Assert.Null(profile.ZonesFor(ZoneSport.Run));

        var measured = PhysiologyProfile.Build(flagged with { Measurements = [V(MetricCodes.MaxHr, 170, "2026-06-01")] });
        Assert.NotNull(measured.ZonesFor(ZoneSport.Run));
    }

    [Fact]
    public void A_measured_resting_HR_gives_heart_rate_reserve_zones()
    {
        // Max 190, resting 60 → Garber %HRR: 60, 99, 112, 138, 177 (calculations.md).
        var profile = PhysiologyProfile.Build(Sam(V(MetricCodes.MaxHr, 190, "2026-06-01"), V(MetricCodes.RestingHr, 60, "2026-10-01")));

        var zones = profile.ZonesFor(ZoneSport.Swim)!;
        Assert.Equal(ZoneBasis.HeartRateReserve, zones.Basis);
        AssertBoundaries(zones, 60, 99, 112, 138, 177);
    }

    [Fact]
    public void Lactate_thresholds_are_used_when_both_are_measured_otherwise_ventilatory_ones()
    {
        var lt1 = V(MetricCodes.Lt1Hr, 140, "2026-06-01", MeasurementSource.Lab);
        var lt2 = V(MetricCodes.Lt2Hr, 165, "2026-06-01", MeasurementSource.Lab);
        var vt1 = V(MetricCodes.Vt1Hr, 138, "2026-06-01", MeasurementSource.Lab);
        var vt2 = V(MetricCodes.Vt2Hr, 168, "2026-06-01", MeasurementSource.Lab);

        var lactate = PhysiologyProfile.Build(Sam(lt1, lt2, vt1, vt2));
        Assert.Equal(MetricCodes.Lt1Hr, lactate.LowerThreshold!.MetricCode);
        AssertBoundaries(lactate.ZonesFor(ZoneSport.Run)!, 0, 130, 140, 165, 175);

        var ventilatory = PhysiologyProfile.Build(Sam(lt2, vt1, vt2));
        Assert.Equal(MetricCodes.Vt1Hr, ventilatory.LowerThreshold!.MetricCode);
        Assert.Equal(MetricCodes.Vt2Hr, ventilatory.UpperThreshold!.MetricCode);
        AssertBoundaries(ventilatory.ZonesFor(ZoneSport.Other)!, 0, 138, 168);
    }

    [Fact]
    public void A_measured_LTHR_gives_Friel_threshold_zones()
    {
        var profile = PhysiologyProfile.Build(Sam(V(MetricCodes.Lthr, 170, "2026-06-01", MeasurementSource.FieldTest)));

        AssertBoundaries(profile.ZonesFor(ZoneSport.Bike)!, 0, 132.6, 147.9, 159.8, 170, 173.4);
    }

    [Fact]
    public void Old_VO2max_and_LTHR_get_a_retest_flag_after_12_months()
    {
        var profile = PhysiologyProfile.Build(Sam(
            V(MetricCodes.Vo2Max, 48, "2025-10-31", MeasurementSource.Lab),
            V(MetricCodes.Lthr, 168, "2025-11-01", MeasurementSource.Lab)));

        Assert.True(profile.Vo2Max!.RetestSuggested);
        Assert.False(profile.Lthr!.RetestSuggested);
    }

    [Fact]
    public void A_measured_RMR_gets_a_retest_flag_once_trend_weight_has_changed_by_5_percent()
    {
        var rmr = V(MetricCodes.Rmr, 1800, "2026-06-01", MeasurementSource.Lab);

        // 80 kg at the test; after the 151-day gap the trend is within a hair of 75 kg, a change of about 6%.
        var changed = PhysiologyProfile.Build(Sam(rmr, V(MetricCodes.Weight, 80, "2026-06-01"), V(MetricCodes.Weight, 75, "2026-10-30")));
        // 77 kg now: under 5%.
        var small = PhysiologyProfile.Build(Sam(rmr, V(MetricCodes.Weight, 80, "2026-06-01"), V(MetricCodes.Weight, 77, "2026-10-30")));
        // Only a later weigh-in: no weight on the test date, so no weight-change flag.
        var unknown = PhysiologyProfile.Build(Sam(rmr, V(MetricCodes.Weight, 76, "2026-10-30")));

        Assert.True(changed.Rmr!.RetestSuggested);
        Assert.False(small.Rmr!.RetestSuggested);
        Assert.False(unknown.Rmr!.RetestSuggested);
    }

    [Fact]
    public void An_estimate_never_gets_a_retest_flag()
    {
        Assert.False(PhysiologyProfile.Build(Sam(Weighed)).Rmr!.RetestSuggested);
    }

    [Fact]
    public void The_Navy_estimate_uses_the_newest_circumferences_at_the_sites_for_the_profile_sex()
    {
        var male = PhysiologyProfile.Build(Sam(
            V(MetricCodes.Neck, 40, "2026-01-01"),
            V(MetricCodes.Neck, 38, "2026-10-01"),
            V(MetricCodes.WaistAtNavel, 90, "2026-10-01"),
            V(MetricCodes.WaistAtNarrowest, 70, "2026-10-01")));

        Assert.Equal(Health360.Domain.Body.NavyMethod.Male(180, 38, 90), male.Navy);

        var female = Sam(V(MetricCodes.Neck, 33, "2026-10-01"), V(MetricCodes.WaistAtNarrowest, 72, "2026-10-01")) with
        {
            Sex = Sex.Female,
            HeightCm = 165,
        };
        Assert.Null(PhysiologyProfile.Build(female).Navy);
        var withHip = PhysiologyProfile.Build(female with { Measurements = [.. female.Measurements, V(MetricCodes.Hip, 98, "2026-10-01")] });
        Assert.Equal(Health360.Domain.Body.NavyMethod.Female(165, 33, 72, 98), withHip.Navy);
    }

    [Fact]
    public void The_Jackson_VO2max_estimate_is_used_only_without_a_resting_HR()
    {
        // %fat model, male, PA-R 5, 40 y, 20% → 41.721 (calculations.md).
        var inputs = new PhysiologyInputs(Today, [V(MetricCodes.BodyFatPercent, 20, "2026-10-01"), Weighed], Sex.Male,
            new DateOnly(1986, 1, 1), 180, ActivityRating: 5);

        var estimated = PhysiologyProfile.Build(inputs);
        Assert.True(Close(estimated.Vo2Max!.Resolution.Value, 41.721, 1e-9));
        Assert.Equal(EstimateMethods.JacksonPercentFat, estimated.Vo2Max.Resolution.Estimate!.Method);

        var withResting = PhysiologyProfile.Build(inputs with { Measurements = [.. inputs.Measurements, V(MetricCodes.RestingHr, 55, "2026-10-01")] });
        Assert.Null(withResting.Vo2Max);
    }

    [Fact]
    public void A_pin_to_an_older_measurement_is_honoured()
    {
        var older = V(MetricCodes.Rmr, 1750, "2025-01-01", MeasurementSource.Lab);
        var newer = V(MetricCodes.Rmr, 1900, "2026-09-01", MeasurementSource.Lab);
        var pins = new Dictionary<string, Pin> { [MetricCodes.Rmr] = Pin.ToMeasurement(older.Measured.Id) };

        var profile = PhysiologyProfile.Build(Sam(Weighed, older, newer) with { Pins = pins });

        Assert.Equal(1750, profile.Rmr!.Resolution.Value);
        Assert.True(profile.Rmr.Resolution.Pinned);
    }

    private static void AssertBoundaries(HeartRateZones zones, params double[] expected)
    {
        Assert.Equal(expected.Length, zones.Boundaries.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Close(zones.Boundaries[i], expected[i]), $"zone {i + 1}: {zones.Boundaries[i]} ≠ {expected[i]}");
        }
    }
}

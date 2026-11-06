using Health360.Domain.Physiology;
using Health360.Domain.Training;

namespace Health360.Domain.Tests.Training;

public sealed class TrainingCompletionTests
{
    [Fact]
    public void Bounded_plate_loading_finds_a_combination_a_greedy_loader_misses()
    {
        var load = PlateLoading.Calculate(80, 20, [new(15, "kg", 2), new(10, "kg", 6)]);
        Assert.Equal(80, load.TotalKg);
        Assert.Equal(new LoadedPlate(10, "kg", 3), Assert.Single(load.Plates));
    }

    [Fact]
    public void Mixed_units_use_exact_kg_and_only_available_pairs()
    {
        var load = PlateLoading.Calculate(66, 20, [new(45, "lb", 3), new(2.5, "kg", 2)]);
        Assert.Equal(20 + 90 * 0.45359237 + 5, load.TotalKg, 10);
        Assert.All(load.Plates, p => Assert.Equal(1, p.PerSide));
        Assert.Equal(20, PlateLoading.Calculate(10, 20, []).TotalKg);
    }

    [Fact]
    public void Plates_heavier_than_100_in_their_own_unit_remain_usable()
    {
        var load = PlateLoading.Calculate(120, 20, [new(110, "lb", 2), new(1000, "lb", 2)]);
        Assert.Equal(20 + 220 * 0.45359237, load.TotalKg, 10);
        Assert.Throws<ArgumentException>(() => PlateLoading.Calculate(120, 20, [new(1000.5, "kg", 2)]));
    }

    [Fact]
    public void A_heavier_near_equal_total_does_not_hide_a_combination_that_fits()
    {
        // 5.512 lb is about 2.50018 kg, sharing a gram bucket with 2.5 kg but leaving no room for 1.25 kg.
        var load = PlateLoading.Calculate(27.5, 20, [new(5.512, "lb", 2), new(2.5, "kg", 2), new(1.25, "kg", 2)]);
        Assert.Equal(27.5, load.TotalKg, 10);
        Assert.Equal([new LoadedPlate(2.5, "kg", 1), new LoadedPlate(1.25, "kg", 1)], load.Plates);
    }

    // Table 2, Péronnet & Massicotte 1991 (the revised table, not Lusk 1924).
    [Theory]
    [InlineData(0.7036, 1)]
    [InlineData(0.80, 0.655)]
    [InlineData(0.85, 0.483)]
    [InlineData(0.90, 0.314)]
    [InlineData(0.95, 0.149)]
    [InlineData(0.996, 0)]
    [InlineData(1.05, 0)]
    public void Fuel_conversion_matches_the_published_nonprotein_rq_table(double rq, double fatShare) =>
        Assert.InRange(Math.Abs(CardioSubstrates.FatFraction(rq) - fatShare), 0, 0.0006);

    [Theory]
    [InlineData(0.25, 0.7265129742365304)]
    [InlineData(0.65, 0.8305136612663864)]
    [InlineData(0.85, 0.9085323839419700)]
    public void Population_calibration_matches_the_documented_reconstruction(double fraction, double rq) =>
        Assert.Equal(rq, CardioSubstrates.RqAt(fraction), 12);

    [Fact]
    public void Range_allocation_preserves_complementary_shares_and_published_gram_equivalents()
    {
        var result = CardioSubstrates.Allocate(new(300, 300), new(0.85, 0.85));
        Assert.Equal(300, result.FatKcal.Low + result.CarbKcal.Low, 10);
        Assert.Equal(result.FatKcal.Low / 9.746, result.FatGrams.Low, 10);
        Assert.Equal(result.CarbKcal.Low / 3.8683, result.CarbGrams.Low, 10);
    }

    [Fact]
    public void No_hr_requires_rpe_for_a_split_and_medication_never_uses_hr_for_fuel()
    {
        var physiology = new CardioPhysiology(80, Sex.Male, 30);
        Assert.Null(CardioEnergy.Estimate(new("17190", 1800), physiology).Fuel);
        var entry = new CardioEntry("17190", 1800, Rpe: 5);
        var estimate = CardioEnergy.Estimate(entry, physiology);
        Assert.Equal(106.4, estimate.Kcal!.Low, 9);
        Assert.Equal(197.6, estimate.Kcal.High, 9);
        Assert.Equal(1800, estimate.Intensity!.ModerateSeconds);
        Assert.NotNull(estimate.Fuel);
        var medication = physiology with { TakesHeartRateLoweringMedication = true };
        Assert.Equal(estimate.Fuel, CardioEnergy.Estimate(entry with { AverageHr = 170 }, medication).Fuel);
        Assert.Null(CardioEnergy.Estimate(entry with { Intervals = true }, physiology).Intensity);
    }

    [Fact]
    public void Strength_always_uses_mets_without_numeric_substrate_or_time_splits()
    {
        var result = CardioEnergy.Estimate(new("02054", 3600, AverageHr: 170, Rpe: 8), new(80, Sex.Male, 30));
        Assert.Equal(new EnergyRange(196, 364), result.Kcal);
        Assert.Equal("2024 Compendium METs", result.Method);
        Assert.Null(result.Fuel);
        Assert.Null(result.Intensity);
        Assert.Null(result.AnaerobicSeconds);
    }

    private static Resolution Measured(double value) => Resolver.Resolve(
        [new(Guid.Parse("10000000-0000-7000-8000-000000000001"), value, MeasurementSource.Lab, new(2026, 1, 1))], null)!;

    [Fact]
    public void Three_intensity_bands_use_actual_thresholds_and_refuse_straddling_zones()
    {
        var p = new CardioPhysiology(80, Sex.Male, 30, MaxHr: Measured(190), ThresholdHr: Measured(170), LowerThresholdHr: Measured(140));
        var zones = new CardioZones("Measured thresholds", [new("1", 0, 140), new("2", 140, 170), new("3", 170, null)], []);
        var estimate = CardioEnergy.Estimate(new("12020", 3600, MaxHr: 190, ZoneSeconds: [2400, 900, 300]), p, zones);
        Assert.Equal(new IntensityMinutes(2400, 900, 300, "Measured LT1/LT2 HR"), estimate.Intensity);
        Assert.Equal(3300, estimate.AerobicSeconds);
        Assert.NotNull(estimate.Fuel);
        var straddled = zones with { Zones = [new("1", 0, 150), new("2", 150, 170), new("3", 170, null)] };
        Assert.Null(CardioEnergy.Estimate(estimate.Entry, p, straddled).Intensity);
    }
}

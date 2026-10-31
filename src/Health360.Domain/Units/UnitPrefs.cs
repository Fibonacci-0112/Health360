namespace Health360.Domain.Units;

public enum BodyWeightUnit { Kilogram, Pound, StonePound }

public enum LoadUnit { Kilogram, Pound }

public enum HeightUnit { Centimetre, FeetInches }

public enum CircumferenceUnit { Centimetre, Inch }

public enum DistanceUnit { Kilometre, Mile }

public enum ShortDistanceUnit { Metre, Yard }

public enum FoodMassUnit { Gram, Ounce }

public enum VolumeUnit { Millilitre, FluidOunce, Cup }

public enum EnergyUnit { Kilocalorie, Kilojoule }

public enum TemperatureUnit { Celsius, Fahrenheit }

public enum GlucoseUnit { MilligramPerDecilitre, MillimolePerLitre }

public enum LabValueSystem { Conventional, SI }

/// <summary>Per-type unit preferences (PLAN.md §2.6), stored in <c>profiles.unit_prefs</c>.</summary>
public sealed record UnitPrefs
{
    public required BodyWeightUnit BodyWeight { get; init; }
    public required LoadUnit Load { get; init; }
    public required HeightUnit Height { get; init; }
    public required CircumferenceUnit Circumference { get; init; }
    public required DistanceUnit Distance { get; init; }
    public required ShortDistanceUnit ShortDistance { get; init; }
    public required PaceUnit Pace { get; init; }
    public required FoodMassUnit FoodMass { get; init; }
    public required VolumeUnit Volume { get; init; }
    public required EnergyUnit Energy { get; init; }
    public required TemperatureUnit Temperature { get; init; }
    public required GlucoseUnit Glucose { get; init; }
    public required LabValueSystem LabValues { get; init; }
    public required ProteinRatioUnit ProteinRatio { get; init; }

    public static UnitPrefs Metric { get; } = new()
    {
        BodyWeight = BodyWeightUnit.Kilogram,
        Load = LoadUnit.Kilogram,
        Height = HeightUnit.Centimetre,
        Circumference = CircumferenceUnit.Centimetre,
        Distance = DistanceUnit.Kilometre,
        ShortDistance = ShortDistanceUnit.Metre,
        Pace = PaceUnit.MinutesPerKilometre,
        FoodMass = FoodMassUnit.Gram,
        Volume = VolumeUnit.Millilitre,
        Energy = EnergyUnit.Kilocalorie,
        Temperature = TemperatureUnit.Celsius,
        Glucose = GlucoseUnit.MillimolePerLitre,
        LabValues = LabValueSystem.SI,
        ProteinRatio = ProteinRatioUnit.GramsPerKilogram,
    };

    public static UnitPrefs UsCustomary { get; } = new()
    {
        BodyWeight = BodyWeightUnit.Pound,
        Load = LoadUnit.Pound,
        Height = HeightUnit.FeetInches,
        Circumference = CircumferenceUnit.Inch,
        Distance = DistanceUnit.Mile,
        ShortDistance = ShortDistanceUnit.Yard,
        Pace = PaceUnit.MinutesPerMile,
        FoodMass = FoodMassUnit.Ounce,
        Volume = VolumeUnit.FluidOunce,
        Energy = EnergyUnit.Kilocalorie,
        Temperature = TemperatureUnit.Fahrenheit,
        Glucose = GlucoseUnit.MilligramPerDecilitre,
        LabValues = LabValueSystem.Conventional,
        ProteinRatio = ProteinRatioUnit.GramsPerPound,
    };

    /// <summary>Regions that use US customary units day to day.</summary>
    private static readonly HashSet<string> UsCustomaryRegions = new(["US", "LR", "MM"], StringComparer.Ordinal);

    /// <summary>Regions where food energy is usually labelled in kJ first.</summary>
    private static readonly HashSet<string> KilojouleRegions = new(["AU", "NZ"], StringComparer.Ordinal);

    /// <summary>Extracts an ISO region from a BCP 47 tag ("en-US", "en_GB", "de-Latn-AT"); null when absent.</summary>
    public static string? RegionOf(string locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        var region = locale.Replace('_', '-').Split('-').Skip(1).FirstOrDefault(IsRegionSubtag);
        return region?.ToUpperInvariant();

        static bool IsRegionSubtag(string part) =>
            (part.Length == 2 && part.All(char.IsAsciiLetter)) || (part.Length == 3 && part.All(char.IsAsciiDigit));
    }

    /// <summary>
    /// Starting preferences for a locale. These are only defaults: onboarding shows every type and the user
    /// can change each one. The UK keeps stone, feet and inches and miles; everything else there is metric.
    /// </summary>
    public static UnitPrefs DefaultFor(string locale)
    {
        var region = RegionOf(locale);
        if (region is not null && UsCustomaryRegions.Contains(region))
        {
            return UsCustomary;
        }

        var prefs = Metric;
        if (region == "GB")
        {
            prefs = prefs with
            {
                BodyWeight = BodyWeightUnit.StonePound,
                Height = HeightUnit.FeetInches,
                Distance = DistanceUnit.Mile,
                Pace = PaceUnit.MinutesPerMile,
                ProteinRatio = ProteinRatioDefault(BodyWeightUnit.StonePound),
            };
        }

        if (region is not null && KilojouleRegions.Contains(region))
        {
            prefs = prefs with { Energy = EnergyUnit.Kilojoule };
        }

        return prefs;
    }

    /// <summary>The protein ratio unit follows the body-weight unit unless the user picks otherwise.</summary>
    public static ProteinRatioUnit ProteinRatioDefault(BodyWeightUnit bodyWeight) =>
        bodyWeight == BodyWeightUnit.Kilogram ? ProteinRatioUnit.GramsPerKilogram : ProteinRatioUnit.GramsPerPound;
}

/// <summary>
/// The choices for each preference and their codes in <c>profiles.unit_prefs</c>, in the order the settings
/// screen lists them. Stored profiles already use these codes, so don't rename them.
/// </summary>
public static class UnitPrefOptions
{
    public static CodeMap<BodyWeightUnit> BodyWeight { get; } =
        new((BodyWeightUnit.Kilogram, "kg"), (BodyWeightUnit.Pound, "lb"), (BodyWeightUnit.StonePound, "st_lb"));

    public static CodeMap<LoadUnit> Load { get; } = new((LoadUnit.Kilogram, "kg"), (LoadUnit.Pound, "lb"));

    public static CodeMap<HeightUnit> Height { get; } = new((HeightUnit.Centimetre, "cm"), (HeightUnit.FeetInches, "ft_in"));

    public static CodeMap<CircumferenceUnit> Circumference { get; } =
        new((CircumferenceUnit.Centimetre, "cm"), (CircumferenceUnit.Inch, "in"));

    public static CodeMap<DistanceUnit> Distance { get; } = new((DistanceUnit.Kilometre, "km"), (DistanceUnit.Mile, "mi"));

    public static CodeMap<ShortDistanceUnit> ShortDistance { get; } =
        new((ShortDistanceUnit.Metre, "m"), (ShortDistanceUnit.Yard, "yd"));

    public static CodeMap<PaceUnit> Pace { get; } =
        new((PaceUnit.MinutesPerKilometre, "min_per_km"), (PaceUnit.MinutesPerMile, "min_per_mi"));

    public static CodeMap<FoodMassUnit> FoodMass { get; } = new((FoodMassUnit.Gram, "g"), (FoodMassUnit.Ounce, "oz"));

    public static CodeMap<VolumeUnit> Volume { get; } =
        new((VolumeUnit.Millilitre, "ml"), (VolumeUnit.FluidOunce, "fl_oz"), (VolumeUnit.Cup, "cup"));

    public static CodeMap<EnergyUnit> Energy { get; } = new((EnergyUnit.Kilocalorie, "kcal"), (EnergyUnit.Kilojoule, "kJ"));

    public static CodeMap<TemperatureUnit> Temperature { get; } =
        new((TemperatureUnit.Celsius, "C"), (TemperatureUnit.Fahrenheit, "F"));

    public static CodeMap<GlucoseUnit> Glucose { get; } =
        new((GlucoseUnit.MilligramPerDecilitre, "mg/dL"), (GlucoseUnit.MillimolePerLitre, "mmol/L"));

    public static CodeMap<LabValueSystem> LabValues { get; } =
        new((LabValueSystem.Conventional, "conventional"), (LabValueSystem.SI, "si"));

    public static CodeMap<ProteinRatioUnit> ProteinRatio { get; } =
        new((ProteinRatioUnit.GramsPerKilogram, "g_per_kg"), (ProteinRatioUnit.GramsPerPound, "g_per_lb"));
}

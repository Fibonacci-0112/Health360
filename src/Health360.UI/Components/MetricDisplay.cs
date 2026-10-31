using System.Globalization;
using Health360.Contracts.Body;
using Health360.Domain.Physiology;
using Health360.Domain.Units;

namespace Health360.UI.Components;

/// <summary>
/// How the body and physiology screens name metrics and show their values. Conversions go through
/// <c>Health360.Domain/Units</c>; values are rounded here for presentation only, never before a calculation.
/// </summary>
public static class MetricDisplay
{
    /// <summary>Names, in the order the entry forms list them. Metrics not listed show their code.</summary>
    public static IReadOnlyDictionary<string, string> Names { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MetricCodes.Weight] = "Weight",
        [MetricCodes.BodyFatPercent] = "Body fat",
        ["fat_mass"] = "Fat mass",
        ["lean_mass"] = "Lean mass",
        [MetricCodes.Neck] = "Neck",
        [MetricCodes.WaistAtNavel] = "Waist at the navel",
        [MetricCodes.WaistAtNarrowest] = "Waist at the narrowest point",
        [MetricCodes.Hip] = "Hips",
        ["circ_chest"] = "Chest",
        ["circ_upper_arm"] = "Upper arm",
        ["circ_thigh"] = "Thigh",
        ["circ_calf"] = "Calf",
        ["bp_systolic"] = "Blood pressure, systolic",
        ["bp_diastolic"] = "Blood pressure, diastolic",
        ["spo2"] = "SpO₂",
        ["glucose"] = "Blood glucose",
        ["body_temperature"] = "Body temperature",
        ["steps"] = "Steps",
        ["hrv_rmssd"] = "HRV (RMSSD)",
        [MetricCodes.Rmr] = "Resting metabolic rate",
        [MetricCodes.MaxHr] = "Max heart rate",
        [MetricCodes.RestingHr] = "Resting heart rate",
        [MetricCodes.Lthr] = "Lactate threshold HR",
        [MetricCodes.Vo2Max] = "VO₂max",
        ["vo2max_absolute"] = "VO₂max (absolute)",
        [MetricCodes.Lt1Hr] = "LT1 heart rate",
        [MetricCodes.Lt2Hr] = "LT2 heart rate",
        [MetricCodes.Vt1Hr] = "VT1 heart rate",
        [MetricCodes.Vt2Hr] = "VT2 heart rate",
        ["lt1_power"] = "LT1 power",
        ["lt2_power"] = "LT2 power",
        ["vt1_power"] = "VT1 power",
        ["vt2_power"] = "VT2 power",
        ["lt1_pace"] = "LT1 pace",
        ["lt2_pace"] = "LT2 pace",
        ["vt1_pace"] = "VT1 pace",
        ["vt2_pace"] = "VT2 pace",
        ["ftp"] = "FTP",
        ["threshold_pace"] = "Threshold pace",
        ["fat_mass_arms"] = "Fat mass, arms",
        ["fat_mass_legs"] = "Fat mass, legs",
        ["fat_mass_trunk"] = "Fat mass, trunk",
        ["lean_mass_arms"] = "Lean mass, arms",
        ["lean_mass_legs"] = "Lean mass, legs",
        ["lean_mass_trunk"] = "Lean mass, trunk",
        ["bone_mineral_content"] = "Bone mineral content",
        ["bmd"] = "Bone mineral density",
        ["bmd_t_score"] = "BMD T-score",
        ["bmd_z_score"] = "BMD Z-score",
        ["vat_mass"] = "Visceral fat (VAT)",
        ["android_fat_pct"] = "Android fat",
        ["gynoid_fat_pct"] = "Gynoid fat",
        ["almi"] = "ALMI",
    };

    /// <summary>Unit codes as shown. Codes not listed show as they are.</summary>
    public static IReadOnlyDictionary<string, string> UnitNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["C"] = "°C",
        ["F"] = "°F",
        [UnitCodes.SecondsPerKm] = "min/km",
        [UnitCodes.SecondsPerMile] = "min/mi",
        ["g/cm2"] = "g/cm²",
        ["kg/m2"] = "kg/m²",
        ["sd"] = "SD",
    };

    public static IReadOnlyDictionary<string, string> SourceNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["lab"] = "Lab",
        ["field_test"] = "Field test",
        ["device"] = "Device",
        ["manual"] = "Entered by hand",
    };

    public static IReadOnlyList<string> Sources { get; } = ["manual", "device", "lab", "field_test"];

    public static string Name(string code) => Names.TryGetValue(code, out var name) ? name : code;

    public static string UnitName(string code) => UnitNames.TryGetValue(code, out var name) ? name : code;

    public static string SourceName(string code) => SourceNames.TryGetValue(code, out var name) ? name : code;

    public static bool IsPace(string unit) => unit is UnitCodes.SecondsPerKm or UnitCodes.SecondsPerMile;

    /// <summary>The unit a new value is entered in: the user's preference for the metric's kind, if the metric allows it.</summary>
    public static string PreferredUnit(MetricDefinition metric, UnitPrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(prefs);
        var preferred = metric.CanonicalUnit switch
        {
            "kg" => prefs.BodyWeight == BodyWeightUnit.Kilogram ? "kg" : "lb",
            "cm" => prefs.Circumference == CircumferenceUnit.Centimetre ? "cm" : "in",
            "kcal" => UnitPrefOptions.Energy.ToCode(prefs.Energy),
            "mmol/L" => UnitPrefOptions.Glucose.ToCode(prefs.Glucose),
            "C" => UnitPrefOptions.Temperature.ToCode(prefs.Temperature),
            UnitCodes.SecondsPerKm => prefs.Pace == PaceUnit.MinutesPerKilometre ? UnitCodes.SecondsPerKm : UnitCodes.SecondsPerMile,
            _ => metric.CanonicalUnit,
        };
        return metric.Units.Contains(preferred) ? preferred : metric.Units[0];
    }

    /// <summary>Decimal places shown for a unit.</summary>
    public static int Decimals(string unit) => unit switch
    {
        "bpm" or "kcal" or "kJ" or "steps" or "ms" or "mmHg" or "W" or "g" or "mg/dL" => 0,
        "g/cm2" => 3,
        "L/min" or "sd" => 2,
        _ => 1,
    };

    /// <summary>A value with its unit, rounded for display ("80.0 kg", "4:30 min/km", "52 bpm").</summary>
    public static string Format(double value, string unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (IsPace(unit))
        {
            return $"{PaceText.Format(value)} {UnitName(unit)}";
        }

        var number = Number(value, Decimals(unit));
        return unit == "%" ? $"{number}%" : $"{number} {UnitName(unit)}";
    }

    /// <summary>Rounded half away from zero (<see cref="Rounding.RoundTo"/>), with that many decimals.</summary>
    public static string Number(double value, int decimals) =>
        Rounding.RoundTo(value, decimals).ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>A body weight in kg shown in the user's unit, stone and pounds included.</summary>
    public static string BodyWeight(double kg, BodyWeightUnit unit)
    {
        switch (unit)
        {
            case BodyWeightUnit.StonePound:
                var split = Compound.KgToStoneLb(kg);
                return $"{Number(split.Stone, 0)} st {Number(split.Pounds, 1)} lb";
            case BodyWeightUnit.Pound:
                return Format(Conversion.Convert(kg, Unit.Kilogram, Unit.Pound), "lb");
            default:
                return Format(kg, "kg");
        }
    }

    /// <summary>The number to chart a body weight at, and the unit's name: stone charts in pounds.</summary>
    public static (double Value, string Unit) ChartWeight(double kg, BodyWeightUnit unit) =>
        unit == BodyWeightUnit.Kilogram ? (kg, "kg") : (Conversion.Convert(kg, Unit.Kilogram, Unit.Pound), "lb");

    /// <summary>An energy in kcal shown in the user's energy unit.</summary>
    public static string Energy(double kcal, EnergyUnit unit) =>
        unit == EnergyUnit.Kilojoule ? Format(Conversion.Convert(kcal, Unit.Kilocalorie, Unit.Kilojoule), "kJ") : Format(kcal, "kcal");

    public static string Date(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}

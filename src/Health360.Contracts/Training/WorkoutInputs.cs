using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentValidation;

namespace Health360.Contracts.Training;

/// <summary>Codes and limits shared by the workout tables (migrations 20261103000001 and 20261103000002).</summary>
public static partial class WorkoutCodes
{
    /// <summary>Lifting units a load or plate may be entered in.</summary>
    public static IReadOnlyList<string> LoadUnits { get; } = ["kg", "lb"];

    /// <summary><c>set_logs.set_type</c>, in the order the logger offers them.</summary>
    public static IReadOnlyList<string> SetTypes { get; } = ["working", "warmup", "drop", "backoff", "failure"];

    /// <summary><c>exercises.tracking_type</c>.</summary>
    public static IReadOnlyList<string> TrackingTypes { get; } = ["weight_reps", "reps", "duration", "distance_duration"];

    /// <summary>The longest name a template, program, location or exercise may have.</summary>
    public const int MaxNameLength = 120;

    /// <summary>An equipment or muscle code, as <c>private.is_code_array</c> accepts it.</summary>
    public static bool IsCode(string? code) => code is not null && CodeFormat().IsMatch(code);

    /// <summary>Whether a name fits the tables' check: 1–120 characters once trimmed.</summary>
    public static bool IsName(string? name) => name is not null && name.Trim().Length is > 0 and <= MaxNameLength;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex CodeFormat();
}

/// <summary>
/// One set as the logger edits it, as entered: reps, the load in <paramref name="WeightUnit"/> (kg or lb), RPE or
/// RIR, and the set type. Fields left empty are null.
/// </summary>
public sealed record SetEntry(
    int? Reps,
    double? Weight,
    string WeightUnit,
    double? Rpe = null,
    int? Rir = null,
    string SetType = "working");

/// <summary>The <c>set_logs</c> checks, so the logger can say what's wrong before a write would be quarantined.</summary>
public sealed class SetEntryValidator : AbstractValidator<SetEntry>
{
    public SetEntryValidator()
    {
        RuleFor(x => x.Reps).InclusiveBetween(0, 1000).WithMessage("Reps must be a whole number from 0.");
        RuleFor(x => x.Weight)
            .Must(w => w is null || (double.IsFinite(w.Value) && w.Value >= 0 && w.Value <= 2000))
            .WithMessage("Enter a weight of 0 or more.");
        RuleFor(x => x.WeightUnit).Must(WorkoutCodes.LoadUnits.Contains).WithMessage("Choose kg or lb.");
        RuleFor(x => x.Rpe).Must(r => r is null || (double.IsFinite(r.Value) && r.Value is >= 0 and <= 10))
            .WithMessage("RPE runs from 0 to 10.");
        RuleFor(x => x.Rir).InclusiveBetween(0, 20).WithMessage("RIR runs from 0 to 20.");
        RuleFor(x => x.SetType).Must(WorkoutCodes.SetTypes.Contains).WithMessage("Choose a set type.");
    }
}

/// <summary>Plates of one weight at a location: the weight in the unit printed on them, and how many there are.</summary>
public sealed record PlateInput(double Weight, string Unit, int Count);

/// <summary>A training location as entered: its name, equipment codes, plates and bar (null when there is none).</summary>
public sealed record TrainingLocationInput(
    string Name,
    IReadOnlyList<string> Equipment,
    IReadOnlyList<PlateInput> Plates,
    double? BarWeight,
    string? BarUnit)
{
    /// <summary><c>plate_inventory</c> as stored: <c>[{"weight": 20, "unit": "kg", "count": 4}, …]</c>.</summary>
    public string PlateInventoryJson() =>
        new JsonArray([.. Plates.Select(p => new JsonObject { ["weight"] = p.Weight, ["unit"] = p.Unit, ["count"] = p.Count })])
            .ToJsonString();

    /// <summary><c>equipment</c> as stored: a JSON array of codes.</summary>
    public string EquipmentJson() => new JsonArray([.. Equipment.Select(code => (JsonNode)code)]).ToJsonString();
}

/// <summary>The <c>training_locations</c> checks.</summary>
public sealed class TrainingLocationInputValidator : AbstractValidator<TrainingLocationInput>
{
    public TrainingLocationInputValidator()
    {
        RuleFor(x => x.Name).Must(WorkoutCodes.IsName).WithMessage("Give the location a name.");
        RuleFor(x => x.Equipment)
            .Must(e => e.Count <= 20 && e.All(WorkoutCodes.IsCode) && e.Distinct(StringComparer.Ordinal).Count() == e.Count)
            .WithMessage("Equipment must be distinct codes.");
        RuleFor(x => x.Plates).Must(p => p.Count <= 40).WithMessage("List at most 40 kinds of plate.");
        RuleForEach(x => x.Plates).ChildRules(plate =>
        {
            plate.RuleFor(p => p.Weight).Must(w => double.IsFinite(w) && w > 0 && w <= 1000).WithMessage("A plate weighs more than 0.");
            plate.RuleFor(p => p.Unit).Must(WorkoutCodes.LoadUnits.Contains).WithMessage("Choose kg or lb.");
            plate.RuleFor(p => p.Count).InclusiveBetween(1, 100).WithMessage("Enter how many, from 1 to 100.");
        });
        RuleFor(x => x.BarWeight)
            .Must(w => w is null || (double.IsFinite(w.Value) && w.Value is >= 0 and <= 100))
            .WithMessage("Enter a bar weight from 0 to 100.");
        RuleFor(x => x.BarUnit)
            .Must((x, unit) => x.BarWeight is null ? unit is null : unit is not null && WorkoutCodes.LoadUnits.Contains(unit))
            .WithMessage("Choose kg or lb for the bar.");
    }
}

/// <summary>
/// The prescription for one exercise in a template, stored as <c>template_exercises.set_scheme</c>:
/// <c>{"sets": 3, "min_reps": 8, "max_reps": 12}</c>, plus <c>"load"</c> and <c>"load_unit"</c> when a starting load
/// is given.
/// </summary>
public sealed record SetScheme(int Sets, int MinReps, int MaxReps, double? Load = null, string? LoadUnit = null)
{
    public string ToJson()
    {
        var json = new JsonObject { ["sets"] = Sets, ["min_reps"] = MinReps, ["max_reps"] = MaxReps };
        if (Load is { } load && LoadUnit is { } unit)
        {
            json["load"] = load;
            json["load_unit"] = unit;
        }

        return json.ToJsonString();
    }

    /// <summary>The scheme stored in a row, or null when it isn't one this app wrote (an empty or foreign object).</summary>
    public static SetScheme? Parse(string? json)
    {
        try
        {
            if (string.IsNullOrEmpty(json) || JsonNode.Parse(json) is not JsonObject o
                || o["sets"]?.GetValueKind() != JsonValueKind.Number || o["min_reps"]?.GetValueKind() != JsonValueKind.Number
                || o["max_reps"]?.GetValueKind() != JsonValueKind.Number)
            {
                return null;
            }

            var scheme = new SetScheme(
                o["sets"]!.GetValue<int>(),
                o["min_reps"]!.GetValue<int>(),
                o["max_reps"]!.GetValue<int>(),
                o["load"]?.GetValueKind() == JsonValueKind.Number ? o["load"]!.GetValue<double>() : null,
                o["load_unit"]?.GetValueKind() == JsonValueKind.String ? o["load_unit"]!.GetValue<string>() : null);
            return new SetSchemeValidator().Validate(scheme).IsValid ? scheme : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Sensible limits for a prescription: 1–20 sets, 1 ≤ min ≤ max ≤ 100 reps, a load with its unit.</summary>
public sealed class SetSchemeValidator : AbstractValidator<SetScheme>
{
    public SetSchemeValidator()
    {
        RuleFor(x => x.Sets).InclusiveBetween(1, 20).WithMessage("From 1 to 20 sets.");
        RuleFor(x => x.MinReps).InclusiveBetween(1, 100).WithMessage("At least 1 rep.");
        RuleFor(x => x.MaxReps).GreaterThanOrEqualTo(x => x.MinReps).LessThanOrEqualTo(100)
            .WithMessage("The top of the rep range can't be below the bottom.");
        RuleFor(x => x.Load).Must(l => l is null || (double.IsFinite(l.Value) && l.Value is >= 0 and <= 2000))
            .WithMessage("Enter a load of 0 or more.");
        RuleFor(x => x.LoadUnit)
            .Must((x, unit) => x.Load is null ? unit is null : unit is not null && WorkoutCodes.LoadUnits.Contains(unit))
            .WithMessage("Choose kg or lb.");
    }
}

/// <summary>One exercise in a new template: which, its scheme and the rest between sets.</summary>
public sealed record TemplateExerciseInput(string ExerciseId, SetScheme Scheme, int? RestSeconds = null);

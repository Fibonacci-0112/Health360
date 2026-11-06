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

    /// <summary>Units a set's distance may be entered in.</summary>
    public static IReadOnlyList<string> DistanceUnits { get; } = ["m", "km", "mi", "yd"];

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
/// RIR, the set type, and for timed exercises the time in seconds and the distance in
/// <paramref name="DistanceUnit"/>. Fields left empty are null.
/// </summary>
public sealed record SetEntry(
    int? Reps,
    double? Weight,
    string WeightUnit,
    double? Rpe = null,
    int? Rir = null,
    string SetType = "working",
    double? DurationSeconds = null,
    double? Distance = null,
    string? DistanceUnit = null);

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
        RuleFor(x => x.DurationSeconds)
            .Must(d => d is null || (double.IsFinite(d.Value) && d.Value is >= 0 and <= 86400))
            .WithMessage("Enter a time of up to 24 hours.");
        RuleFor(x => x.Distance)
            .Must(d => d is null || (double.IsFinite(d.Value) && d.Value is >= 0 and <= 1_000_000))
            .WithMessage("Enter a distance of 0 or more.");
        RuleFor(x => x.DistanceUnit)
            .Must((x, unit) => x.Distance is null || (unit is not null && WorkoutCodes.DistanceUnits.Contains(unit)))
            .WithMessage("Choose a distance unit.");
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
public sealed record SetScheme(int Sets, int MinReps, int MaxReps, double? Load = null, string? LoadUnit = null, string? Superset = null)
{
    public string ToJson()
    {
        var json = new JsonObject { ["sets"] = Sets, ["min_reps"] = MinReps, ["max_reps"] = MaxReps };
        if (Superset is not null) json["superset"] = Superset;
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
                o["load_unit"]?.GetValueKind() == JsonValueKind.String ? o["load_unit"]!.GetValue<string>() : null,
                o["superset"]?.GetValueKind() == JsonValueKind.String ? o["superset"]!.GetValue<string>() : null);
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
        RuleFor(x => x.Superset).Must(s => s is null || (s.Length is >= 1 and <= 12 && s.All(char.IsAsciiLetterOrDigit)))
            .WithMessage("A superset group uses up to 12 letters or numbers.");
        RuleFor(x => x.Sets).InclusiveBetween(1, 20).WithMessage("From 1 to 20 sets.");
        RuleFor(x => x.MinReps).InclusiveBetween(1, 100).WithMessage("At least 1 rep.");
        RuleFor(x => x.MaxReps).GreaterThanOrEqualTo(x => x.MinReps).WithMessage("The top of the rep range can't be below the bottom.")
            .LessThanOrEqualTo(100).WithMessage("At most 100 reps.");
        RuleFor(x => x.Load).Must(l => l is null || (double.IsFinite(l.Value) && l.Value is >= 0 and <= 2000))
            .WithMessage("Enter a load of 0 or more.");
        RuleFor(x => x.LoadUnit)
            .Must((x, unit) => x.Load is null ? unit is null : unit is not null && WorkoutCodes.LoadUnits.Contains(unit))
            .WithMessage("Choose kg or lb.");
    }
}

/// <summary>
/// How a template exercise's load moves from session to session, stored as <c>template_exercises.progression</c>:
/// <c>{"kind": "double", "increment": 2.5, "increment_unit": "kg", "deload_after": 3}</c>. <c>linear</c> aims for the
/// top of the scheme's rep range on every set; <c>double</c> climbs through the range first
/// (Health360.Domain/Training/Progression). <paramref name="DeloadAfter"/> misses in a row bring a deload; 0 turns
/// deloads off.
/// </summary>
public sealed record ProgressionRule(string Kind, double Increment, string IncrementUnit, int DeloadAfter = 3)
{
    /// <summary><c>kind</c> values, in the order the template editor offers them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["double", "linear"];

    public string ToJson() =>
        new JsonObject { ["kind"] = Kind, ["increment"] = Increment, ["increment_unit"] = IncrementUnit, ["deload_after"] = DeloadAfter }
            .ToJsonString();

    /// <summary>The rule stored in a row, or null when there is none or it isn't one this app wrote.</summary>
    public static ProgressionRule? Parse(string? json)
    {
        try
        {
            if (string.IsNullOrEmpty(json) || JsonNode.Parse(json) is not JsonObject o
                || o["kind"]?.GetValueKind() != JsonValueKind.String || o["increment"]?.GetValueKind() != JsonValueKind.Number
                || o["increment_unit"]?.GetValueKind() != JsonValueKind.String)
            {
                return null;
            }

            var rule = new ProgressionRule(
                o["kind"]!.GetValue<string>(),
                o["increment"]!.GetValue<double>(),
                o["increment_unit"]!.GetValue<string>(),
                o["deload_after"]?.GetValueKind() == JsonValueKind.Number ? o["deload_after"]!.GetValue<int>() : 3);
            return new ProgressionRuleValidator().Validate(rule).IsValid ? rule : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Limits for a rule: a known kind, an increment above 0 and up to 50 in kg or lb, 0–10 misses before a deload.</summary>
public sealed class ProgressionRuleValidator : AbstractValidator<ProgressionRule>
{
    public ProgressionRuleValidator()
    {
        RuleFor(x => x.Kind).Must(ProgressionRule.Kinds.Contains).WithMessage("Choose linear or double progression.");
        RuleFor(x => x.Increment).Must(i => double.IsFinite(i) && i > 0 && i <= 50).WithMessage("Enter an increment above 0, up to 50.");
        RuleFor(x => x.IncrementUnit).Must(WorkoutCodes.LoadUnits.Contains).WithMessage("Choose kg or lb.");
        RuleFor(x => x.DeloadAfter).InclusiveBetween(0, 10).WithMessage("Deload after 0 to 10 misses (0 for never).");
    }
}

/// <summary>
/// One exercise of a template as entered: which, its scheme, the rest between sets and how its load progresses (none
/// when null). <paramref name="Id"/> is the <c>template_exercises</c> row it keeps when a template is edited; null for
/// a new one.
/// </summary>
public sealed record TemplateExerciseInput(
    string ExerciseId,
    SetScheme Scheme,
    int? RestSeconds = null,
    ProgressionRule? Progression = null,
    string? Id = null);

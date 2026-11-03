using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Health360.Importers;

/// <summary>One system exercise, mapped to the columns of <c>public.exercises</c>.</summary>
public sealed record ImportedExercise(
    string ExternalId,
    string Name,
    string TrackingType,
    IReadOnlyList<string> Equipment,
    IReadOnlyList<string> PrimaryMuscles,
    IReadOnlyList<string> SecondaryMuscles);

/// <summary>An entry left out of the import, with why. The reason names fields, never values.</summary>
public sealed record SkippedEntry(int Index, string? ExternalId, string Reason);

/// <summary>The exercises to import and the entries left out.</summary>
public sealed record ParsedDataset(IReadOnlyList<ImportedExercise> Exercises, IReadOnlyList<SkippedEntry> Skipped);

/// <summary>
/// free-exercise-db (github.com/yuhonas/free-exercise-db, Unlicense): <c>dist/exercises.json</c>, an array of
/// objects with <c>id</c>, <c>name</c>, <c>category</c>, <c>equipment</c>, <c>primaryMuscles</c> and
/// <c>secondaryMuscles</c>. Instructions and images are not imported.
/// </summary>
public static partial class FreeExerciseDb
{
    public const string DatasetCode = "free-exercise-db";

    public const string Attribution = "free-exercise-db by Yuhonas and contributors (github.com/yuhonas/free-exercise-db), Unlicense";

    /// <summary>The longest name and id the table accepts.</summary>
    private const int MaxLength = 120;

    /// <summary>
    /// Reads the dataset. Equipment and muscle names become codes (<c>"e-z curl bar"</c> → <c>e_z_curl_bar</c>), the
    /// format <c>private.is_code_array</c> accepts. The tracking type follows the category and equipment:
    /// <c>cardio</c> is distance and time, <c>stretching</c> is time, body-weight exercises are reps, and the rest
    /// weight and reps. An entry the table would reject, or a repeated id, is skipped and reported.
    /// </summary>
    /// <exception cref="JsonException">The file isn't a JSON array.</exception>
    public static ParsedDataset Parse(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected a JSON array of exercises.");
        }

        var exercises = new List<ImportedExercise>();
        var skipped = new List<SkippedEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var id = entry.ValueKind == JsonValueKind.Object ? Text(entry, "id") : null;
            var exercise = Map(entry, out var reason);
            if (exercise is null)
            {
                skipped.Add(new SkippedEntry(index, id, reason!));
            }
            else if (!seen.Add(exercise.ExternalId))
            {
                skipped.Add(new SkippedEntry(index, id, "repeated id"));
            }
            else
            {
                exercises.Add(exercise);
            }

            index++;
        }

        return new ParsedDataset(exercises, skipped);
    }

    /// <summary>
    /// A name as a code: lower case, each run of other characters an underscore, at most 40 characters, starting with
    /// a letter. Null when nothing usable is left.
    /// </summary>
    public static string? ToCode(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var code = NonCodeCharacters().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        return CodeFormat().IsMatch(code) ? code : null;
    }

    private static ImportedExercise? Map(JsonElement entry, out string? reason)
    {
        reason = null;
        if (entry.ValueKind != JsonValueKind.Object)
        {
            reason = "not an object";
            return null;
        }

        var id = Text(entry, "id")?.Trim();
        var name = Text(entry, "name")?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
        {
            reason = "id missing or too long";
            return null;
        }

        if (string.IsNullOrEmpty(name) || name.Length > MaxLength)
        {
            reason = "name missing or too long";
            return null;
        }

        var equipmentName = Text(entry, "equipment");
        var equipment = equipmentName is null ? [] : new List<string?> { ToCode(equipmentName) };
        var primary = Codes(entry, "primaryMuscles");
        var secondary = Codes(entry, "secondaryMuscles");
        if (equipment.Contains(null) || primary is null || secondary is null)
        {
            reason = "equipment or muscles not usable as codes";
            return null;
        }

        var category = ToCode(Text(entry, "category"));
        var trackingType = category switch
        {
            "cardio" => "distance_duration",
            "stretching" => "duration",
            _ when equipment.Count == 0 || equipment[0] == "body_only" => "reps",
            _ => "weight_reps",
        };

        return new ImportedExercise(id, name, trackingType, [.. equipment!], primary, secondary);
    }

    /// <summary>The array's names as distinct codes in their order, or null if one isn't usable.</summary>
    private static List<string>? Codes(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var array) || array.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var codes = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var code = item.ValueKind == JsonValueKind.String ? ToCode(item.GetString()) : null;
            if (code is null)
            {
                return null;
            }

            if (!codes.Contains(code))
            {
                codes.Add(code);
            }
        }

        // private.is_code_array allows at most 20.
        return codes.Count <= 20 ? codes : null;
    }

    private static string? Text(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Normalize(NormalizationForm.FormC)
            : null;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonCodeCharacters();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex CodeFormat();
}

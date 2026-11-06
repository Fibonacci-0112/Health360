using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Health360.Contracts.Training;
using Health360.Domain.Ids;
using Health360.Domain.Units;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>One <c>exercises</c> row: a system exercise (no household) or a household's custom one.</summary>
public sealed class ExerciseRow
{
    public string Id { get; set; } = "";

    public string? HouseholdId { get; set; }

    public string Name { get; set; } = "";

    public string TrackingType { get; set; } = "";

    /// <summary>JSON text: an array of equipment codes.</summary>
    public string? Equipment { get; set; }

    /// <summary>JSON text: an array of muscle codes.</summary>
    public string? PrimaryMuscles { get; set; }

    /// <summary>JSON text: an array of muscle codes.</summary>
    public string? SecondaryMuscles { get; set; }

    public bool IsCustom => HouseholdId is not null;

    /// <summary>Whether a set of it records a load (the logger shows weight and reps, and PRs apply).</summary>
    public bool TracksLoad => TrackingType == "weight_reps";

    public IReadOnlyList<string> EquipmentCodes => Codes(Equipment);

    public IReadOnlyList<string> PrimaryMuscleCodes => Codes(PrimaryMuscles);

    public IReadOnlyList<string> SecondaryMuscleCodes => Codes(SecondaryMuscles);

    /// <summary>The codes in a stored array; empty when it can't be read, so one bad synced row can't break a screen.</summary>
    internal static IReadOnlyList<string> Codes(string? json)
    {
        try
        {
            return string.IsNullOrEmpty(json) || JsonNode.Parse(json) is not JsonArray array
                ? []
                : [.. array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var code) ? code : null).OfType<string>()];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// The exercise library in the local database: system exercises (reference stream) and the households' custom ones
/// (household stream). Every row synced to this device is one the user may use.
/// </summary>
public static class ExerciseLibrary
{
    private const string Columns =
        "id AS Id, household_id AS HouseholdId, name AS Name, tracking_type AS TrackingType, equipment AS Equipment, " +
        "primary_muscles AS PrimaryMuscles, secondary_muscles AS SecondaryMuscles";

    /// <summary>
    /// Exercises whose name contains <paramref name="query"/> (case-insensitive for ASCII; all of them when empty),
    /// names starting with it first, then alphabetical.
    /// </summary>
    public static async Task<ExerciseRow[]> SearchAsync(IPowerSyncDatabase db, string? query, int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(db);
        var text = (query ?? "").Trim();
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        return await db.GetAll<ExerciseRow>(
            $"SELECT {Columns} FROM exercises WHERE name LIKE ? ESCAPE '\\' " +
            "ORDER BY CASE WHEN name LIKE ? ESCAPE '\\' THEN 0 ELSE 1 END, name COLLATE NOCASE, id LIMIT ?",
            ["%" + escaped + "%", escaped + "%", Math.Clamp(limit, 1, 500)]).ConfigureAwait(false);
    }

    public static async Task<ExerciseRow?> GetAsync(IPowerSyncDatabase db, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetOptional<ExerciseRow>($"SELECT {Columns} FROM exercises WHERE id = ?", [id]).ConfigureAwait(false);
    }

    /// <summary>Adds a custom exercise to the household; it uploads when online and syncs to the other members.</summary>
    /// <exception cref="ArgumentException">The name, tracking type or a code isn't one the table accepts.</exception>
    public static async Task<string> AddCustomAsync(
        IPowerSyncDatabase db,
        string householdId,
        string name,
        string trackingType,
        IReadOnlyList<string> equipment,
        IReadOnlyList<string> primaryMuscles,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(householdId);
        ArgumentNullException.ThrowIfNull(equipment);
        ArgumentNullException.ThrowIfNull(primaryMuscles);
        ArgumentNullException.ThrowIfNull(clock);
        if (!WorkoutCodes.IsName(name))
        {
            throw new ArgumentException("A name of 1 to 120 characters.", nameof(name));
        }

        if (!WorkoutCodes.TrackingTypes.Contains(trackingType))
        {
            throw new ArgumentException("Unknown tracking type.", nameof(trackingType));
        }

        CheckCodes(equipment, nameof(equipment));
        CheckCodes(primaryMuscles, nameof(primaryMuscles));
        var id = NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.Execute(
            "INSERT INTO exercises (id, household_id, name, tracking_type, equipment, primary_muscles, secondary_muscles, created_at, updated_at) " +
            "VALUES (?, ?, ?, ?, ?, ?, '[]', ?, ?)",
            [id, householdId, name.Trim(), trackingType, CodesJson(equipment), CodesJson(primaryMuscles), now, now]).ConfigureAwait(false);
        return id;
    }

    internal static string NewId(TimeProvider clock) => UuidV7.Create(clock, RandomNumberGenerator.Fill).ToString();

    private static void CheckCodes(IReadOnlyList<string> codes, string name)
    {
        if (codes.Count > 20 || !codes.All(WorkoutCodes.IsCode) || codes.Distinct(StringComparer.Ordinal).Count() != codes.Count)
        {
            throw new ArgumentException("At most 20 distinct codes.", name);
        }
    }

    private static string CodesJson(IReadOnlyList<string> codes) => new JsonArray([.. codes.Select(c => (JsonNode)c)]).ToJsonString();
}

/// <summary>One <c>training_locations</c> row.</summary>
public sealed class TrainingLocationRow
{
    public string Id { get; set; } = "";

    public string HouseholdId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>JSON text: an array of equipment codes.</summary>
    public string? Equipment { get; set; }

    /// <summary>JSON text: <c>[{"weight", "unit", "count"}, …]</c>.</summary>
    public string? PlateInventory { get; set; }

    public double? BarWeight { get; set; }

    public string? BarUnit { get; set; }

    public double? BarWeightKg { get; set; }

    public IReadOnlyList<string> EquipmentCodes => ExerciseRow.Codes(Equipment);

    /// <summary>The plates, leaving out entries that can't be read.</summary>
    public IReadOnlyList<PlateInput> Plates
    {
        get
        {
            try
            {
                if (string.IsNullOrEmpty(PlateInventory) || JsonNode.Parse(PlateInventory) is not JsonArray array)
                {
                    return [];
                }

                var plates = new List<PlateInput>();
                foreach (var item in array.OfType<JsonObject>())
                {
                    if (item["weight"] is JsonValue w && w.TryGetValue<double>(out var weight)
                        && item["unit"] is JsonValue u && u.TryGetValue<string>(out var unit)
                        && item["count"] is JsonValue c && c.TryGetValue<int>(out var count))
                    {
                        plates.Add(new PlateInput(weight, unit, count));
                    }
                }

                return plates;
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}

/// <summary>A household's training locations (<c>training_locations</c>, H) in the local database.</summary>
public static partial class TrainingLocations
{
    private const string Columns =
        "id AS Id, household_id AS HouseholdId, name AS Name, equipment AS Equipment, plate_inventory AS PlateInventory, " +
        "bar_weight AS BarWeight, bar_unit AS BarUnit, bar_weight_kg AS BarWeightKg";

    /// <summary>Every location of the user's households, by name.</summary>
    public static async Task<TrainingLocationRow[]> ListAsync(IPowerSyncDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<TrainingLocationRow>($"SELECT {Columns} FROM training_locations ORDER BY name COLLATE NOCASE, id", [])
            .ConfigureAwait(false);
    }

    /// <summary>Adds a location to the household, with the bar's canonical kg as the server's trigger computes it.</summary>
    /// <exception cref="ValidationException">The input fails <see cref="TrainingLocationInputValidator"/>; nothing is written.</exception>
    public static async Task<string> AddAsync(IPowerSyncDatabase db, string householdId, TrainingLocationInput input, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(householdId);
        ArgumentNullException.ThrowIfNull(clock);
        new TrainingLocationInputValidator().ValidateAndThrow(input);
        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        var barKg = input.BarWeight is { } bar ? UnitCodes.Convert(bar, input.BarUnit!, "kg") : null;
        await db.Execute(
            "INSERT INTO training_locations (id, household_id, name, equipment, plate_inventory, bar_weight, bar_unit, bar_weight_kg, " +
            "created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            [id, householdId, input.Name.Trim(), input.EquipmentJson(), input.PlateInventoryJson(), input.BarWeight, input.BarUnit, barKg, now, now])
            .ConfigureAwait(false);
        return id;
    }

    /// <summary>Deletes a location; programs that used it keep going without one (the server sets theirs to null).</summary>
    public static async Task DeleteAsync(IPowerSyncDatabase db, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.WriteTransaction(async tx =>
        {
            _ = await tx.GetOptional<WorkoutPlans.IdOnly>("SELECT id AS Id FROM training_locations WHERE id = ?", [id]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The location is not in the local database.");
            // Mirror the server's ON DELETE SET NULL locally; the update uploads too, which the server accepts either way.
            await tx.Execute("UPDATE programs SET location_id = NULL WHERE location_id = ?", [id]).ConfigureAwait(false);
            await tx.Execute("DELETE FROM training_locations WHERE id = ?", [id]).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

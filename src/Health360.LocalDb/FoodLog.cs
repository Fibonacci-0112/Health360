using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Health360.Domain.Units;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>A food as it can be logged: its basis, values per 100 of it, and named portions.</summary>
public sealed record LoggableFood(
    string Id, string Name, string? Brand, string Source, FoodBasis Basis, NutrientAmounts Per100, IReadOnlyList<FoodPortion> Portions);

/// <summary>One <c>foods</c> row (a household's food, synced on the household stream).</summary>
public sealed class FoodRow
{
    public string Id { get; set; } = "";

    public string? HouseholdId { get; set; }

    public string Source { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Brand { get; set; }

    public string Basis { get; set; } = "";

    public double? Energy100 { get; set; }

    public double? Protein100 { get; set; }

    public double? Carbohydrate100 { get; set; }

    public double? Fat100 { get; set; }

    /// <summary>JSON text: other nutrients per 100 by code.</summary>
    public string? Nutrients100 { get; set; }

    /// <summary>JSON text: <c>[{"name", "amount"}]</c>.</summary>
    public string? Portions { get; set; }

    /// <summary>
    /// The food as the log uses it, or null when a synced row can't be read (an unknown basis), so one bad row can't
    /// break the screen. A malformed nutrient map or portion list reads as empty: those values are then unknown.
    /// </summary>
    public LoggableFood? ToFood()
    {
        if (!FoodBases.TryParse(Basis, out var basis))
        {
            return null;
        }

        var amounts = new List<KeyValuePair<string, double>>();
        AddMacro(amounts, Nutrient.Energy, Energy100);
        AddMacro(amounts, Nutrient.Protein, Protein100);
        AddMacro(amounts, Nutrient.Carbohydrate, Carbohydrate100);
        AddMacro(amounts, Nutrient.Fat, Fat100);
        amounts.AddRange(FoodJson.NutrientMap(Nutrients100).Where(kv => !IsMacro(kv.Key)));
        return new LoggableFood(Id, Name, Brand, Source, basis, NutrientAmounts.Create(amounts), FoodJson.Portions(Portions));
    }

    private static void AddMacro(List<KeyValuePair<string, double>> amounts, Nutrient nutrient, double? value)
    {
        if (value is { } v && double.IsFinite(v) && v >= 0)
        {
            amounts.Add(KeyValuePair.Create(nutrient.Code, v));
        }
    }

    internal static bool IsMacro(string code) => Nutrient.Macros.Any(m => m.Code == code);
}

/// <summary>One <c>food_log_entries</c> row as the Food tab shows it.</summary>
public sealed class FoodLogEntryRow
{
    public string Id { get; set; } = "";

    public string LoggedAt { get; set; } = "";

    public string LocalDate { get; set; } = "";

    public string Meal { get; set; } = "";

    public string EntryMethod { get; set; } = "";

    public string? FoodId { get; set; }

    public string Name { get; set; } = "";

    public string? Brand { get; set; }

    public double? Quantity { get; set; }

    public string? QuantityUnit { get; set; }

    public string? PortionName { get; set; }

    public double? Energy { get; set; }

    public double? Protein { get; set; }

    public double? Carbohydrate { get; set; }

    public double? Fat { get; set; }

    /// <summary>JSON text: other nutrients in the amount eaten, by code.</summary>
    public string? Nutrients { get; set; }

    /// <summary>JSON text: codes whose amount is only a lower bound.</summary>
    public string? IncompleteNutrients { get; set; }

    public long IsEstimate { get; set; }

    /// <summary>Everything the entry records, macronutrients included; unknown nutrients are absent.</summary>
    public NutrientAmounts Amounts()
    {
        var amounts = new List<KeyValuePair<string, double>>();
        void Add(Nutrient nutrient, double? value)
        {
            if (value is { } v && double.IsFinite(v) && v >= 0)
            {
                amounts.Add(KeyValuePair.Create(nutrient.Code, v));
            }
        }

        Add(Nutrient.Energy, Energy);
        Add(Nutrient.Protein, Protein);
        Add(Nutrient.Carbohydrate, Carbohydrate);
        Add(Nutrient.Fat, Fat);
        amounts.AddRange(FoodJson.NutrientMap(Nutrients).Where(kv => !FoodRow.IsMacro(kv.Key)));
        return NutrientAmounts.Create(amounts);
    }

    /// <summary>The codes the entry itself marks as lower bounds (a recipe with ingredients that didn't list them).</summary>
    public IReadOnlyList<string> Incomplete() => FoodJson.Codes(IncompleteNutrients);
}

/// <summary>
/// The household's own foods (<c>foods</c>, H) in the local database: entered by a member, or copies of catalogue
/// foods. Catalogue foods aren't synced; they are searched online through the API (not built yet).
/// </summary>
public static class FoodLibrary
{
    private const string Columns =
        "f.id AS Id, f.household_id AS HouseholdId, f.source AS Source, f.name AS Name, f.brand AS Brand, f.basis AS Basis, " +
        "f.energy_100 AS Energy100, f.protein_100 AS Protein100, f.carbohydrate_100 AS Carbohydrate100, f.fat_100 AS Fat100, " +
        "f.nutrients_100 AS Nutrients100, f.portions AS Portions";

    /// <summary>The household foods whose name or brand contains <paramref name="query"/>, by name; all of them when it is empty.</summary>
    public static Task<FoodRow[]> SearchAsync(IPowerSyncDatabase db, string? query, int limit)
    {
        ArgumentNullException.ThrowIfNull(db);
        var pattern = "%" + EscapeLike(query?.Trim() ?? "") + "%";
        return db.GetAll<FoodRow>(
            $"SELECT {Columns} FROM foods f WHERE f.household_id IS NOT NULL " +
            "AND (f.name LIKE ? ESCAPE '\\' OR coalesce(f.brand, '') LIKE ? ESCAPE '\\') " +
            "ORDER BY f.name COLLATE NOCASE, f.id LIMIT ?",
            [pattern, pattern, limit]);
    }

    /// <summary>The foods this profile logged most recently that are still on this device, newest first.</summary>
    public static Task<FoodRow[]> RecentAsync(IPowerSyncDatabase db, string profileId, int limit)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.GetAll<FoodRow>(
            $"SELECT {Columns} FROM foods f JOIN (SELECT food_id, max({WorkoutLog.Instant("logged_at")}) AS last FROM food_log_entries " +
            "WHERE profile_id = ? AND food_id IS NOT NULL GROUP BY food_id) r ON r.food_id = f.id " +
            "WHERE f.household_id IS NOT NULL ORDER BY r.last DESC, f.id LIMIT ?",
            [profileId, limit]);
    }

    /// <summary>
    /// Fires now and after every change to what the Food tab offers: the profile's households and the household foods,
    /// whether written here or synced from another device. The caller reloads its choices.
    /// </summary>
    public static IAsyncEnumerable<WorkoutLog.ChangeStamp[]> WatchChoices(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<WorkoutLog.ChangeStamp>(
            "SELECT (SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM household_members WHERE profile_id = ?) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') || ';' FROM households) || " +
            "(SELECT count(*) || ':' || coalesce(max(updated_at), '') FROM foods WHERE household_id IS NOT NULL) AS Stamp",
            [profileId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static Task<FoodRow?> GetAsync(IPowerSyncDatabase db, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.GetOptional<FoodRow>($"SELECT {Columns} FROM foods f WHERE f.id = ? AND f.household_id IS NOT NULL", [id]);
    }

    /// <summary>
    /// Adds a food to the household, stored per 100 of its basis; it uploads when online and syncs to the other
    /// members.
    /// </summary>
    public static async Task<string> AddAsync(IPowerSyncDatabase db, string householdId, FoodInput input, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(householdId);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(clock);
        new FoodInputValidator().ValidateAndThrow(input);
        var per100 = input.Per100();
        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.WriteTransaction(async tx =>
        {
            _ = await tx.GetOptional<WorkoutPlans.IdOnly>("SELECT id AS Id FROM households WHERE id = ?", [householdId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("That household is no longer on this device.");
            await tx.Execute(
                "INSERT INTO foods (id, household_id, source, name, brand, basis, energy_100, protein_100, carbohydrate_100, fat_100, " +
                "nutrients_100, portions, verified, created_at, updated_at) VALUES (?, ?, 'custom', ?, ?, ?, ?, ?, ?, ?, '{}', ?, 0, ?, ?)",
                [id, householdId, input.Name.Trim(), string.IsNullOrWhiteSpace(input.Brand) ? null : input.Brand.Trim(),
                    FoodBases.Code(input.Basis), per100[Nutrient.Energy], per100[Nutrient.Protein], per100[Nutrient.Carbohydrate],
                    per100[Nutrient.Fat], input.PortionsJson(), now, now]).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>
/// The food log (<c>food_log_entries</c>, U) in the local database. Each entry keeps a snapshot of its name and the
/// nutrients in the amount eaten, computed when it is logged, so the log works offline and never changes when a food
/// is corrected later. Writes upload when online; the server recomputes <c>base_amount</c> for a unit as the app does.
/// </summary>
public static class FoodLog
{
    public static IReadOnlyList<string> Meals { get; } = ["breakfast", "lunch", "dinner", "snack"];

    /// <summary>The units an amount may be entered in, for a food per 100 g or per 100 ml (the database's list).</summary>
    public static IReadOnlyList<string> UnitsFor(FoodBasis basis) => basis == FoodBasis.Mass
        ? ["g", "kg", "oz", "lb"]
        : ["ml", "l", "fl_oz", "cup", "tbsp", "tsp"];

    private static readonly string DaySql =
        "SELECT id AS Id, logged_at AS LoggedAt, local_date AS LocalDate, meal AS Meal, entry_method AS EntryMethod, food_id AS FoodId, " +
        "name AS Name, brand AS Brand, quantity AS Quantity, quantity_unit AS QuantityUnit, portion_name AS PortionName, " +
        "energy AS Energy, protein AS Protein, carbohydrate AS Carbohydrate, fat AS Fat, nutrients AS Nutrients, " +
        "incomplete_nutrients AS IncompleteNutrients, coalesce(is_estimate, 0) AS IsEstimate " +
        "FROM food_log_entries WHERE profile_id = ? AND local_date = ? ORDER BY " + WorkoutLog.Instant("logged_at") + ", id";

    /// <summary>The profile's entries for one day, in the order they were logged, now and after every change.</summary>
    public static IAsyncEnumerable<FoodLogEntryRow[]> WatchDay(PowerSyncDatabase db, string profileId, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<FoodLogEntryRow>(DaySql, [profileId, Day(date)],
            new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static Task<FoodLogEntryRow[]> DayAsync(IPowerSyncDatabase db, string profileId, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.GetAll<FoodLogEntryRow>(DaySql, [profileId, Day(date)]);
    }

    /// <summary>
    /// The nutrients in <paramref name="quantity"/> of a food: converted to g or ml of its basis
    /// (<see cref="FoodAmount.ToBase(double, Unit, FoodBasis)"/>), or a number of its named portions, then its per-100
    /// values scaled (<see cref="NutrientAmounts.ForAmount"/>).
    /// </summary>
    /// <param name="food">The food, as <see cref="FoodRow.ToFood"/> reads it.</param>
    /// <param name="quantity">The amount as entered: in <paramref name="unit"/>, or a number of portions.</param>
    /// <param name="unit">A unit code from <see cref="UnitsFor"/>, or null for a portion.</param>
    /// <param name="portionName">The portion's name when <paramref name="unit"/> is null.</param>
    /// <exception cref="ArgumentException">The amount, unit or portion doesn't fit the food.</exception>
    public static (double BaseAmount, NutrientAmounts Amounts) Compute(LoggableFood food, double quantity, string? unit, string? portionName)
    {
        ArgumentNullException.ThrowIfNull(food);
        if (!double.IsFinite(quantity) || quantity <= 0 || quantity > 100000)
        {
            throw new ArgumentException("Enter an amount more than 0.", nameof(quantity));
        }

        double baseAmount;
        if (unit is null)
        {
            var portion = food.Portions.FirstOrDefault(p => p.Name == portionName)
                ?? throw new ArgumentException("Choose one of the food's portions.", nameof(portionName));
            baseAmount = FoodAmount.ToBase(quantity, portion);
        }
        else
        {
            if (!UnitsFor(food.Basis).Contains(unit) || !Unit.TryParse(unit, out var parsed))
            {
                throw new ArgumentException(food.Basis == FoodBasis.Mass
                    ? "This food is listed by weight: enter a weight or one of its portions."
                    : "This food is listed by volume: enter a volume or one of its portions.", nameof(unit));
            }

            baseAmount = FoodAmount.ToBase(quantity, parsed, food.Basis);
        }

        return (baseAmount, food.Per100.ForAmount(baseAmount));
    }

    /// <summary>Logs an amount of a household food in a meal on <paramref name="date"/> (this device's calendar).</summary>
    public static async Task<string> LogFoodAsync(
        IPowerSyncDatabase db, string profileId, string foodId, string meal, DateOnly date, double quantity, string? unit, string? portionName,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        CheckMeal(meal);
        var id = ExerciseLibrary.NewId(clock);
        await db.WriteTransaction(async tx =>
        {
            var row = await tx.GetOptional<FoodRow>(
                "SELECT id AS Id, household_id AS HouseholdId, source AS Source, name AS Name, brand AS Brand, basis AS Basis, " +
                "energy_100 AS Energy100, protein_100 AS Protein100, carbohydrate_100 AS Carbohydrate100, fat_100 AS Fat100, " +
                "nutrients_100 AS Nutrients100, portions AS Portions FROM foods WHERE id = ? AND household_id IS NOT NULL", [foodId]).ConfigureAwait(false)
                ?? throw new InvalidOperationException("That food is no longer on this device.");
            var food = row.ToFood() ?? throw new InvalidOperationException("That food can't be read on this device.");
            var (baseAmount, amounts) = Compute(food, quantity, unit, portionName);
            var now = Timestamps.UtcNow(clock);
            await tx.Execute(
                "INSERT INTO food_log_entries (id, profile_id, logged_at, local_date, meal, entry_method, food_id, name, brand, quantity, " +
                "quantity_unit, portion_name, basis, base_amount, energy, protein, carbohydrate, fat, nutrients, incomplete_nutrients, " +
                "is_estimate, created_at, updated_at) VALUES (?, ?, ?, ?, ?, 'search', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, '[]', 0, ?, ?)",
                [id, profileId, LoggedAt(date, clock), Day(date), meal, food.Id, Trim(food.Name, 200), food.Brand is null ? null : Trim(food.Brand, 120),
                    quantity, unit ?? "portion", unit is null ? portionName : null, FoodBases.Code(food.Basis), baseAmount,
                    amounts[Nutrient.Energy], amounts[Nutrient.Protein], amounts[Nutrient.Carbohydrate], amounts[Nutrient.Fat],
                    OtherNutrientsJson(amounts), now, now]).ConfigureAwait(false);
        }).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Logs calories and macronutrients without a food (<c>quick_add</c>). At least one value is needed; the others
    /// stay unknown.
    /// </summary>
    public static async Task<string> QuickAddAsync(
        IPowerSyncDatabase db, string profileId, string meal, DateOnly date, string? name,
        double? energy, double? protein, double? carbohydrate, double? fat, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        CheckMeal(meal);
        double?[] values = [energy, protein, carbohydrate, fat];
        if (values.All(v => v is null))
        {
            throw new ArgumentException("Enter calories or at least one macronutrient.");
        }

        if (values.Any(v => v is { } x && (!double.IsFinite(x) || x < 0 || x > 100000)))
        {
            throw new ArgumentException("Calories and macronutrients can't be negative.");
        }

        var label = string.IsNullOrWhiteSpace(name) ? "Quick add" : name.Trim();
        if (label.Length > 200)
        {
            throw new ArgumentException("Keep the name to 200 characters.", nameof(name));
        }

        var id = ExerciseLibrary.NewId(clock);
        var now = Timestamps.UtcNow(clock);
        await db.Execute(
            "INSERT INTO food_log_entries (id, profile_id, logged_at, local_date, meal, entry_method, name, energy, protein, carbohydrate, fat, " +
            "nutrients, incomplete_nutrients, is_estimate, created_at, updated_at) VALUES (?, ?, ?, ?, ?, 'quick_add', ?, ?, ?, ?, ?, '{}', '[]', 0, ?, ?)",
            [id, profileId, LoggedAt(date, clock), Day(date), meal, label, energy, protein, carbohydrate, fat, now, now]).ConfigureAwait(false);
        return id;
    }

    public static Task DeleteAsync(IPowerSyncDatabase db, string profileId, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Execute("DELETE FROM food_log_entries WHERE id = ? AND profile_id = ?", [id, profileId]);
    }

    /// <summary>
    /// The day's total (<see cref="NutrientTotal.Sum"/>): a nutrient some entries recorded and others didn't, or that
    /// an entry marks as a lower bound, is incomplete, so the total is only a lower bound.
    /// </summary>
    public static NutrientTotal Total(IReadOnlyList<FoodLogEntryRow> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sum = NutrientTotal.Sum(entries.Select(e => e.Amounts()));
        var marked = entries.SelectMany(e => e.Incomplete()).Where(sum.Amounts.Values.ContainsKey);
        return sum with { Incomplete = sum.Incomplete.Concat(marked).ToHashSet(StringComparer.Ordinal) };
    }

    /// <summary>
    /// When an entry was logged: now for today; noon on another day, so an entry added for yesterday sorts within
    /// that day.
    /// </summary>
    private static string LoggedAt(DateOnly date, TimeProvider clock)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        if (date == today)
        {
            return Timestamps.UtcNow(clock);
        }

        var zone = clock.LocalTimeZone;
        var noon = date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(noon, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static void CheckMeal(string meal)
    {
        if (!Meals.Contains(meal))
        {
            throw new ArgumentException("Choose breakfast, lunch, dinner or a snack.", nameof(meal));
        }
    }

    private static string OtherNutrientsJson(NutrientAmounts amounts)
    {
        var json = new JsonObject();
        foreach (var (code, amount) in amounts.Values.Where(kv => !FoodRow.IsMacro(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            json[code] = amount;
        }

        return json.ToJsonString();
    }

    private static string Trim(string text, int max) => text.Trim() is var t && t.Length > max ? t[..max] : t;

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>Reads the nutrient maps and portion lists synced rows store as JSON text, tolerating malformed values.</summary>
internal static class FoodJson
{
    public static IEnumerable<KeyValuePair<string, double>> NutrientMap(string? json)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(json))
        {
            return map;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return map;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (NutrientAmounts.IsValidCode(property.Name) && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetDouble(out var v) && double.IsFinite(v) && v >= 0)
                {
                    map[property.Name] = v;
                }
            }
        }
        catch (JsonException)
        {
            map.Clear();
        }

        return map;
    }

    public static IReadOnlyList<FoodPortion> Portions(string? json)
    {
        var portions = new List<FoodPortion>();
        if (string.IsNullOrEmpty(json))
        {
            return portions;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return portions;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString())
                    && item.TryGetProperty("amount", out var amount) && amount.ValueKind == JsonValueKind.Number
                    && amount.TryGetDouble(out var a) && double.IsFinite(a) && a > 0
                    && portions.TrueForAll(p => p.Name != name.GetString()))
                {
                    portions.Add(new FoodPortion(name.GetString()!, a));
                }
            }
        }
        catch (JsonException)
        {
            portions.Clear();
        }

        return portions;
    }

    public static IReadOnlyList<string> Codes(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? [.. document.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String && NutrientAmounts.IsValidCode(e.GetString()))
                    .Select(e => e.GetString()!).Distinct(StringComparer.Ordinal)]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

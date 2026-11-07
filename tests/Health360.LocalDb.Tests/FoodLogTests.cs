using FluentValidation;
using Health360.Contracts.Nutrition;
using Health360.Domain.Nutrition;
using Health360.LocalDb.Tests.Support;

namespace Health360.LocalDb.Tests;

public sealed class FoodLogTests
{
    private const string Profile = "10000000-0000-7000-8000-000000000001";
    private const string Other = "10000000-0000-7000-8000-000000000002";
    private const string Household = "20000000-0000-7000-8000-000000000001";
    private static readonly DateOnly Day = new(2026, 11, 7);
    // Each read moves a second on, so entries logged one after another sort in that order (ids within one millisecond
    // are random after their timestamp).
    private readonly ZonedClock clock = new(new DateTimeOffset(2026, 11, 7, 18, 30, 0, TimeSpan.Zero), TimeZoneInfo.Utc, TimeSpan.FromSeconds(1));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A granola label: 30 g serving, 130 kcal, 3 g protein, 20 g carbohydrate, fat not given.</summary>
    private static FoodInput Granola() =>
        new("Granola", " Oat Co ", FoodBasis.Mass, 30, 130, 3, 20, null, [new PortionInput("1 serving", 30), new PortionInput("1 cup", 110)]);

    private static async Task<TestDatabase> OpenAsync()
    {
        var t = await TestDatabase.OpenAsync();
        await t.SeedProfileAsync(Profile);
        await t.SeedProfileAsync(Other, "user-b");
        // As the household stream brings them down: synced rows, not local writes.
        await t.Db.WriteTransaction(async tx =>
        {
            await tx.Execute("INSERT INTO households (id, name) VALUES (?, 'Home')", [Household]);
            await tx.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('m1', ?, ?)", [Household, Profile]);
            await tx.Execute("DELETE FROM ps_crud");
        });
        return t;
    }

    [Fact]
    public async Task A_food_entered_per_serving_is_stored_per_100_and_logs_a_snapshot_for_the_amount_eaten()
    {
        await using var t = await OpenAsync();
        var foodId = await FoodLibrary.AddAsync(t.Db, Household, Granola(), clock);

        var food = (await FoodLibrary.GetAsync(t.Db, foodId))!.ToFood()!;
        // 130 × 100 / 30, 3 × 100 / 30, 20 × 100 / 30 (NutrientAmounts.Per100); fat unknown, not 0.
        Assert.Equal(130 * 100 / 30.0, food.Per100[Nutrient.Energy]);
        Assert.Equal(3 * 100 / 30.0, food.Per100[Nutrient.Protein]);
        Assert.Null(food.Per100[Nutrient.Fat]);
        Assert.Equal(("Granola", "Oat Co", "custom"), (food.Name, food.Brand, food.Source));
        Assert.Equal(["1 serving", "1 cup"], food.Portions.Select(p => p.Name));

        var grams = await FoodLog.LogFoodAsync(t.Db, Profile, foodId, "breakfast", Day, 45, "g", null, clock);
        var cup = await FoodLog.LogFoodAsync(t.Db, Profile, foodId, "snack", Day, 0.5, null, "1 cup", clock);
        var ounce = await FoodLog.LogFoodAsync(t.Db, Profile, foodId, "snack", Day, 1, "oz", null, clock);

        await t.ReopenAsync();
        var day = await FoodLog.DayAsync(t.Db, Profile, Day);
        Assert.Equal([grams, cup, ounce], day.Select(e => e.Id));
        var first = day[0];
        Assert.Equal(("breakfast", "search", foodId, "Granola", "Oat Co"), (first.Meal, first.EntryMethod, first.FoodId, first.Name, first.Brand));
        Assert.Equal((45.0, "g", (string?)null), (first.Quantity!.Value, first.QuantityUnit, first.PortionName));
        // Per-100 value × 45 / 100, in that order (NutrientAmounts.ForAmount).
        Assert.Equal(130 * 100 / 30.0 * 45 / 100, first.Energy);
        Assert.Null(first.Fat);
        Assert.Equal(("portion", "1 cup", 0.5 * 110 * (130 * 100 / 30.0) / 100), (day[1].QuantityUnit, day[1].PortionName, day[1].Energy!.Value));
        // An ounce is exactly 28.349523125 g.
        Assert.Equal(130 * 100 / 30.0 * 28.349523125 / 100, day[2].Energy);
    }

    [Fact]
    public async Task Entries_upload_with_their_JSON_and_boolean_columns_and_deletes_upload_too()
    {
        await using var t = await OpenAsync();
        var foodId = await FoodLibrary.AddAsync(t.Db, Household, Granola(), clock);
        var entry = await FoodLog.LogFoodAsync(t.Db, Profile, foodId, "lunch", Day, 60, "g", null, clock);
        var writer = new FakeWriter();
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);

        var food = Assert.Single(writer.Writes, w => w.Table == "foods");
        Assert.Equal(("PUT", foodId, Household, "custom", "g"),
            (food.Op, food.Id, food.Data!["household_id"]!.GetValue<string>(), food.Data["source"]!.GetValue<string>(), food.Data["basis"]!.GetValue<string>()));
        Assert.Equal(30, food.Data["portions"]![0]!["amount"]!.GetValue<double>());
        Assert.False(food.Data["verified"]!.GetValue<bool>());
        var logged = Assert.Single(writer.Writes, w => w.Table == "food_log_entries");
        Assert.Equal(("PUT", entry, Profile, "2026-11-07", 60.0, "g"),
            (logged.Op, logged.Id, logged.Data!["profile_id"]!.GetValue<string>(), logged.Data["local_date"]!.GetValue<string>(),
                logged.Data["base_amount"]!.GetValue<double>(), logged.Data["basis"]!.GetValue<string>()));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, logged.Data["nutrients"]!.GetValueKind());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, logged.Data["incomplete_nutrients"]!.GetValueKind());
        Assert.False(logged.Data["is_estimate"]!.GetValue<bool>());

        writer.Writes.Clear();
        await FoodLog.DeleteAsync(t.Db, Profile, entry);
        await Uploads.UploadPendingAsync(t.Db, writer, clock, cancellationToken: Ct);
        Assert.Equal(("DELETE", "food_log_entries", entry), (writer.Writes.Single().Op, writer.Writes.Single().Table, writer.Writes.Single().Id));
        Assert.Empty(await FoodLog.DayAsync(t.Db, Profile, Day));
    }

    [Fact]
    public async Task Amounts_that_do_not_fit_the_food_are_refused_and_nothing_is_logged()
    {
        await using var t = await OpenAsync();
        var foodId = await FoodLibrary.AddAsync(t.Db, Household, Granola(), clock);

        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.LogFoodAsync(t.Db, Profile, foodId, "lunch", Day, 1, "cup", null, clock));
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.LogFoodAsync(t.Db, Profile, foodId, "lunch", Day, 1, null, "1 slice", clock));
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.LogFoodAsync(t.Db, Profile, foodId, "lunch", Day, 0, "g", null, clock));
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.LogFoodAsync(t.Db, Profile, foodId, "brunch", Day, 10, "g", null, clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FoodLog.LogFoodAsync(t.Db, Profile, "missing", "lunch", Day, 10, "g", null, clock));
        Assert.Empty(await FoodLog.DayAsync(t.Db, Profile, Day));
    }

    [Fact]
    public async Task A_drink_listed_per_100_ml_takes_volumes()
    {
        await using var t = await OpenAsync();
        var milk = await FoodLibrary.AddAsync(t.Db, Household,
            new FoodInput("Milk", null, FoodBasis.Volume, 100, 64, 3.4, 4.8, 3.6, []), clock);

        await FoodLog.LogFoodAsync(t.Db, Profile, milk, "breakfast", Day, 1, "cup", null, clock);
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.LogFoodAsync(t.Db, Profile, milk, "breakfast", Day, 100, "g", null, clock));

        var entry = Assert.Single(await FoodLog.DayAsync(t.Db, Profile, Day));
        // A US cup is 8 × 29.5735295625 ml.
        Assert.Equal(64 * (8 * 29.5735295625) / 100, entry.Energy);
        Assert.Equal(["ml", "l", "fl_oz", "cup", "tbsp", "tsp"], FoodLog.UnitsFor(FoodBasis.Volume));
    }

    [Fact]
    public async Task The_day_total_is_a_lower_bound_where_an_entry_left_a_nutrient_unknown()
    {
        await using var t = await OpenAsync();
        var foodId = await FoodLibrary.AddAsync(t.Db, Household, Granola(), clock);
        await FoodLog.LogFoodAsync(t.Db, Profile, foodId, "breakfast", Day, 30, "g", null, clock);
        await FoodLog.QuickAddAsync(t.Db, Profile, "dinner", Day, "  ", 600, null, null, 25, clock);

        var entries = await FoodLog.DayAsync(t.Db, Profile, Day);
        var total = FoodLog.Total(entries);

        Assert.Equal(("Quick add", "quick_add", (double?)null), (entries[1].Name, entries[1].EntryMethod, entries[1].Quantity));
        Assert.Equal(130 * 100 / 30.0 * 30 / 100 + 600, total.Amounts[Nutrient.Energy]);
        Assert.Equal(25, total.Amounts[Nutrient.Fat]);
        // Granola didn't list fat; the quick add gave neither protein nor carbohydrate.
        Assert.Equal(["carbohydrate", "fat", "protein"], total.Incomplete.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_quick_add_needs_a_value_and_no_negatives()
    {
        await using var t = await OpenAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.QuickAddAsync(t.Db, Profile, "snack", Day, null, null, null, null, null, clock));
        await Assert.ThrowsAsync<ArgumentException>(() => FoodLog.QuickAddAsync(t.Db, Profile, "snack", Day, null, -5, null, null, null, clock));
        Assert.Empty(await FoodLog.DayAsync(t.Db, Profile, Day));
    }

    [Fact]
    public async Task A_day_shows_only_its_own_entries_for_the_profile_and_an_earlier_day_is_logged_at_its_local_noon()
    {
        await using var t = await OpenAsync();
        var eastern = new ZonedClock(new DateTimeOffset(2026, 11, 7, 18, 30, 0, TimeSpan.Zero), TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var yesterday = Day.AddDays(-1);
        await FoodLog.QuickAddAsync(t.Db, Profile, "lunch", yesterday, "Sandwich", 450, null, null, null, eastern);
        await FoodLog.QuickAddAsync(t.Db, Other, "lunch", yesterday, "Theirs", 300, null, null, null, eastern);

        var entry = Assert.Single(await FoodLog.DayAsync(t.Db, Profile, yesterday));
        Assert.Equal(("Sandwich", "2026-11-06T17:00:00.000Z"), (entry.Name, entry.LoggedAt));
        Assert.Empty(await FoodLog.DayAsync(t.Db, Profile, Day));
    }

    [Fact]
    public async Task Search_finds_household_foods_by_name_or_brand_and_recent_lists_what_was_logged_last()
    {
        await using var t = await OpenAsync();
        var granola = await FoodLibrary.AddAsync(t.Db, Household, Granola(), clock);
        var oats = await FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Rolled oats", null, FoodBasis.Mass, 100, 379, 13.2, 67.7, 6.5, []), clock);
        await FoodLibrary.AddAsync(t.Db, Household, new FoodInput("100%_juice", null, FoodBasis.Volume, 100, 45, null, 10, null, []), clock);

        Assert.Equal(["Granola", "Rolled oats"], (await FoodLibrary.SearchAsync(t.Db, "oat", 10)).Select(f => f.Name));
        Assert.Equal(["100%_juice"], (await FoodLibrary.SearchAsync(t.Db, "0%_", 10)).Select(f => f.Name));
        Assert.Equal(3, (await FoodLibrary.SearchAsync(t.Db, "", 10)).Length);

        await FoodLog.LogFoodAsync(t.Db, Profile, oats, "breakfast", Day, 40, "g", null, clock);
        await FoodLog.LogFoodAsync(t.Db, Profile, granola, "snack", Day, 30, "g", null, clock);
        Assert.Equal([granola, oats], (await FoodLibrary.RecentAsync(t.Db, Profile, 5)).Select(f => f.Id));
        Assert.Empty(await FoodLibrary.RecentAsync(t.Db, Other, 5));
    }

    [Fact]
    public async Task A_food_the_table_would_refuse_is_not_written()
    {
        await using var t = await OpenAsync();

        await Assert.ThrowsAsync<ValidationException>(() =>
            FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Butter", null, FoodBasis.Mass, 10, 72, null, null, 12, []), clock));
        await Assert.ThrowsAsync<ValidationException>(() =>
            FoodLibrary.AddAsync(t.Db, Household, new FoodInput("Nothing", null, FoodBasis.Mass, 100, null, null, null, null, []), clock));
        await Assert.ThrowsAsync<ValidationException>(() =>
            FoodLibrary.AddAsync(t.Db, Household, Granola() with { Portions = [new("1 cup", 110), new("1 cup ", 120)] }, clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FoodLibrary.AddAsync(t.Db, "elsewhere", Granola(), clock));
        Assert.Empty(await FoodLibrary.SearchAsync(t.Db, "", 10));
    }

    [Fact]
    public void A_malformed_synced_food_reads_without_its_bad_values()
    {
        var row = new FoodRow
        {
            Id = "f",
            Name = "Synced",
            Basis = "g",
            Energy100 = 100,
            Nutrients100 = "{\"iron\": 2, \"Bad\": 1, \"zinc\": -1, \"protein\": 9}",
            Portions = "[{\"name\": \"1 slice\", \"amount\": 25}, {\"name\": \"\", \"amount\": 5}, {\"name\": \"1 slice\", \"amount\": 30}, 7]",
        };

        var food = row.ToFood()!;
        Assert.Equal(new Dictionary<string, double> { ["energy"] = 100, ["iron"] = 2 }, food.Per100.Values);
        Assert.Equal([new FoodPortion("1 slice", 25)], food.Portions);
        Assert.Null(new FoodRow { Basis = "oz" }.ToFood());
        Assert.Empty(new FoodRow { Basis = "g", Nutrients100 = "not json", Portions = "{" }.ToFood()!.Per100.Values);
    }

    private sealed class ZonedClock(DateTimeOffset start, TimeZoneInfo zone, TimeSpan step = default) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow()
        {
            var current = now;
            now += step;
            return current;
        }

        public override TimeZoneInfo LocalTimeZone => zone;
    }
}

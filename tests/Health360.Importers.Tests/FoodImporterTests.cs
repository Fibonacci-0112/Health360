using Npgsql;

namespace Health360.Importers.Tests;

/// <summary>
/// The food import against a migrated database from <c>HEALTH360_TEST_DATABASE</c>, inside a transaction that is
/// rolled back; skipped without it.
/// </summary>
[Trait("Category", "Database")]
public sealed class FoodImporterTests
{
    private const string EnvironmentVariable = "HEALTH360_TEST_DATABASE";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Importing_again_creates_no_duplicates_and_leaves_unchanged_rows_alone()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");
        var parsed = FoodDataCentralTests.ParseText(FoodDataCentralTests.SrLegacySample);
        var foods = parsed.Foods;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);

        var first = await FoodImporter.ImportAsync(tx, parsed.Dataset, foods, "2018-04", Ct);
        var rows = await RowVersions(tx, foods);
        var second = await FoodImporter.ImportAsync(tx, parsed.Dataset, foods, "2018-04", Ct);
        var corrected = foods.Select((f, i) => i == 1 ? f with { Protein100 = 0 } : f).ToList();
        var third = await FoodImporter.ImportAsync(tx, parsed.Dataset, corrected, "2018-05", Ct);

        // Assumes the sample's ids aren't in the database yet (a disposable test database).
        Assert.Equal(new ImportResult(foods.Count, 0, 0), first);
        Assert.Equal(new ImportResult(0, 0, foods.Count), second);
        Assert.Equal(new ImportResult(0, 1, foods.Count - 1), third);
        // An update writes a new row version, so an unchanged ctid means the row wasn't touched.
        var after = await RowVersions(tx, foods);
        Assert.Equal(rows[0], after[0]);
        Assert.NotEqual(rows[1], after[1]);

        await using (var check = new NpgsqlCommand(
            """
            select household_id is null, source, basis, name, energy_100, protein_100, carbohydrate_100, fat_100,
                   nutrients_100::text, portions::text, verified
            from public.foods where source = 'usda_fdc' and external_id = '167512'
            """, connection, tx))
        await using (var reader = await check.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct));
            Assert.True(reader.GetBoolean(0));
            Assert.Equal(("usda_fdc", "g", "Pillsbury Golden Layer Biscuits, refrigerated dough"), (reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            Assert.Equal((307d, 5.88, 41.2, 13.2), (reader.GetDouble(4), reader.GetDouble(5), reader.GetDouble(6), reader.GetDouble(7)));
            Assert.Equal("""{"folate": 85, "sodium": 1010, "sugars": 0, "vitamin_d": 0.1}""", reader.GetString(8));
            Assert.Equal(
                """[{"name": "1 serving", "amount": 34}, {"name": "1 oz", "amount": 28.35}, {"name": "0.5 cup, chopped", "amount": 80}]""",
                reader.GetString(9));
            Assert.False(reader.GetBoolean(10));
        }

        // Unknown stays null: salt lists no fat.
        await using (var salt = new NpgsqlCommand(
            "select fat_100 is null, protein_100 from public.foods where source = 'usda_fdc' and external_id = '171705'", connection, tx))
        await using (var reader = await salt.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal((true, 0d), (reader.GetBoolean(0), reader.GetDouble(1)));
        }

        await using var dataset = new NpgsqlCommand(
            "select version, attribution from public.reference_datasets where code = 'usda-fdc-sr-legacy'", connection, tx);
        await using (var reader = await dataset.ExecuteReaderAsync(Ct))
        {
            Assert.True(await reader.ReadAsync(Ct));
            Assert.Equal(("2018-05", FoodDataCentral.Attribution), (reader.GetString(0), reader.GetString(1)));
        }

        await tx.RollbackAsync(Ct);
    }

    [Fact]
    public async Task Imports_more_foods_than_one_batch()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");
        var template = FoodDataCentralTests.ParseText(FoodDataCentralTests.SrLegacySample).Foods[1];
        var foods = Enumerable.Range(0, 1203).Select(i => template with { ExternalId = $"test-{i}", Name = $"Food {i}" }).ToList();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);

        var result = await FoodImporter.ImportAsync(tx, FoodDataCentral.Survey, foods, "test", Ct);

        Assert.Equal(new ImportResult(foods.Count, 0, 0), result);
        await using var count = new NpgsqlCommand(
            "select count(*) from public.foods where source = 'usda_fdc' and external_id like 'test-%'", connection, tx);
        Assert.Equal((long)foods.Count, await count.ExecuteScalarAsync(Ct));
        await tx.RollbackAsync(Ct);
    }

    private static async Task<List<string>> RowVersions(NpgsqlTransaction tx, IReadOnlyList<ImportedFood> foods)
    {
        var versions = new List<string>();
        foreach (var food in foods)
        {
            await using var command = new NpgsqlCommand(
                "select ctid::text from public.foods where source = 'usda_fdc' and external_id = @id", tx.Connection, tx);
            command.Parameters.AddWithValue("id", food.ExternalId);
            versions.Add((string)(await command.ExecuteScalarAsync(Ct))!);
        }

        return versions;
    }
}

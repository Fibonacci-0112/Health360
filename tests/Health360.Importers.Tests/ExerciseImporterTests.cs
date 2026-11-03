using Npgsql;

namespace Health360.Importers.Tests;

/// <summary>
/// The import against a migrated database from <c>HEALTH360_TEST_DATABASE</c>, inside a transaction that is rolled
/// back; skipped without it.
/// </summary>
[Trait("Category", "Database")]
public sealed class ExerciseImporterTests
{
    private const string EnvironmentVariable = "HEALTH360_TEST_DATABASE";

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Importing_again_creates_no_duplicates_and_leaves_unchanged_rows_alone()
    {
        Assert.SkipUnless(ConnectionString is { Length: > 0 }, $"Set {EnvironmentVariable} to run the database tests.");
        var exercises = FreeExerciseDbTests.ParseText(FreeExerciseDbTests.Sample).Exercises;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Ct);
        await using var tx = await connection.BeginTransactionAsync(Ct);
        await Execute(tx, "delete from public.reference_datasets where code = 'free-exercise-db'");

        var first = await ExerciseImporter.ImportAsync(tx, exercises, "v1", Ct);
        var rows = await RowVersions(tx, exercises);
        var second = await ExerciseImporter.ImportAsync(tx, exercises, "v2", Ct);
        var renamed = exercises.Select((e, i) => i == 0 ? e with { Name = "Back Squat" } : e).ToList();
        var third = await ExerciseImporter.ImportAsync(tx, renamed, "v3", Ct);

        // Assumes the sample's ids aren't in the database yet (a disposable test database).
        Assert.Equal(new ImportResult(exercises.Count, 0, 0), first);
        Assert.Equal(new ImportResult(0, 0, exercises.Count), second);
        Assert.Equal(new ImportResult(0, 1, exercises.Count - 1), third);
        // An update writes a new row version, so an unchanged ctid means the row wasn't touched (updated_at is
        // the transaction's start time here, so it can't tell).
        var after = await RowVersions(tx, exercises);
        Assert.NotEqual(rows[0], after[0]);
        Assert.Equal(rows.Skip(1), after.Skip(1));
        await using var check = new NpgsqlCommand(
            "select count(*), count(*) filter (where household_id is null), max(name) filter (where external_id = 'Barbell_Squat') " +
            "from public.exercises where external_id = any(@ids)", tx.Connection, tx);
        check.Parameters.AddWithValue("ids", exercises.Select(e => e.ExternalId).ToArray());
        await using (var reader = await check.ExecuteReaderAsync(Ct))
        {
            await reader.ReadAsync(Ct);
            Assert.Equal((exercises.Count, exercises.Count, "Back Squat"), ((int)reader.GetInt64(0), (int)reader.GetInt64(1), reader.GetString(2)));
        }

        await using var dataset = new NpgsqlCommand("select version from public.reference_datasets where code = 'free-exercise-db'", tx.Connection, tx);
        Assert.Equal("v3", await dataset.ExecuteScalarAsync(Ct));
        await tx.RollbackAsync(Ct);
    }

    /// <summary>Each exercise's physical row version, in the order given.</summary>
    private static async Task<List<string>> RowVersions(NpgsqlTransaction tx, IReadOnlyList<ImportedExercise> exercises)
    {
        var versions = new List<string>();
        foreach (var exercise in exercises)
        {
            await using var command = new NpgsqlCommand("select ctid::text from public.exercises where external_id = @id", tx.Connection, tx);
            command.Parameters.AddWithValue("id", exercise.ExternalId);
            versions.Add((string)(await command.ExecuteScalarAsync(Ct))!);
        }

        return versions;
    }

    private static async Task Execute(NpgsqlTransaction tx, string sql)
    {
        await using var command = new NpgsqlCommand(sql, tx.Connection, tx);
        await command.ExecuteNonQueryAsync(Ct);
    }
}

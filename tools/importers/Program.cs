using System.Text.Json;
using Health360.Importers;
using Npgsql;

// Imports reference data into a migrated database. For now, the exercise library from free-exercise-db:
//
//   curl -fsSLo exercises.json https://raw.githubusercontent.com/yuhonas/free-exercise-db/<commit>/dist/exercises.json
//   HEALTH360_IMPORT_DATABASE='Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres' \
//     dotnet run --project tools/importers -- exercises exercises.json <commit>
//
// The connection needs a role that may write system rows (postgres or service_role). Re-running is safe.

const string Variable = "HEALTH360_IMPORT_DATABASE";

if (args is not ["exercises", var path, var version] || string.IsNullOrWhiteSpace(version))
{
    Console.Error.WriteLine("Usage: exercises <path to free-exercise-db exercises.json> <dataset version, e.g. the commit>");
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable(Variable);
if (string.IsNullOrEmpty(connectionString))
{
    Console.Error.WriteLine($"Set {Variable} to an Npgsql connection string for a migrated database.");
    return 2;
}

ParsedDataset dataset;
try
{
    await using var file = File.OpenRead(path);
    dataset = FreeExerciseDb.Parse(file);
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"Could not read {path}: {e.Message}");
    return 2;
}

foreach (var skipped in dataset.Skipped)
{
    Console.Error.WriteLine($"Skipped entry {skipped.Index} ({skipped.ExternalId ?? "no id"}): {skipped.Reason}");
}

if (dataset.Exercises.Count == 0)
{
    Console.Error.WriteLine("No exercises to import.");
    return 1;
}

try
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var tx = await connection.BeginTransactionAsync();
    var result = await ExerciseImporter.ImportAsync(tx, dataset.Exercises, version);
    await tx.CommitAsync();
    Console.WriteLine(
        $"{FreeExerciseDb.DatasetCode} {version}: {result.Inserted} added, {result.Updated} updated, {result.Unchanged} unchanged, " +
        $"{dataset.Skipped.Count} skipped");
    return 0;
}
catch (NpgsqlException e)
{
    // The message only; the connection string (and its password) is never printed.
    Console.Error.WriteLine($"Import failed: {e.Message}");
    return 1;
}
catch (ArgumentException)
{
    // A malformed connection string. Npgsql's message can quote part of it, so it isn't printed.
    Console.Error.WriteLine($"{Variable} is not a valid Npgsql connection string.");
    return 2;
}

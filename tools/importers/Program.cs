using System.Text.Json;
using Health360.Importers;
using Npgsql;

// Imports reference data into a migrated database.
//
// The exercise library from free-exercise-db:
//
//   curl -fsSLo exercises.json https://raw.githubusercontent.com/yuhonas/free-exercise-db/<commit>/dist/exercises.json
//   HEALTH360_IMPORT_DATABASE='Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres' \
//     dotnet run --project tools/importers -- exercises exercises.json <commit>
//
// USDA FoodData Central foods, one JSON download at a time (Foundation Foods, SR Legacy, FNDDS survey foods), from
// https://fdc.nal.usda.gov/download-datasets, for example:
//
//   curl -fsSLO https://fdc.nal.usda.gov/fdc-datasets/FoodData_Central_sr_legacy_food_json_2018-04.zip
//   unzip FoodData_Central_sr_legacy_food_json_2018-04.zip
//   HEALTH360_IMPORT_DATABASE='...' \
//     dotnet run --project tools/importers -- foods FoodData_Central_sr_legacy_food_json_2018-04.json 2018-04
//
// The connection needs a role that may write system rows (postgres or service_role). Re-running is safe.

const string Variable = "HEALTH360_IMPORT_DATABASE";
const string Usage =
    """
    Usage:
      exercises <path to free-exercise-db exercises.json> <dataset version, e.g. the commit>
      foods <path to a FoodData Central JSON download> <dataset version, e.g. the release date>
    """;

if (args is not [var kind and ("exercises" or "foods"), var path, var version] || string.IsNullOrWhiteSpace(version))
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var connectionString = Environment.GetEnvironmentVariable(Variable);
if (string.IsNullOrEmpty(connectionString))
{
    Console.Error.WriteLine($"Set {Variable} to an Npgsql connection string for a migrated database.");
    return 2;
}

ParsedDataset? exercises = null;
ParsedFoods? foods = null;
try
{
    await using var file = File.OpenRead(path);
    if (kind == "exercises")
    {
        exercises = FreeExerciseDb.Parse(file);
    }
    else
    {
        foods = FoodDataCentral.Parse(file);
    }
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
{
    Console.Error.WriteLine($"Could not read {path}: {e.Message}");
    return 2;
}

var skipped = exercises?.Skipped ?? foods!.Skipped;
foreach (var entry in skipped)
{
    Console.Error.WriteLine($"Skipped entry {entry.Index} ({entry.ExternalId ?? "no id"}): {entry.Reason}");
}

if ((exercises?.Exercises.Count ?? foods!.Foods.Count) == 0)
{
    Console.Error.WriteLine($"No {kind} to import.");
    return 1;
}

try
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var tx = await connection.BeginTransactionAsync();
    string summary;
    if (exercises is not null)
    {
        var result = await ExerciseImporter.ImportAsync(tx, exercises.Exercises, version);
        summary = $"{FreeExerciseDb.DatasetCode} {version}: {Counts(result)}, {skipped.Count} skipped";
    }
    else
    {
        var result = await FoodImporter.ImportAsync(tx, foods!.Dataset, foods.Foods, version);
        summary = $"{foods.Dataset.Code} {version}: {Counts(result)}, {skipped.Count} skipped, {foods.PortionsDropped} portions dropped";
    }

    await tx.CommitAsync();
    Console.WriteLine(summary);
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

static string Counts(ImportResult result) => $"{result.Inserted} added, {result.Updated} updated, {result.Unchanged} unchanged";

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Health360.Importers;

/// <summary>What an import changed.</summary>
public sealed record ImportResult(int Inserted, int Updated, int Unchanged);

/// <summary>
/// Writes system exercises (<c>household_id</c> null) in the caller's transaction: inserts new ids, updates changed ones and
/// leaves identical rows untouched, so their <c>updated_at</c> stays put and sync has nothing to send. Exercises gone
/// from the dataset are kept: templates and logged sets may use them. Records the dataset's version in
/// <c>reference_datasets</c>.
/// </summary>
public static class ExerciseImporter
{
    private const string UpsertSql =
        """
        insert into public.exercises (external_id, name, tracking_type, equipment, primary_muscles, secondary_muscles)
        values (@external_id, @name, @tracking_type, @equipment, @primary_muscles, @secondary_muscles)
        on conflict (external_id) do update set
          name = excluded.name,
          tracking_type = excluded.tracking_type,
          equipment = excluded.equipment,
          primary_muscles = excluded.primary_muscles,
          secondary_muscles = excluded.secondary_muscles
        where (exercises.name, exercises.tracking_type, exercises.equipment, exercises.primary_muscles, exercises.secondary_muscles)
          is distinct from
          (excluded.name, excluded.tracking_type, excluded.equipment, excluded.primary_muscles, excluded.secondary_muscles)
        returning (xmax = 0) as inserted
        """;

    private const string DatasetSql =
        """
        insert into public.reference_datasets (code, version, attribution, imported_at)
        values (@code, @version, @attribution, now())
        on conflict (code) do update set version = excluded.version, attribution = excluded.attribution, imported_at = now()
        """;

    public static async Task<ImportResult> ImportAsync(
        NpgsqlTransaction tx,
        IReadOnlyList<ImportedExercise> exercises,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(exercises);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var connection = tx.Connection ?? throw new ArgumentException("The transaction has ended.", nameof(tx));
        int inserted = 0, updated = 0;
        foreach (var exercise in exercises)
        {
            await using var command = new NpgsqlCommand(UpsertSql, connection, tx);
            command.Parameters.AddWithValue("external_id", exercise.ExternalId);
            command.Parameters.AddWithValue("name", exercise.Name);
            command.Parameters.AddWithValue("tracking_type", exercise.TrackingType);
            command.Parameters.AddWithValue("equipment", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(exercise.Equipment));
            command.Parameters.AddWithValue("primary_muscles", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(exercise.PrimaryMuscles));
            command.Parameters.AddWithValue("secondary_muscles", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(exercise.SecondaryMuscles));
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is true)
            {
                inserted++;
            }
            else if (result is false)
            {
                updated++;
            }
        }

        await using (var dataset = new NpgsqlCommand(DatasetSql, connection, tx))
        {
            dataset.Parameters.AddWithValue("code", FreeExerciseDb.DatasetCode);
            dataset.Parameters.AddWithValue("version", version);
            dataset.Parameters.AddWithValue("attribution", FreeExerciseDb.Attribution);
            await dataset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ImportResult(inserted, updated, exercises.Count - inserted - updated);
    }
}

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Health360.Importers;

/// <summary>
/// Writes USDA FoodData Central catalogue foods (<c>household_id</c> null, source <c>usda_fdc</c>) in the caller's
/// transaction, keyed by <c>fdcId</c>: inserts new ones, updates changed ones and leaves identical rows untouched.
/// Foods gone from the dataset are kept: households' copies and log entries record where their values came from.
/// Records the dataset's version in <c>reference_datasets</c>. Statements go in batches, so a hosted database's round
/// trips don't dominate a 13,000-food import.
/// </summary>
public static class FoodImporter
{
    private const int BatchSize = 500;

    private const string UpsertSql =
        """
        insert into public.foods (source, external_id, name, basis, energy_100, protein_100, carbohydrate_100, fat_100, nutrients_100, portions)
        values ('usda_fdc', @external_id, @name, 'g', @energy_100, @protein_100, @carbohydrate_100, @fat_100, @nutrients_100, @portions)
        on conflict (source, external_id) do update set
          name = excluded.name,
          energy_100 = excluded.energy_100,
          protein_100 = excluded.protein_100,
          carbohydrate_100 = excluded.carbohydrate_100,
          fat_100 = excluded.fat_100,
          nutrients_100 = excluded.nutrients_100,
          portions = excluded.portions
        where (foods.name, foods.energy_100, foods.protein_100, foods.carbohydrate_100, foods.fat_100, foods.nutrients_100, foods.portions)
          is distinct from
          (excluded.name, excluded.energy_100, excluded.protein_100, excluded.carbohydrate_100, excluded.fat_100, excluded.nutrients_100, excluded.portions)
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
        FoodDataCentral.Dataset dataset,
        IReadOnlyList<ImportedFood> foods,
        string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(foods);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var connection = tx.Connection ?? throw new ArgumentException("The transaction has ended.", nameof(tx));
        int inserted = 0, updated = 0;
        foreach (var chunk in foods.Chunk(BatchSize))
        {
            await using var batch = new NpgsqlBatch(connection, tx);
            foreach (var food in chunk)
            {
                var command = new NpgsqlBatchCommand(UpsertSql);
                command.Parameters.AddWithValue("external_id", food.ExternalId);
                command.Parameters.AddWithValue("name", food.Name);
                AddNumber(command, "energy_100", food.Energy100);
                AddNumber(command, "protein_100", food.Protein100);
                AddNumber(command, "carbohydrate_100", food.Carbohydrate100);
                AddNumber(command, "fat_100", food.Fat100);
                command.Parameters.AddWithValue("nutrients_100", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(food.Nutrients100));
                command.Parameters.AddWithValue(
                    "portions", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(food.Portions.Select(p => new { name = p.Name, amount = p.Grams })));
                batch.BatchCommands.Add(command);
            }

            await using var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            do
            {
                // An unchanged food returns no row.
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (reader.GetBoolean(0))
                    {
                        inserted++;
                    }
                    else
                    {
                        updated++;
                    }
                }
            }
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        }

        await using (var command = new NpgsqlCommand(DatasetSql, connection, tx))
        {
            command.Parameters.AddWithValue("code", dataset.Code);
            command.Parameters.AddWithValue("version", version);
            command.Parameters.AddWithValue("attribution", FoodDataCentral.Attribution);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ImportResult(inserted, updated, foods.Count - inserted - updated);
    }

    private static void AddNumber(NpgsqlBatchCommand command, string name, double? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Double) { Value = value is { } v ? v : DBNull.Value });
}

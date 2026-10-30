using Health360.Drift;
using Npgsql;

// Fails when the PowerSync client schema (Health360.LocalDb's AppSchema) and Postgres drift apart.
// Run against a database with the migrations applied (`supabase db start` in CI):
//
//   HEALTH360_DRIFT_DATABASE='Host=127.0.0.1;Port=54322;Username=postgres;Password=postgres;Database=postgres' \
//     dotnet run --project tools/drift

const string Variable = "HEALTH360_DRIFT_DATABASE";

var connectionString = Environment.GetEnvironmentVariable(Variable);
if (string.IsNullOrEmpty(connectionString))
{
    Console.Error.WriteLine($"Set {Variable} to an Npgsql connection string for a migrated database.");
    return 2;
}

var published = new List<ServerColumn>();
try
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        """
        select c.table_name, c.column_name, c.data_type
        from information_schema.columns c
        join pg_publication_tables p on p.schemaname = c.table_schema and p.tablename = c.table_name
        where p.pubname = 'powersync' and c.table_schema = 'public'
        order by c.table_name, c.ordinal_position
        """,
        connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        published.Add(new ServerColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
    }
}
catch (NpgsqlException e)
{
    // The message only; the connection string (and its password) is never printed.
    Console.Error.WriteLine($"Could not read the schema: {e.Message}");
    return 2;
}

if (published.Count == 0)
{
    // An empty publication would otherwise report every client table as unpublished; say why instead.
    Console.Error.WriteLine("The powersync publication has no tables. Are the migrations applied?");
    return 1;
}

var problems = SchemaDrift.Check(published);
if (problems.Count > 0)
{
    Console.Error.WriteLine($"Schema drift between Postgres and AppSchema:\n  {string.Join("\n  ", problems)}");
    return 1;
}

Console.WriteLine($"AppSchema matches Postgres ({published.Select(c => c.Table).Distinct().Count()} synced tables)");
return 0;

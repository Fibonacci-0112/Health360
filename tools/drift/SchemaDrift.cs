using Health360.LocalDb;
using PowerSync.Common.DB.Schema;

namespace Health360.Drift;

/// <summary>One column of a table in the <c>powersync</c> publication, as information_schema reports it.</summary>
public sealed record ServerColumn(string Table, string Column, string DataType);

/// <summary>
/// Compares the PowerSync client schema with the Postgres tables published to PowerSync. Checks, for
/// every published table:
/// <list type="bullet">
/// <item>the client has the table, with the same columns (PowerSync supplies <c>id</c> itself);</item>
/// <item>each column's SQLite type matches its Postgres type;</item>
/// <item>every jsonb and boolean column is in <see cref="AppSchema.ServerColumnTypes"/>, so uploads
/// convert it back, and no other column (or <c>id</c>) is.</item>
/// </list>
/// And that every synced client table, and every uploadable table, is published.
/// </summary>
public static class SchemaDrift
{
    /// <summary>How PowerSync stores each Postgres type the migrations use.</summary>
    private static readonly Dictionary<string, ColumnType> SqliteType = new(StringComparer.Ordinal)
    {
        ["text"] = ColumnType.Text,
        ["uuid"] = ColumnType.Text,
        ["date"] = ColumnType.Text,
        ["timestamp with time zone"] = ColumnType.Text,
        ["timestamp without time zone"] = ColumnType.Text,
        ["jsonb"] = ColumnType.Text,
        ["json"] = ColumnType.Text,
        ["boolean"] = ColumnType.Integer,
        ["integer"] = ColumnType.Integer,
        ["bigint"] = ColumnType.Integer,
        ["smallint"] = ColumnType.Integer,
        ["double precision"] = ColumnType.Real,
        ["real"] = ColumnType.Real,
        ["numeric"] = ColumnType.Real,
    };

    /// <summary>Checks <see cref="AppSchema"/> against the published columns.</summary>
    public static IReadOnlyList<string> Check(IEnumerable<ServerColumn> published) =>
        Check(published, AppSchema.Tables, AppSchema.ServerColumnTypes, AppSchema.UploadableTables);

    public static IReadOnlyList<string> Check(
        IEnumerable<ServerColumn> published,
        IEnumerable<Table> clientTables,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ServerColumnType>> serverColumnTypes,
        IReadOnlySet<string> uploadableTables)
    {
        var server = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var c in published)
        {
            if (!server.TryGetValue(c.Table, out var columns))
            {
                server[c.Table] = columns = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            columns[c.Column] = c.DataType;
        }

        var client = clientTables.Where(t => !t.LocalOnly).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var (table, columns) in server)
        {
            if (!client.TryGetValue(table, out var mine))
            {
                problems.Add($"{table}: published but missing from AppSchema");
                continue;
            }

            if (!columns.ContainsKey("id"))
            {
                problems.Add($"{table}: has no id column, which PowerSync needs");
            }

            var codecs = serverColumnTypes.GetValueOrDefault(table);
            foreach (var (column, type) in columns)
            {
                if (column == "id")
                {
                    continue;
                }

                var known = SqliteType.TryGetValue(type, out var expected);
                if (!known)
                {
                    problems.Add($"{table}.{column}: no SQLite mapping for Postgres type {type}");
                }

                if (!mine.Columns.TryGetValue(column, out var actual))
                {
                    problems.Add($"{table}.{column}: missing from the client table");
                }
                else if (known && actual != expected)
                {
                    problems.Add($"{table}.{column}: client type {actual}, expected {expected} for {type}");
                }

                ServerColumnType? codec = codecs is not null && codecs.TryGetValue(column, out var c) ? c : null;
                ServerColumnType? needed = type switch
                {
                    "jsonb" or "json" => ServerColumnType.Json,
                    "boolean" => ServerColumnType.Boolean,
                    _ => null,
                };
                if (codec != needed)
                {
                    problems.Add(needed is null
                        ? $"{table}.{column}: {type} is listed as {codec} in ServerColumnTypes"
                        : $"{table}.{column}: {type} must be listed as {needed} in ServerColumnTypes");
                }
            }

            foreach (var column in mine.Columns.Keys.Where(c => !columns.ContainsKey(c)))
            {
                problems.Add($"{table}.{column}: on the client but not published by Postgres");
            }

            // Uploads apply every listed codec, so an entry for a column that isn't synced is stale.
            foreach (var column in codecs?.Keys.Where(c => c == "id" || !columns.ContainsKey(c)) ?? [])
            {
                problems.Add($"{table}.{column}: in ServerColumnTypes but not a synced column");
            }
        }

        foreach (var table in client.Keys.Where(t => !server.ContainsKey(t)))
        {
            problems.Add($"{table}: client table is not in the powersync publication");
        }

        foreach (var table in serverColumnTypes.Keys.Where(t => !client.ContainsKey(t)))
        {
            problems.Add($"{table}: in ServerColumnTypes but not a synced client table");
        }

        foreach (var table in uploadableTables.Where(t => !server.ContainsKey(t)))
        {
            problems.Add($"{table}: uploadable but not in the powersync publication");
        }

        return problems;
    }
}

using Health360.LocalDb;
using PowerSync.Common.DB.Schema;

namespace Health360.Drift.Tests;

/// <summary>
/// The comparison only; CI runs the tool against the migrated database. Each test starts from a
/// published table that matches and changes one thing.
/// </summary>
public class SchemaDriftTests
{
    private static readonly Table Client = new(
        "things",
        new Dictionary<string, ColumnType>
        {
            ["profile_id"] = ColumnType.Text,
            ["amount"] = ColumnType.Real,
            ["count"] = ColumnType.Integer,
            ["details"] = ColumnType.Text,
            ["done"] = ColumnType.Integer,
        },
        new TableOptions());

    private static readonly Table LocalOnly = new(
        AppSchema.FailedUploads, new Dictionary<string, ColumnType> { ["data"] = ColumnType.Text }, new TableOptions(localOnly: true));

    private static readonly Dictionary<string, IReadOnlyDictionary<string, ServerColumnType>> Codecs = new()
    {
        ["things"] = new Dictionary<string, ServerColumnType> { ["details"] = ServerColumnType.Json, ["done"] = ServerColumnType.Boolean },
    };

    private static List<ServerColumn> Published() =>
    [
        new("things", "id", "uuid"),
        new("things", "profile_id", "uuid"),
        new("things", "amount", "double precision"),
        new("things", "count", "bigint"),
        new("things", "details", "jsonb"),
        new("things", "done", "boolean"),
    ];

    private static IReadOnlyList<string> Check(
        IEnumerable<ServerColumn> published,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ServerColumnType>>? codecs = null,
        params string[] uploadable) =>
        SchemaDrift.Check(published, [Client, LocalOnly], codecs ?? Codecs, new HashSet<string>(uploadable.Length > 0 ? uploadable : ["things"]));

    [Fact]
    public void Matching_schemas_have_no_problems()
    {
        Assert.Empty(Check(Published()));
    }

    [Fact]
    public void A_published_table_missing_from_the_client()
    {
        var problems = Check([.. Published(), new("other", "id", "uuid")]);

        Assert.Equal(["other: published but missing from AppSchema"], problems);
    }

    [Fact]
    public void A_client_table_that_is_not_published()
    {
        var problems = Check([new("other", "id", "uuid")], uploadable: "other");

        Assert.Contains("things: client table is not in the powersync publication", problems);
    }

    [Fact]
    public void A_local_only_table_needs_no_server_table()
    {
        Assert.DoesNotContain(Check(Published()), p => p.Contains(AppSchema.FailedUploads, StringComparison.Ordinal));
    }

    [Fact]
    public void A_server_column_missing_from_the_client()
    {
        var problems = Check([.. Published(), new("things", "note", "text")]);

        Assert.Equal(["things.note: missing from the client table"], problems);
    }

    [Fact]
    public void A_client_column_missing_from_the_server()
    {
        var problems = Check(Published().Where(c => c.Column != "amount"));

        Assert.Equal(["things.amount: on the client but not in Postgres"], problems);
    }

    [Fact]
    public void A_column_stored_as_the_wrong_type()
    {
        var problems = Check(Published().Select(c => c.Column == "amount" ? c with { DataType = "text" } : c));

        Assert.Equal(["things.amount: client type Real, expected Text for text"], problems);
    }

    [Fact]
    public void An_unmapped_postgres_type()
    {
        var problems = Check(Published().Select(c => c.Column == "amount" ? c with { DataType = "interval" } : c));

        Assert.Equal(["things.amount: no SQLite mapping for Postgres type interval"], problems);
    }

    [Fact]
    public void Json_and_boolean_columns_need_a_codec()
    {
        var problems = Check(Published(), new Dictionary<string, IReadOnlyDictionary<string, ServerColumnType>>());

        Assert.Equal(
            ["things.details: jsonb must be listed as Json in ServerColumnTypes", "things.done: boolean must be listed as Boolean in ServerColumnTypes"],
            problems);
    }

    [Fact]
    public void A_codec_on_a_column_postgres_stores_plainly()
    {
        var codecs = new Dictionary<string, IReadOnlyDictionary<string, ServerColumnType>>
        {
            ["things"] = new Dictionary<string, ServerColumnType>(Codecs["things"]) { ["count"] = ServerColumnType.Boolean },
        };

        Assert.Equal(["things.count: bigint is listed as Boolean in ServerColumnTypes"], Check(Published(), codecs));
    }

    [Fact]
    public void A_codec_for_a_table_the_client_does_not_sync()
    {
        var codecs = new Dictionary<string, IReadOnlyDictionary<string, ServerColumnType>>(Codecs)
        {
            ["gone"] = new Dictionary<string, ServerColumnType> { ["x"] = ServerColumnType.Json },
        };

        Assert.Equal(["gone: in ServerColumnTypes but not a synced client table"], Check(Published(), codecs));
    }

    [Fact]
    public void An_uploadable_table_that_is_not_published()
    {
        Assert.Equal(["gone: uploadable but not in the powersync publication"], Check(Published(), uploadable: ["things", "gone"]));
    }

    [Fact]
    public void A_published_table_without_an_id()
    {
        var problems = Check(Published().Where(c => c.Column != "id"));

        Assert.Equal(["things: has no id column, which PowerSync needs"], problems);
    }
}

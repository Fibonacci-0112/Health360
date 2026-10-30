using PowerSync.Common.DB.Schema;

namespace Health360.LocalDb;

/// <summary>
/// The PowerSync client schema. Client tables mirror the synced Postgres tables column for column
/// (PowerSync stores everything as text, integer or real; jsonb arrives as JSON text and booleans as
/// 0/1). Server-only columns that clients never read can be left out. PowerSync adds the <c>id</c>
/// column itself. Keep this identical to <c>packages/local-db/src/schema.ts</c> while the prototype
/// exists.
/// </summary>
public static class AppSchema
{
    public const string FailedUploads = "failed_uploads";

    private static readonly (string Name, ColumnType Type)[] Timestamps =
    [
        ("created_at", ColumnType.Text),
        ("updated_at", ColumnType.Text),
    ];

    public static Table Profiles { get; } = Synced("profiles",
        ("user_id", ColumnType.Text),
        ("managed_by", ColumnType.Text),
        ("display_name", ColumnType.Text),
        ("birth_date", ColumnType.Text),
        ("sex_for_calcs", ColumnType.Text),
        ("height", ColumnType.Real),
        ("height_unit", ColumnType.Text),
        ("height_cm", ColumnType.Real),
        ("unit_prefs", ColumnType.Text),
        ("timezone", ColumnType.Text),
        ("currency", ColumnType.Text),
        ("activity_level", ColumnType.Text),
        ("physiology_prefs", ColumnType.Text),
        ("onboarded_at", ColumnType.Text));

    public static Table Households { get; } = Synced("households",
        ("name", ColumnType.Text));

    public static Table HouseholdMembers { get; } = Synced("household_members",
        ("household_id", ColumnType.Text),
        ("profile_id", ColumnType.Text),
        ("user_id", ColumnType.Text),
        ("role", ColumnType.Text));

    public static Table SharingGrants { get; } = Synced("sharing_grants",
        ("profile_id", ColumnType.Text),
        ("household_id", ColumnType.Text),
        ("grantee_profile_id", ColumnType.Text),
        ("category", ColumnType.Text),
        ("access", ColumnType.Text));

    public static Table Consents { get; } = Synced("consents",
        ("profile_id", ColumnType.Text),
        ("kind", ColumnType.Text),
        ("version", ColumnType.Text),
        ("granted", ColumnType.Integer),
        ("decided_at", ColumnType.Text));

    public static Table ReferenceDatasets { get; } = Synced("reference_datasets",
        ("code", ColumnType.Text),
        ("version", ColumnType.Text),
        ("attribution", ColumnType.Text),
        ("imported_at", ColumnType.Text));

    /// <summary>Phase 0 throwaway table that proves sync on every target.</summary>
    public static Table Notes { get; } = Synced("notes",
        ("profile_id", ColumnType.Text),
        ("body", ColumnType.Text),
        ("local_date", ColumnType.Text));

    /// <summary>
    /// Local only, never uploaded. When the server permanently rejects a write (constraint or RLS
    /// violation), the connector parks it here instead of blocking the upload queue (PLAN.md §4).
    /// <c>data</c> is the CRUD payload as JSON, so the user can retry or discard it.
    /// </summary>
    public static Table FailedUploadsTable { get; } = new(
        FailedUploads,
        Columns(
            ("table_name", ColumnType.Text),
            ("row_id", ColumnType.Text),
            ("op", ColumnType.Text),
            ("data", ColumnType.Text),
            ("error_code", ColumnType.Text),
            ("error_message", ColumnType.Text),
            ("failed_at", ColumnType.Text)),
        new TableOptions(localOnly: true));

    public static IReadOnlyList<Table> Tables { get; } =
        [Profiles, Households, HouseholdMembers, SharingGrants, Consents, ReferenceDatasets, Notes, FailedUploadsTable];

    public static Schema Schema { get; } = new([.. Tables]);

    /// <summary>
    /// Columns stored differently in SQLite than in Postgres: jsonb is JSON text locally and boolean is
    /// 0/1. Uploads convert them back so the server receives real JSON values and booleans. Every jsonb
    /// and boolean column of a synced table must be listed.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, ServerColumnType>> ServerColumnTypes { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, ServerColumnType>>(StringComparer.Ordinal)
        {
            ["profiles"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["unit_prefs"] = ServerColumnType.Json,
                ["physiology_prefs"] = ServerColumnType.Json,
            },
            ["consents"] = new Dictionary<string, ServerColumnType>(StringComparer.Ordinal)
            {
                ["granted"] = ServerColumnType.Boolean,
            },
        };

    /// <summary>
    /// Tables whose rows are a record: clients may add rows but never change them (no UPDATE grant), so
    /// uploads insert and ignore duplicates instead of upserting, which would need UPDATE.
    /// </summary>
    public static IReadOnlySet<string> InsertOnlyTables { get; } = new HashSet<string>(["consents"], StringComparer.Ordinal);

    /// <summary>Tables the client may write. Everything else synced is read-only on the client.</summary>
    public static IReadOnlySet<string> UploadableTables { get; } =
        new HashSet<string>(["profiles", "consents", "sharing_grants", "notes"], StringComparer.Ordinal);

    private static Table Synced(string name, params (string Name, ColumnType Type)[] columns) =>
        new(name, Columns([.. columns, .. Timestamps]), new TableOptions());

    private static Dictionary<string, ColumnType> Columns(params (string Name, ColumnType Type)[] columns) =>
        columns.ToDictionary(c => c.Name, c => c.Type, StringComparer.Ordinal);
}

/// <summary>How a column's local SQLite value maps back to its Postgres type on upload.</summary>
public enum ServerColumnType
{
    Json,
    Boolean,
}

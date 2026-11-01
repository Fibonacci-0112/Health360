using Health360.LocalDb;
using PowerSync.Common.Client;

namespace Health360.UI.Tests.Support;

/// <summary>
/// A real PowerSync database (the SDK's native core and SQLite) in a temporary file, deleted afterwards.
/// Nothing connects it to a server. The same helper as in Health360.LocalDb.Tests.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string directory;

    private TestDatabase(string directory, PowerSyncDatabase db)
    {
        this.directory = directory;
        Db = db;
    }

    public PowerSyncDatabase Db { get; }

    public static async Task<TestDatabase> OpenAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "health360-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = new PowerSyncDatabase(new PowerSyncDatabaseOptions
        {
            Database = new SQLOpenOptions { DbFilename = Path.Combine(directory, "test.db") },
            Schema = AppSchema.Schema,
        });
        await db.Init();
        return new TestDatabase(directory, db);
    }

    /// <summary>
    /// A profile row as the server's sign-up trigger creates it and sync brings it down: inserted, then
    /// taken off the upload queue, since a synced row was never a local write.
    /// </summary>
    public Task SeedProfileAsync(string profileId, string userId, string? unitPrefs = "{}", string? onboardedAt = null, string displayName = "") =>
        Db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "INSERT INTO profiles (id, user_id, display_name, unit_prefs, onboarded_at, timezone, currency) VALUES (?, ?, ?, ?, ?, 'UTC', 'USD')",
                [profileId, userId, displayName, unitPrefs, onboardedAt]);
            await tx.Execute("DELETE FROM ps_crud WHERE id = (SELECT max(id) FROM ps_crud)");
        });

    /// <summary>A progress photo row as sync brings it down from another device: no local files, nothing queued.</summary>
    public Task SeedPhotoAsync(string id, string profileId, string pose, string localDate) =>
        Db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "INSERT INTO progress_photos (id, profile_id, pose, taken_at, local_date, storage_path, thumb_path, created_at, updated_at) " +
                "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                [id, profileId, pose, localDate + "T12:00:00.000Z", localDate, $"{profileId}/{id}.jpg", $"{profileId}/{id}_thumb.jpg",
                 localDate + "T12:00:00.000Z", localDate + "T12:00:00.000Z"]);
            await tx.Execute("DELETE FROM ps_crud");
        });

    /// <summary>
    /// Metric definitions as the reference stream brings them down, copied from the migration
    /// (20261031000001_body_measurements.sql) for the metrics the screens' tests use.
    /// </summary>
    public Task SeedMetricsAsync() =>
        Db.WriteTransaction(async tx =>
        {
            (string Code, string Category, string Units, string Canonical, double? Min, double? Max)[] metrics =
            [
                ("weight", "body", "[\"kg\", \"lb\"]", "kg", 0, null),
                ("body_fat_pct", "body", "[\"%\"]", "%", 0, 100),
                ("circ_neck", "body", "[\"cm\", \"in\"]", "cm", 0, null),
                ("circ_waist_navel", "body", "[\"cm\", \"in\"]", "cm", 0, null),
                ("circ_waist_narrowest", "body", "[\"cm\", \"in\"]", "cm", 0, null),
                ("circ_hip", "body", "[\"cm\", \"in\"]", "cm", 0, null),
                ("bp_systolic", "body", "[\"mmHg\"]", "mmHg", 0, null),
                ("bp_diastolic", "body", "[\"mmHg\"]", "mmHg", 0, null),
                ("rmr", "physiology", "[\"kcal\", \"kJ\"]", "kcal", 0, null),
                ("max_hr", "physiology", "[\"bpm\"]", "bpm", 0, null),
                ("resting_hr", "physiology", "[\"bpm\"]", "bpm", 0, null),
                ("lthr", "physiology", "[\"bpm\"]", "bpm", 0, null),
                ("vo2max", "physiology", "[\"ml/kg/min\"]", "ml/kg/min", 0, null),
                ("threshold_pace", "physiology", "[\"s_per_km\", \"s_per_mi\"]", "s_per_km", 0, null),
            ];
            foreach (var (code, category, units, canonical, min, max) in metrics)
            {
                await tx.Execute(
                    "INSERT INTO metric_definitions (id, code, category, units, canonical_unit, valid_min, valid_max) VALUES (uuid(), ?, ?, ?, ?, ?, ?)",
                    [code, category, units, canonical, min, max]);
            }

            await tx.Execute("DELETE FROM ps_crud");
        });

    /// <summary>A measurement as sync brings it down from another device (not queued for upload here).</summary>
    public Task SeedMeasurementAsync(string id, string profileId, string metric, double value, string unit, double canonical,
        string localDate, string source = "manual", string? groupId = null) =>
        Db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "INSERT INTO measurements (id, profile_id, metric_code, category, value, unit, value_canonical, measured_at, local_date, group_id, source) " +
                "VALUES (?, ?, ?, 'body', ?, ?, ?, ?, ?, ?, ?)",
                [id, profileId, metric, value, unit, canonical, localDate + "T12:00:00.000Z", localDate, groupId, source]);
            await tx.Execute("DELETE FROM ps_crud");
        });

    /// <summary>Profile details from onboarding, as synced.</summary>
    public Task SeedDetailsAsync(string profileId, string sex, string birthDate, double heightCm, string activityLevel) =>
        Db.WriteTransaction(async tx =>
        {
            await tx.Execute(
                "UPDATE profiles SET sex_for_calcs = ?, birth_date = ?, height = ?, height_unit = 'cm', height_cm = ?, activity_level = ? WHERE id = ?",
                [sex, birthDate, heightCm, heightCm, activityLevel, profileId]);
            await tx.Execute("DELETE FROM ps_crud");
        });

    public async ValueTask DisposeAsync()
    {
        await Db.Close();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A file the native library still holds; the OS cleans the temp directory.
        }
    }
}

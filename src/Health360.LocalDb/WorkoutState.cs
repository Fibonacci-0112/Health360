using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>Local-only owner-bound state. Callers serialize with generated JSON metadata.</summary>
public static class WorkoutState
{
    public static async Task<string?> ReadAsync(IPowerSyncDatabase db, string profileId, string key) =>
        (await db.GetOptional<ValueRow>("SELECT value AS Value FROM workout_state WHERE id = ? AND profile_id = ?",
            [profileId + "/" + key, profileId]).ConfigureAwait(false))?.Value;

    public static Task WriteAsync(IPowerSyncDatabase db, string profileId, string key, string value) =>
        db.Execute("INSERT OR REPLACE INTO workout_state (id, profile_id, value) VALUES (?, ?, ?)", [profileId + "/" + key, profileId, value]);

    public static Task DeleteAsync(IPowerSyncDatabase db, string profileId, string key) =>
        db.Execute("DELETE FROM workout_state WHERE id = ? AND profile_id = ?", [profileId + "/" + key, profileId]);

    public sealed class ValueRow
    {
        public string? Value { get; set; }
    }
}

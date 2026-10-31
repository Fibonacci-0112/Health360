using Health360.Contracts.Profiles;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>The columns of a profile the app's shell and onboarding read.</summary>
public sealed class ProfileRow
{
    public string Id { get; set; } = "";

    public string? DisplayName { get; set; }

    /// <summary>JSON text, as PowerSync stores jsonb; read it with <see cref="UnitPrefsJson.Parse"/>.</summary>
    public string? UnitPrefs { get; set; }

    public string? OnboardedAt { get; set; }

    public double? Height { get; set; }

    public string? HeightUnit { get; set; }

    /// <summary>In cm, as the server recomputes it from <see cref="Height"/> and <see cref="HeightUnit"/>.</summary>
    public double? HeightCm { get; set; }

    /// <summary><c>YYYY-MM-DD</c>, or null when not given.</summary>
    public string? BirthDate { get; set; }

    /// <summary>A <c>SexForCalcs</c> code, or null when not given.</summary>
    public string? SexForCalcs { get; set; }

    /// <summary>An activity level code, or null when not given.</summary>
    public string? ActivityLevel { get; set; }

    /// <summary>
    /// The heart-rate-lowering medication flag, as SQLite stores a boolean (0 or 1). Nothing is derived from
    /// age-predicted max HR when it is set (PLAN.md §2.5).
    /// </summary>
    public long TakesHrLoweringMedication { get; set; }
}

/// <summary>
/// The signed-in user's own profile in the local database. Writes go to the local database and upload
/// when online, like every other write; the server creates the row at sign-up and sync brings it down.
/// </summary>
public static class Profiles
{
    private const string MineSql =
        "SELECT id AS Id, display_name AS DisplayName, unit_prefs AS UnitPrefs, onboarded_at AS OnboardedAt, " +
        "height AS Height, height_unit AS HeightUnit, height_cm AS HeightCm, birth_date AS BirthDate, " +
        "sex_for_calcs AS SexForCalcs, activity_level AS ActivityLevel, takes_hr_lowering_medication AS TakesHrLoweringMedication " +
        "FROM profiles WHERE user_id = ?";

    /// <summary>The user's profile now and after every change (an empty array until it has synced).</summary>
    public static IAsyncEnumerable<ProfileRow[]> WatchMine(PowerSyncDatabase db, string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<ProfileRow>(MineSql, [userId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<ProfileRow?> GetMineAsync(IPowerSyncDatabase db, string userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetOptional<ProfileRow>(MineSql, [userId]).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves onboarding and marks the profile onboarded. The form must already have passed
    /// <see cref="OnboardingFormValidator"/>. Throws if the profile isn't in the local database, so a save
    /// that changed nothing is never shown as done.
    /// </summary>
    public static Task CompleteOnboardingAsync(IPowerSyncDatabase db, string profileId, OnboardingForm form, double? heightCm, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(clock);
        return UpdateAsync(
            db,
            profileId,
            """
            UPDATE profiles SET display_name = ?, birth_date = ?, sex_for_calcs = ?, height = ?, height_unit = ?,
              height_cm = ?, timezone = ?, currency = ?, activity_level = ?, unit_prefs = ?, onboarded_at = ?
            WHERE id = ?
            """,
            [
                form.DisplayName.Trim(),
                form.BirthDate,
                form.SexForCalcs,
                form.Height,
                form.HeightUnit,
                heightCm,
                form.Timezone,
                form.Currency,
                form.ActivityLevel,
                UnitPrefsJson.Serialize(form.UnitPrefs),
                Timestamps.UtcNow(clock),
                profileId,
            ]);
    }

    /// <summary>
    /// Saves the details edited in Settings. The form must already have passed <see cref="ProfileDetailsFormValidator"/>.
    /// Only columns whose value changes go into the upload, so this can't undo another device's change to a
    /// different setting.
    /// </summary>
    public static Task SetDetailsAsync(IPowerSyncDatabase db, string profileId, ProfileDetailsForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return UpdateAsync(
            db,
            profileId,
            """
            UPDATE profiles SET birth_date = ?, sex_for_calcs = ?, height = ?, height_unit = ?, height_cm = ?, activity_level = ?
            WHERE id = ?
            """,
            [form.BirthDate, form.SexForCalcs, form.Height, form.HeightUnit, form.HeightCm, form.ActivityLevel, profileId]);
    }

    public static Task SetUnitPrefsAsync(IPowerSyncDatabase db, string profileId, Domain.Units.UnitPrefs prefs) =>
        UpdateAsync(db, profileId, "UPDATE profiles SET unit_prefs = ? WHERE id = ?", [UnitPrefsJson.Serialize(prefs), profileId]);

    /// <summary>
    /// Sets the heart-rate-lowering medication flag. It is a column of its own, so the upload carries only this
    /// column and can't undo another device's change to a different setting.
    /// </summary>
    public static Task SetHeartRateLoweringMedicationAsync(IPowerSyncDatabase db, string profileId, bool value) =>
        UpdateAsync(db, profileId, "UPDATE profiles SET takes_hr_lowering_medication = ? WHERE id = ?", [value ? 1 : 0, profileId]);

    // PowerSync tables are views with INSTEAD OF triggers, whose row counts SQLite reports as 0, so the
    // row is checked for in the same transaction instead.
    private static async Task UpdateAsync(IPowerSyncDatabase db, string profileId, string sql, object?[] parameters)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        await db.WriteTransaction(async tx =>
        {
            var found = await tx.GetOptional<IdRow>("SELECT id AS Id FROM profiles WHERE id = ?", [profileId]).ConfigureAwait(false);
            if (found is null)
            {
                throw new InvalidOperationException("The profile is not in the local database.");
            }

            await tx.Execute(sql, parameters).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    internal sealed class IdRow
    {
        public string Id { get; set; } = "";
    }
}

/// <summary>A write the server rejected, as Settings lists it. The row data stays in the table, out of the UI.</summary>
public sealed class FailedUpload
{
    public string Id { get; set; } = "";

    public string TableName { get; set; } = "";

    public string Op { get; set; } = "";

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public string? FailedAt { get; set; }
}

public static class FailedUploads
{
    private const string ListSql =
        $"SELECT id AS Id, table_name AS TableName, op AS Op, error_code AS ErrorCode, error_message AS ErrorMessage, " +
        $"failed_at AS FailedAt FROM {AppSchema.FailedUploads} ORDER BY failed_at DESC";

    /// <summary>The quarantine, newest first, now and after every change.</summary>
    public static IAsyncEnumerable<FailedUpload[]> Watch(PowerSyncDatabase db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<FailedUpload>(ListSql, [], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<FailedUpload[]> ListAsync(IPowerSyncDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<FailedUpload>(ListSql, []).ConfigureAwait(false);
    }
}

using System.Security.Cryptography;
using FluentValidation;
using Health360.Contracts.Notes;
using Health360.Domain.Ids;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>A note as the sync test screen lists it.</summary>
public sealed class NoteRow
{
    public string Id { get; set; } = "";

    public string? Body { get; set; }

    public string? LocalDate { get; set; }

    public string? CreatedAt { get; set; }
}

/// <summary>
/// The Phase 0b throwaway <c>notes</c> table (PLAN.md §3): a note written offline on one device should
/// reach the user's other devices once both are online. Don't build features on it.
/// </summary>
public static class Notes
{
    private static readonly NoteInputValidator Validator = new();

    // Every note this device has synced, not only the signed-in profile's: the sync test must show
    // anything the streams deliver, so a row that shouldn't be here is visible rather than filtered out.
    private const string ListSql =
        "SELECT id AS Id, body AS Body, local_date AS LocalDate, created_at AS CreatedAt FROM notes ORDER BY created_at DESC, id DESC";

    /// <summary>The notes, newest first, now and after every change.</summary>
    public static IAsyncEnumerable<NoteRow[]> Watch(PowerSyncDatabase db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<NoteRow>(ListSql, [], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<NoteRow[]> ListAsync(IPowerSyncDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<NoteRow>(ListSql, []).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a note for the profile to the local database; it uploads when online. The id is a UUIDv7
    /// made on the device, so a retried upload can't create a second row.
    /// </summary>
    /// <returns>The new note's id.</returns>
    /// <exception cref="ValidationException">The note is empty or too long; nothing is written.</exception>
    public static async Task<string> AddAsync(IPowerSyncDatabase db, string profileId, NoteInput note, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(clock);
        Validator.ValidateAndThrow(note);
        var id = UuidV7.Create(clock, RandomNumberGenerator.Fill).ToString();
        var now = Timestamps.UtcNow(clock);
        await db.Execute(
            "INSERT INTO notes (id, profile_id, body, local_date, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)",
            [id, profileId, note.Body.Trim(), Timestamps.LocalDate(clock), now, now]).ConfigureAwait(false);
        return id;
    }
}

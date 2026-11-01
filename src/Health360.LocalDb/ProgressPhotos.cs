using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>Where a progress photo's files are: on the server, waiting on this device, or refused by the server.</summary>
public enum PhotoUploadState
{
    /// <summary>No upload is waiting on this device: the files reached Storage (or another device holds them).</summary>
    Uploaded,

    /// <summary>The files are on this device and go up when there is a connection.</summary>
    Pending,

    /// <summary>The server refused the files for good; the user can retry or discard the photo.</summary>
    Failed,
}

/// <summary>One <c>progress_photos</c> row with the state of its files, as the Body tab lists them.</summary>
public sealed class ProgressPhotoRow
{
    public string Id { get; set; } = "";

    public string Pose { get; set; } = "";

    public string TakenAt { get; set; } = "";

    public string LocalDate { get; set; } = "";

    public long Queued { get; set; }

    public string? FailedAt { get; set; }

    public string? ErrorCode { get; set; }

    public PhotoUploadState State =>
        Queued == 0 ? PhotoUploadState.Uploaded : FailedAt is null ? PhotoUploadState.Pending : PhotoUploadState.Failed;
}

/// <summary>The profile's progress photos from the local database, newest first.</summary>
public static class ProgressPhotos
{
    // photo_uploads is local only, so the join reports this device's pending and refused files.
    private const string MineSql =
        "SELECT p.id AS Id, p.pose AS Pose, p.taken_at AS TakenAt, p.local_date AS LocalDate, " +
        "CASE WHEN u.id IS NULL THEN 0 ELSE 1 END AS Queued, u.failed_at AS FailedAt, u.error_code AS ErrorCode " +
        "FROM progress_photos p LEFT JOIN " + PhotoUploader.Table + " u ON u.id = p.id " +
        "WHERE p.profile_id = ? ORDER BY p.taken_at DESC, p.id DESC";

    /// <summary>The profile's photos, now and after every change to them or their uploads.</summary>
    public static IAsyncEnumerable<ProgressPhotoRow[]> Watch(PowerSyncDatabase db, string profileId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.Watch<ProgressPhotoRow>(MineSql, [profileId], new SQLWatchOptions { Signal = cancellationToken, TriggerImmediately = true });
    }

    public static async Task<ProgressPhotoRow[]> ListAsync(IPowerSyncDatabase db, string profileId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.GetAll<ProgressPhotoRow>(MineSql, [profileId]).ConfigureAwait(false);
    }
}

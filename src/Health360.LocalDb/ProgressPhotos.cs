using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>
/// Whether a progress photo has reached the server. It has two parts that upload separately: its row (through the
/// normal upload queue) and its files (through <see cref="PhotoUploader"/>). Both must have arrived for Uploaded.
/// </summary>
public enum PhotoUploadState
{
    /// <summary>Nothing for it is waiting on this device: its row and files reached the server (or another device's did).</summary>
    Uploaded,

    /// <summary>Its row, its files or both are still on this device and go up when there is a connection.</summary>
    Pending,

    /// <summary>The server refused its files or its row for good; the user can retry or discard the photo.</summary>
    Failed,
}

/// <summary>One <c>progress_photos</c> row with the state of its files, as the Body tab lists them.</summary>
public sealed class ProgressPhotoRow
{
    public string Id { get; set; } = "";

    public string Pose { get; set; } = "";

    public string TakenAt { get; set; } = "";

    public string LocalDate { get; set; } = "";

    /// <summary>1 while its files have an entry in this device's upload queue.</summary>
    public long Queued { get; set; }

    /// <summary>When the server refused its files, if it did.</summary>
    public string? FailedAt { get; set; }

    /// <summary>Why the server refused its files, if it did.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>1 while a write of its row is waiting in the normal upload queue.</summary>
    public long RowPending { get; set; }

    /// <summary>
    /// The code the server refused its row with, if an insert or update of it is quarantined in <c>failed_uploads</c>.
    /// A refused delete isn't counted here: it's retried from Settings, not by discarding the photo.
    /// </summary>
    public string? RowErrorCode { get; set; }

    /// <summary>1 when a write of its row is quarantined; the code may be missing.</summary>
    public long RowRejected { get; set; }

    /// <summary>True when the files were refused (retried here); a refused row is retried from Settings.</summary>
    public bool FilesFailed => Queued != 0 && FailedAt is not null;

    public PhotoUploadState State =>
        FilesFailed || RowRejected != 0 ? PhotoUploadState.Failed
        : Queued != 0 || RowPending != 0 ? PhotoUploadState.Pending
        : PhotoUploadState.Uploaded;
}

/// <summary>The profile's progress photos from the local database, newest first.</summary>
public static class ProgressPhotos
{
    // photo_uploads and failed_uploads are local only and ps_crud is PowerSync's upload queue, so this reports
    // what of each photo is still on this device or was refused.
    private const string MineSql =
        "SELECT p.id AS Id, p.pose AS Pose, p.taken_at AS TakenAt, p.local_date AS LocalDate, " +
        "CASE WHEN u.id IS NULL THEN 0 ELSE 1 END AS Queued, u.failed_at AS FailedAt, u.error_code AS ErrorCode, " +
        "EXISTS (SELECT 1 FROM ps_crud c WHERE json_extract(c.data, '$.type') = 'progress_photos' " +
        "AND json_extract(c.data, '$.id') = p.id) AS RowPending, " +
        "EXISTS (SELECT 1 FROM " + AppSchema.FailedUploads + " f WHERE f.table_name = 'progress_photos' AND f.row_id = p.id AND f.op <> 'DELETE') AS RowRejected, " +
        "(SELECT f.error_code FROM " + AppSchema.FailedUploads + " f WHERE f.table_name = 'progress_photos' AND f.row_id = p.id AND f.op <> 'DELETE' " +
        "ORDER BY f.failed_at DESC LIMIT 1) AS RowErrorCode " +
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

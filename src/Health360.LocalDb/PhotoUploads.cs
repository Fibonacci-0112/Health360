using System.Globalization;
using System.Security.Cryptography;
using Health360.Domain.Ids;
using Health360.Domain.Photos;
using PowerSync.Common.Client;

namespace Health360.LocalDb;

/// <summary>A photo whose files the server refused for good, shown to the user to retry or cancel.</summary>
public sealed class FailedPhotoUpload
{
    public string Id { get; set; } = "";

    public string? ErrorCode { get; set; }

    public string? FailedAt { get; set; }
}

/// <summary>What one pass of <see cref="PhotoUploader.ProcessAsync"/> did.</summary>
public sealed record PhotoUploadReport(int Uploaded, int Failed, bool Stopped);

/// <summary>
/// The offline upload queue for progress photos (PLAN.md §3, step 1.1). Adding a photo strips its metadata on the
/// device, keeps the stripped files in the app's private storage, and writes the <c>progress_photos</c> row and a
/// local-only <c>photo_uploads</c> entry in one transaction. The row goes up through the normal upload queue; the
/// files go up from here when there is a connection, oldest first. A file may reach Storage
/// before its row: the bucket's policies depend only on the profile folder.
/// <para>
/// The queue belongs to the local database's owner like everything else in it: the entries are cleared with the
/// database (<see cref="LocalDatabase"/> deletes the files too), and files are only sent with the owner's token.
/// A refusal that a retry can't fix is marked failed instead of blocking later photos; a missing connection
/// stops the pass and leaves everything for the next one.
/// </para>
/// </summary>
public sealed class PhotoUploader(PowerSyncDatabase db, IPhotoFileStore files, IPhotoStorage storage, TimeProvider clock) : IDisposable
{
    public const string Table = "photo_uploads";

    /// <summary>How often the background loop tries again when nothing wakes it.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>The poses the server accepts (<c>progress_photos.pose</c>).</summary>
    public static readonly IReadOnlySet<string> Poses = new HashSet<string>(["front", "back", "side_left", "side_right", "other"], StringComparer.Ordinal);

    private readonly SemaphoreSlim pass = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource stop = new();

    // Cancels the pass in progress when the account's data is cleared (guarded by pass).
    private CancellationTokenSource? running;
    private int started;
    private int disposed;

    // Changes each time a clear begins, so an add that was waiting for the lock across a clear is refused.
    private int generation;

    /// <summary>Raised after a pass changed the queue, possibly off the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Raised when a clear of the account's data begins, before it waits for work in progress.</summary>
    public event Action? Clearing;

    /// <summary>
    /// Changes each time the account's data is cleared. A screen reads it before opening the camera or library and
    /// passes it to <see cref="AddAsync"/>, so a photo picked across a sign-out or account switch is refused.
    /// </summary>
    public int ClearGeneration => Volatile.Read(ref generation);

    public static string PhotoName(string id) => id + ".jpg";

    public static string ThumbName(string id) => id + "_thumb.jpg";

    /// <summary>
    /// Adds a photo taken (or picked) on this device and returns its id. Both images must be JPEGs already resized and
    /// rotated upright by the platform (the EXIF orientation is removed with the rest of the metadata, so a sideways
    /// original would stay sideways). Throws <see cref="ArgumentException"/> for anything else, before storing
    /// anything, and <see cref="InvalidOperationException"/> if the account's data was cleared while it waited, or
    /// since <paramref name="clearGeneration"/> (<see cref="ClearGeneration"/> read before the photo was taken).
    /// </summary>
    public async Task<string> AddAsync(
        string profileId,
        string pose,
        DateTimeOffset takenAt,
        DateOnly localDate,
        byte[] photo,
        byte[] thumbnail,
        int? clearGeneration = null,
        CancellationToken cancellationToken = default)
    {
        // Read before anything else (stripping takes a while), so a clear that begins at any point during this add
        // is seen when it takes the lock.
        var before = clearGeneration ?? Volatile.Read(ref generation);
        ArgumentException.ThrowIfNullOrEmpty(profileId);
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(thumbnail);
        if (!Guid.TryParse(profileId, out var profile) || profile.ToString() != profileId)
        {
            throw new ArgumentException("The profile id must be a lower-case UUID.", nameof(profileId));
        }

        if (!Poses.Contains(pose))
        {
            throw new ArgumentException("Unknown pose.", nameof(pose));
        }

        var strippedPhoto = Strip(photo, nameof(photo));
        var strippedThumb = Strip(thumbnail, nameof(thumbnail));

        var id = UuidV7.Create(clock, RandomNumberGenerator.Fill).ToString();
        var now = Timestamps.UtcNow(clock);
        // Held from the first file to the commit, so clearing the account's data can't land in between and leave an
        // entry without its files (or this account's row in the next account's database).
        await pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The account was cleared (signed out or switched) while this waited: the photo belongs to whoever
            // was signed in before, so it must not land in the next account's database.
            if (Volatile.Read(ref generation) != before)
            {
                throw new InvalidOperationException("The account changed before the photo was saved.");
            }

            // The profile must still be in this device's database. A clear empties it, so a profile id the screen read
            // before a sign-out or account switch (even one that finished before the camera opened) is refused here,
            // under the same lock the clear takes. The screen only offers photos once the profile has synced, so this
            // costs nothing offline.
            if (await db.GetOptional<CountRow>("SELECT 1 AS Count FROM profiles WHERE id = ?", [profileId]).ConfigureAwait(false) is null)
            {
                throw new InvalidOperationException("The profile isn't in this device's database.");
            }

            await files.WriteAsync(PhotoName(id), strippedPhoto, cancellationToken).ConfigureAwait(false);
            await files.WriteAsync(ThumbName(id), strippedThumb, cancellationToken).ConfigureAwait(false);
            await db.WriteTransaction(async tx =>
            {
                await tx.Execute(
                    "INSERT INTO progress_photos (id, profile_id, pose, taken_at, local_date, storage_path, thumb_path, created_at, updated_at) " +
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    [
                        id, profileId, pose, takenAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                        localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), $"{profileId}/{PhotoName(id)}",
                        $"{profileId}/{ThumbName(id)}", now, now,
                    ]).ConfigureAwait(false);
                await tx.Execute(
                    $"INSERT INTO {Table} (id, profile_id, attempts, created_at) VALUES (?, ?, 0, ?)",
                    [id, profileId, now]).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch
        {
            DeleteFiles(id);
            throw;
        }
        finally
        {
            pass.Release();
        }

        Wake();
        return id;
    }

    /// <summary>
    /// Uploads waiting photos, oldest first, until the queue is empty or the connection fails. Each photo's thumbnail
    /// then full image go up; once both are stored the entry and the local files are removed.
    /// </summary>
    public async Task<PhotoUploadReport> ProcessAsync(CancellationToken cancellationToken = default)
    {
        var before = Volatile.Read(ref generation);
        await pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var current = Register(before, cancellationToken);
        cancellationToken = current.Token;
        try
        {
            var uploaded = 0;
            var failed = 0;
            var pending = await db.GetAll<PendingRow>(
                $"SELECT id AS Id, profile_id AS ProfileId FROM {Table} WHERE failed_at IS NULL ORDER BY created_at, id", []).ConfigureAwait(false);
            foreach (var row in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await UploadAsync(row, cancellationToken).ConfigureAwait(false);
                }
                catch (PhotoUploadException error) when (error.Permanent)
                {
                    await db.Execute(
                        $"UPDATE {Table} SET attempts = attempts + 1, error_code = ?, failed_at = ? WHERE id = ?",
                        [error.Code, Timestamps.UtcNow(clock), row.Id]).ConfigureAwait(false);
                    failed++;
                    continue;
                }
                catch (PhotoUploadException error)
                {
                    // Offline, signed out or a server error: everything after this would fail the same way.
                    await db.Execute(
                        $"UPDATE {Table} SET attempts = attempts + 1, error_code = ? WHERE id = ?", [error.Code, row.Id]).ConfigureAwait(false);
                    Notify(uploaded, failed);
                    return new PhotoUploadReport(uploaded, failed, Stopped: true);
                }

                // The entry goes first: if the app dies before the files are deleted, the sweep removes them.
                await db.Execute($"DELETE FROM {Table} WHERE id = ?", [row.Id]).ConfigureAwait(false);
                DeleteFiles(row.Id);
                uploaded++;
            }

            Notify(uploaded, failed);
            return new PhotoUploadReport(uploaded, failed, Stopped: false);
        }
        finally
        {
            running = null;
            pass.Release();
        }
    }

    /// <summary>Photos whose files haven't reached the server yet, failed ones included.</summary>
    public async Task<long> PendingCountAsync()
    {
        var row = await db.Get<CountRow>($"SELECT count(*) AS Count FROM {Table}", []).ConfigureAwait(false);
        return row.Count;
    }

    /// <summary>Photos the server refused for good, waiting for the user to retry or cancel them.</summary>
    public async Task<long> FailedCountAsync()
    {
        var row = await db.Get<CountRow>($"SELECT count(*) AS Count FROM {Table} WHERE failed_at IS NOT NULL", []).ConfigureAwait(false);
        return row.Count;
    }

    public Task<FailedPhotoUpload[]> FailedAsync() =>
        db.GetAll<FailedPhotoUpload>(
            $"SELECT id AS Id, error_code AS ErrorCode, failed_at AS FailedAt FROM {Table} WHERE failed_at IS NOT NULL ORDER BY failed_at", []);

    /// <summary>Puts a failed photo back in the queue.</summary>
    public async Task RetryAsync(string id)
    {
        await db.Execute($"UPDATE {Table} SET failed_at = NULL, error_code = NULL WHERE id = ?", [id]).ConfigureAwait(false);
        Wake();
    }

    /// <summary>
    /// Stops a photo's files from uploading and deletes them from the device. Used when the photo is deleted before
    /// its files went up; deleting the row and any stored files is the delete flow's job.
    /// </summary>
    public async Task CancelAsync(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        await pass.WaitAsync().ConfigureAwait(false);
        try
        {
            await db.Execute($"DELETE FROM {Table} WHERE id = ?", [id]).ConfigureAwait(false);
            DeleteFiles(id);
        }
        finally
        {
            pass.Release();
        }
    }

    /// <summary>
    /// Gives up on a photo the server refused, its files or its row. Any file of it already in Storage is deleted
    /// first (the thumbnail goes up before the photo, so one may be there), which needs a connection: if Storage
    /// can't be reached this throws <see cref="PhotoUploadException"/> and changes nothing. Then its queue entry,
    /// any quarantined write of its row and its local files go, and its row is deleted through the normal upload
    /// queue (which treats a row the server never got as already gone). Deleting a photo that uploaded fine is the
    /// delete flow's job.
    /// </summary>
    public async Task DiscardFailedAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var before = Volatile.Read(ref generation);
        await pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Registered like a pass, so a sign-out or account switch cancels the Storage requests instead of waiting.
        using var current = Register(before, cancellationToken);
        cancellationToken = current.Token;
        try
        {
            // The row may be gone already: PowerSync rolls a rejected insert back once the upload queue is empty. So the
            // profile comes from the row, the file queue entry or the quarantined write, whichever is still there.
            var photo = await db.GetOptional<DiscardRow>(
                $"SELECT (SELECT count(*) FROM {Table} u WHERE u.id = ? AND u.failed_at IS NOT NULL) AS FilesFailed, " +
                $"(SELECT count(*) FROM {AppSchema.FailedUploads} f WHERE f.table_name = 'progress_photos' AND f.row_id = ? AND f.op <> 'DELETE') AS RowRejected, " +
                "coalesce((SELECT p.profile_id FROM progress_photos p WHERE p.id = ?), " +
                $"(SELECT u.profile_id FROM {Table} u WHERE u.id = ?), " +
                $"(SELECT json_extract(f.data, '$.profile_id') FROM {AppSchema.FailedUploads} f WHERE f.table_name = 'progress_photos' AND f.row_id = ? LIMIT 1)) AS ProfileId",
                [id, id, id, id, id]).ConfigureAwait(false);
            if (photo is null || (photo.FilesFailed == 0 && photo.RowRejected == 0) || string.IsNullOrEmpty(photo.ProfileId))
            {
                throw new InvalidOperationException("That photo isn't waiting as a refused upload.");
            }

            await storage.DeleteAsync($"{photo.ProfileId}/{ThumbName(id)}", cancellationToken).ConfigureAwait(false);
            await storage.DeleteAsync($"{photo.ProfileId}/{PhotoName(id)}", cancellationToken).ConfigureAwait(false);
            await db.WriteTransaction(async tx =>
            {
                await tx.Execute($"DELETE FROM {Table} WHERE id = ?", [id]).ConfigureAwait(false);
                await tx.Execute(
                    $"DELETE FROM {AppSchema.FailedUploads} WHERE table_name = 'progress_photos' AND row_id = ? AND op <> 'DELETE'", [id]).ConfigureAwait(false);
                // Only if it's still here; deleting it queues a DELETE, which the server treats as done if it never had it.
                await tx.Execute("DELETE FROM progress_photos WHERE id = ?", [id]).ConfigureAwait(false);
            }).ConfigureAwait(false);
            DeleteFiles(id);
        }
        finally
        {
            running = null;
            pass.Release();
        }
    }

    /// <summary>
    /// Deletes files that no queue entry refers to: left by a crash between an upload and its clean-up, or by a
    /// database cleared while the app was closed.
    /// </summary>
    public async Task<int> SweepAsync()
    {
        await pass.WaitAsync().ConfigureAwait(false);
        try
        {
            var queued = (await db.GetAll<PendingRow>($"SELECT id AS Id, profile_id AS ProfileId FROM {Table}", []).ConfigureAwait(false))
                .SelectMany(r => new[] { PhotoName(r.Id), ThumbName(r.Id) })
                .ToHashSet(StringComparer.Ordinal);
            var removed = 0;
            foreach (var name in files.List().Where(n => !queued.Contains(n)))
            {
                files.Delete(name);
                removed++;
            }

            return removed;
        }
        finally
        {
            pass.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="clear"/> (which empties the local database) with uploads stopped: the pass in progress
    /// is cancelled and waited for, no pass or add starts until it is done, and then every photo file is deleted.
    /// <see cref="LocalDatabase"/> calls this for a sign-out or another user's sign-in. A request already sent when
    /// the pass is cancelled may still complete, but only into the departing owner's own folder with their token.
    /// </summary>
    public async Task ClearAsync(Func<Task> clear)
    {
        ArgumentNullException.ThrowIfNull(clear);
        Interlocked.Increment(ref generation);
        Clearing?.Invoke();
        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That pass has just finished.
        }

        await pass.WaitAsync().ConfigureAwait(false);
        try
        {
            await clear().ConfigureAwait(false);
            files.DeleteAll();
        }
        finally
        {
            pass.Release();
        }
    }

    /// <summary>
    /// Makes the work that now holds the lock cancellable by <see cref="ClearAsync"/>. A clear that began after
    /// <paramref name="before"/> was read may have looked for running work before this registered, and is now waiting
    /// for the lock; the work is then cancelled at once rather than left to run uncancelled.
    /// </summary>
    private CancellationTokenSource Register(int before, CancellationToken cancellationToken)
    {
        var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        running = current;
        if (Volatile.Read(ref generation) != before)
        {
            current.Cancel();
        }

        return current;
    }

    /// <summary>Asks the background loop to try now (a photo was added, or the connection came back).</summary>
    public void Wake()
    {
        if (Volatile.Read(ref disposed) == 1)
        {
            return;
        }

        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile.
        }
    }

    /// <summary>Starts the background loop once; later calls do nothing. It runs until this is disposed.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            var stopping = stop.Token;
            _ = Task.Run(() => RunAsync(stopping), CancellationToken.None);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1)
        {
            return;
        }

        stop.Cancel();
        stop.Dispose();
        pass.Dispose();
        wake.Dispose();
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            try
            {
                await SweepAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException and not ObjectDisposedException)
            {
                // Left for the next start; uploads go on regardless.
            }

            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await ProcessAsync(stopping).ConfigureAwait(false);
                }
                catch (Exception) when (!stopping.IsCancellationRequested)
                {
                    // A pass cancelled by a sign-out, or a closed or cleared database; the next pass starts afresh.
                }

                await wake.WaitAsync(RetryInterval, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task UploadAsync(PendingRow row, CancellationToken cancellationToken)
    {
        foreach (var name in new[] { ThumbName(row.Id), PhotoName(row.Id) })
        {
            var bytes = await files.ReadAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new PhotoUploadException("The photo file is missing.", permanent: true, code: "missing_file");
            // Checked again here, so nothing with metadata can leave the device whatever wrote the file.
            if (!JpegMetadata.IsStripped(bytes))
            {
                throw new PhotoUploadException("The photo file still carries metadata.", permanent: true, code: "not_stripped");
            }

            await storage.UploadAsync($"{row.ProfileId}/{name}", bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static byte[] Strip(byte[] jpeg, string parameter)
    {
        if (!JpegMetadata.TryStrip(jpeg, out var stripped))
        {
            throw new ArgumentException("Not a complete JPEG image.", parameter);
        }

        if (!stripped!.IsUpright)
        {
            throw new ArgumentException("The image must be rotated upright before it is added.", parameter);
        }

        return stripped.Jpeg;
    }

    private void DeleteFiles(string id)
    {
        files.Delete(PhotoName(id));
        files.Delete(ThumbName(id));
    }

    private void Notify(int uploaded, int failed)
    {
        if (uploaded + failed > 0)
        {
            Changed?.Invoke();
        }
    }

    internal sealed class PendingRow
    {
        public string Id { get; set; } = "";

        public string ProfileId { get; set; } = "";
    }

    internal sealed class DiscardRow
    {
        public string? ProfileId { get; set; }

        public long FilesFailed { get; set; }

        public long RowRejected { get; set; }
    }

    internal sealed class CountRow
    {
        public long Count { get; set; }
    }
}

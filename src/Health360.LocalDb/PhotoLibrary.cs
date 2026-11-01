using Health360.Domain.Photos;

namespace Health360.LocalDb;

/// <summary>Whether a progress photo's image could be shown.</summary>
public enum PhotoImageStatus
{
    /// <summary>The image is here: from this device's upload queue, or downloaded from Storage.</summary>
    Available,

    /// <summary>
    /// Storage has no file for it yet: its row synced, but the device that took it hasn't uploaded the files. It
    /// appears once that device is online.
    /// </summary>
    NotUploaded,

    /// <summary>Storage couldn't be reached (offline, signed out, a server error); trying again may work.</summary>
    Unreachable,

    /// <summary>Storage refused to send it, or sent something that isn't a JPEG.</summary>
    Refused,
}

/// <summary>A photo's image, or why there isn't one. <see cref="Jpeg"/> is set only when <see cref="Status"/> is Available.</summary>
public sealed record PhotoImage(PhotoImageStatus Status, byte[]? Jpeg = null)
{
    public static readonly PhotoImage NotUploaded = new(PhotoImageStatus.NotUploaded);

    public static readonly PhotoImage Unreachable = new(PhotoImageStatus.Unreachable);

    public static readonly PhotoImage Refused = new(PhotoImageStatus.Refused);
}

/// <summary>
/// Loads progress photos for viewing (step 1.1). A photo still in this device's upload queue is read from its local
/// file; any other is downloaded from Storage with the owner's session. One whose files haven't reached Storage from
/// the device that took it is reported as <see cref="PhotoImageStatus.NotUploaded"/>, not as an error.
/// <para>
/// Downloads are kept in memory only, never written to disk: thumbnails in a small cache, which empties whenever the
/// account's data is cleared (sign-out or another user), and full photos not at all.
/// </para>
/// </summary>
public sealed class PhotoLibrary
{
    /// <summary>The most thumbnails kept in memory (about 20 KB each).</summary>
    public const int ThumbnailCacheSize = 200;

    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]> thumbnails = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();

    // Thumbnails of deleted photos, so a download already under way when one was forgotten isn't cached afterwards.
    private readonly HashSet<string> forgotten = new(StringComparer.Ordinal);
    private readonly IPhotoFileStore files;
    private readonly IPhotoStorage storage;
    private readonly PhotoUploader uploader;
    private int cachedGeneration;

    public PhotoLibrary(IPhotoFileStore files, IPhotoStorage storage, PhotoUploader uploader)
    {
        ArgumentNullException.ThrowIfNull(uploader);
        this.files = files;
        this.storage = storage;
        this.uploader = uploader;
        // Dropped as soon as a sign-out or account switch begins, not at the next lookup.
        uploader.Clearing += () =>
        {
            lock (gate)
            {
                Reset(uploader.ClearGeneration);
            }
        };
    }

    /// <summary>How many thumbnails are held in memory.</summary>
    public int CachedCount
    {
        get
        {
            lock (gate)
            {
                return thumbnails.Count;
            }
        }
    }

    /// <summary>
    /// Drops a photo's cached thumbnail, for a photo deleted here or whose row disappeared when another device deleted
    /// it.
    /// </summary>
    public void Forget(string id)
    {
        var name = PhotoUploader.ThumbName(id);
        lock (gate)
        {
            forgotten.Add(name);
            if (thumbnails.Remove(name))
            {
                var kept = order.Where(n => n != name).ToList();
                order.Clear();
                foreach (var n in kept)
                {
                    order.Enqueue(n);
                }
            }
        }
    }

    /// <summary>The photo's thumbnail.</summary>
    public Task<PhotoImage> ThumbnailAsync(string profileId, string id, CancellationToken cancellationToken = default) =>
        LoadAsync(profileId, id, thumbnail: true, cancellationToken);

    /// <summary>The full photo.</summary>
    public Task<PhotoImage> PhotoAsync(string profileId, string id, CancellationToken cancellationToken = default) =>
        LoadAsync(profileId, id, thumbnail: false, cancellationToken);

    private async Task<PhotoImage> LoadAsync(string profileId, string id, bool thumbnail, CancellationToken cancellationToken)
    {
        // The same rule as the row's check constraint: the path is derived, never read from a synced value.
        if (!IsUuid(profileId) || !IsUuid(id))
        {
            throw new ArgumentException("Photo and profile ids must be lower-case UUIDs.");
        }

        var name = thumbnail ? PhotoUploader.ThumbName(id) : PhotoUploader.PhotoName(id);
        var generation = uploader.ClearGeneration;
        if (thumbnail && Cached(name, generation) is { } hit)
        {
            return new PhotoImage(PhotoImageStatus.Available, hit);
        }

        // Still waiting to upload from this device: the stripped file is here.
        var local = await files.ReadAsync(name, cancellationToken).ConfigureAwait(false);
        ThrowIfCleared(generation);
        if (local is not null)
        {
            return IsJpeg(local) ? new PhotoImage(PhotoImageStatus.Available, local) : PhotoImage.Refused;
        }

        byte[]? jpeg;
        try
        {
            jpeg = await storage.DownloadAsync($"{profileId}/{name}", cancellationToken).ConfigureAwait(false);
        }
        catch (PhotoUploadException error)
        {
            ThrowIfCleared(generation);
            return error.Permanent ? PhotoImage.Refused : PhotoImage.Unreachable;
        }

        // A sign-out or account switch began while this waited: the bytes belong to the account that left.
        ThrowIfCleared(generation);
        if (jpeg is null)
        {
            return PhotoImage.NotUploaded;
        }

        if (!IsJpeg(jpeg))
        {
            return PhotoImage.Refused;
        }

        if (thumbnail)
        {
            Remember(name, jpeg, generation);
        }

        return new PhotoImage(PhotoImageStatus.Available, jpeg);
    }

    private byte[]? Cached(string name, int generation)
    {
        lock (gate)
        {
            Reset(generation);
            return thumbnails.GetValueOrDefault(name);
        }
    }

    private void Remember(string name, byte[] jpeg, int generation)
    {
        lock (gate)
        {
            // Downloaded before a clear finished: it belongs to the previous account, so it isn't kept.
            if (generation != uploader.ClearGeneration)
            {
                return;
            }

            Reset(generation);
            if (!forgotten.Contains(name) && thumbnails.TryAdd(name, jpeg))
            {
                order.Enqueue(name);
                while (order.Count > ThumbnailCacheSize)
                {
                    thumbnails.Remove(order.Dequeue());
                }
            }
        }
    }

    private void Reset(int generation)
    {
        if (generation != cachedGeneration)
        {
            thumbnails.Clear();
            order.Clear();
            forgotten.Clear();
            cachedGeneration = generation;
        }
    }

    private void ThrowIfCleared(int generation)
    {
        if (generation != uploader.ClearGeneration)
        {
            throw new OperationCanceledException("The account's data was cleared while the photo loaded.");
        }
    }

    /// <summary>A whole, well-formed JPEG (frame, scan and end marker), not just its first bytes.</summary>
    private static bool IsJpeg(byte[] jpeg) => JpegMetadata.TryStrip(jpeg, out _);

    private static bool IsUuid(string value) => Guid.TryParse(value, out var guid) && guid.ToString() == value;
}

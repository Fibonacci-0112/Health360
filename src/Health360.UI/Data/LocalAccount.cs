using Health360.LocalDb;
using Health360.UI.Auth;

namespace Health360.UI.Data;

/// <summary>
/// The app's <see cref="ILocalAccount"/>: the PowerSync database's account binding. Progress photos whose files haven't
/// uploaded count as unsynced too, even once their row has synced, so a sign-out never deletes them unannounced.
/// </summary>
public sealed class LocalAccount(LocalDatabase database, PhotoUploader? photos = null) : ILocalAccount
{
    public Task BindAsync(string userId) => database.BindToUserAsync(userId);

    public Task ConnectAsync(string userId) => database.ConnectAsync(userId);

    public Task DisconnectAsync() => database.DisconnectAsync();

    public Task ClearAsync() => database.ClearAsync();

    public async Task<UnsyncedChanges> UnsyncedAsync()
    {
        var pending = await Uploads.PendingUploadCountAsync(database.Db).ConfigureAwait(false);
        var failed = await Uploads.FailedUploadCountAsync(database.Db).ConfigureAwait(false);
        if (photos is not null)
        {
            var failedPhotos = await photos.FailedCountAsync().ConfigureAwait(false);
            pending += await photos.PendingCountAsync().ConfigureAwait(false) - failedPhotos;
            failed += failedPhotos;
        }

        return new(pending, failed);
    }
}

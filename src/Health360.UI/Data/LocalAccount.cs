using Health360.LocalDb;
using Health360.UI.Auth;

namespace Health360.UI.Data;

/// <summary>The app's <see cref="ILocalAccount"/>: the PowerSync database's account binding.</summary>
public sealed class LocalAccount(LocalDatabase database) : ILocalAccount
{
    public Task BindAsync(string userId) => database.BindToUserAsync(userId);

    public Task ConnectAsync(string userId) => database.ConnectAsync(userId);

    public Task DisconnectAsync() => database.DisconnectAsync();

    public Task ClearAsync() => database.ClearAsync();

    public async Task<UnsyncedChanges> UnsyncedAsync() =>
        new(await Uploads.PendingUploadCountAsync(database.Db).ConfigureAwait(false),
            await Uploads.FailedUploadCountAsync(database.Db).ConfigureAwait(false));
}

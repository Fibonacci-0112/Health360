namespace Health360.LocalDb;

/// <summary>The signed-in Supabase session, as the sync connector and uploads need it.</summary>
public interface ISupabaseSession
{
    /// <summary>
    /// The current access token and the user it belongs to, refreshed first if it is about to expire;
    /// null when nobody is signed in. Never log the token.
    /// </summary>
    Task<SessionToken?> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>An access token and the id of the user it was issued to.</summary>
public sealed record SessionToken(string UserId, string AccessToken)
{
    // Keeps the token out of logs and debugger displays.
    public override string ToString() => $"SessionToken {{ UserId = {UserId} }}";
}

/// <summary>Where the app syncs and uploads. None of these are secrets: the API key is the publishable (anon) key.</summary>
/// <param name="SupabaseUrl">Project URL, such as <c>http://127.0.0.1:54321</c> for the local stack.</param>
/// <param name="SupabaseApiKey">The publishable (anon) key. Never the service-role key.</param>
/// <param name="PowerSyncUrl">The PowerSync service the app downloads from.</param>
public sealed record SyncEndpoints(Uri SupabaseUrl, string SupabaseApiKey, Uri PowerSyncUrl);

internal static class OwnerSession
{
    /// <summary>
    /// The access token, but only if it belongs to the user who owns the local database. When someone
    /// else has signed in, the database still holds the previous user's rows and unsent writes until
    /// <see cref="LocalDatabase.ConnectAsync"/> rebinds and clears it; until then nothing may sync or
    /// upload with the new user's token, or the old writes would be applied as them.
    /// </summary>
    public static async Task<string?> TokenAsync(ISupabaseSession session, IDatabaseOwnerStore owner, CancellationToken cancellationToken)
    {
        var token = await session.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        return token is not null && token.UserId == owner.Load() ? token.AccessToken : null;
    }
}

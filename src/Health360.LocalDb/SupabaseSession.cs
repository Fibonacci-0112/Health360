namespace Health360.LocalDb;

/// <summary>The signed-in Supabase session, as the sync connector and uploads need it.</summary>
public interface ISupabaseSession
{
    /// <summary>
    /// The current access token, refreshed first if it is about to expire; null when nobody is signed
    /// in. Never log it.
    /// </summary>
    Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>Where the app syncs and uploads. None of these are secrets: the API key is the publishable (anon) key.</summary>
/// <param name="SupabaseUrl">Project URL, such as <c>http://127.0.0.1:54321</c> for the local stack.</param>
/// <param name="SupabaseApiKey">The publishable (anon) key. Never the service-role key.</param>
/// <param name="PowerSyncUrl">The PowerSync service the app downloads from.</param>
public sealed record SyncEndpoints(Uri SupabaseUrl, string SupabaseApiKey, Uri PowerSyncUrl);

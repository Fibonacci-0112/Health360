using Microsoft.Extensions.Logging;
using PowerSync.Common.Client;
using PowerSync.Common.Client.Connection;

namespace Health360.LocalDb;

/// <summary>
/// Downloads authenticate with the Supabase access token; uploads go through PostgREST with the same
/// session, so RLS checks every write. A write the server rejects for good (RLS, a constraint) is moved
/// to <c>failed_uploads</c> instead of blocking the queue (PLAN.md §4).
/// </summary>
public sealed partial class SupabaseConnector(
    ISupabaseSession session,
    IDatabaseOwnerStore owner,
    IRemoteWriter writer,
    SyncEndpoints endpoints,
    TimeProvider clock,
    ILogger<SupabaseConnector> logger) : IPowerSyncBackendConnector
{
    public async Task<PowerSyncCredentials?> FetchCredentials()
    {
        var token = await OwnerSession.TokenAsync(session, owner, CancellationToken.None).ConfigureAwait(false);
        // No session, or another user's: PowerSync stays disconnected until the app connects again
        // after sign-in, which rebinds the database first.
        return token is null ? null : new PowerSyncCredentials(Endpoint(endpoints.PowerSyncUrl), token, null);
    }

    /// <summary>
    /// The PowerSync URL without a trailing slash. The SDK appends <c>/sync/stream</c>, and <see cref="Uri"/>
    /// always ends a bare host with <c>/</c>, so passing it as is requests <c>//sync/stream</c>, which the
    /// service answers with 404: sync then retries forever and never connects.
    /// </summary>
    public static string Endpoint(Uri powerSyncUrl)
    {
        ArgumentNullException.ThrowIfNull(powerSyncUrl);
        return powerSyncUrl.ToString().TrimEnd('/');
    }

    public Task UploadData(IPowerSyncDatabase database) =>
        Uploads.UploadPendingAsync(database, writer, clock, op => LogQuarantined(logger, op.Op.ToString(), op.Table, op.Code ?? "no code"));

    // Sentry (with PII scrubbing) replaces this in Phase 1. Never log row data or ids: it is health data.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Upload quarantined: {Op} {Table} ({Code})")]
    private static partial void LogQuarantined(ILogger logger, string op, string table, string code);
}

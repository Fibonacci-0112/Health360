using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PowerSync.Common.Client;
using PowerSync.Common.DB.Crud;

namespace Health360.LocalDb;

/// <summary>A write the server rejected for good, as reported to the connector's log.</summary>
public sealed record QuarantinedOp(string Table, UpdateType Op, string? Code);

/// <summary>The outcome of <see cref="Uploads.RetryFailedUploadAsync"/>.</summary>
public enum RetryResult
{
    /// <summary>The server accepted it; the entry is removed and sync brings the result back.</summary>
    Sent,

    /// <summary>The server refused it again; the entry stays, with the new error.</summary>
    Rejected,

    /// <summary>It couldn't be sent (no connection, server error); the entry stays as it was.</summary>
    Offline,
}

/// <summary>
/// Uploads the local write queue and keeps the <c>failed_uploads</c> quarantine (PLAN.md §4).
/// </summary>
public static partial class Uploads
{
    /// <summary>
    /// Errors that will fail the same way however often the write is retried. These are quarantined so
    /// one bad row can't block every later write. Anything else (network, 5xx, an expired token) is
    /// thrown so PowerSync retries the whole transaction later.
    ///   22xxx data exception · 23xxx integrity violation · 42xxx access rule violation or schema
    ///   mismatch (42501 is RLS) · PGRST1xx/2xx malformed request (PGRST3xx are auth, so transient).
    /// </summary>
    public static bool IsPermanentError(string? code) =>
        !string.IsNullOrEmpty(code) && (SqlStateRegex().IsMatch(code) || PostgrestRegex().IsMatch(code));

    /// <summary>
    /// Uploads every pending transaction. Each op either reaches the server, is quarantined in the
    /// local-only <c>failed_uploads</c> table, or (on a transient error) aborts the upload so it is retried.
    /// </summary>
    public static async Task UploadPendingAsync(
        IPowerSyncDatabase db,
        IRemoteWriter writer,
        TimeProvider clock,
        Action<QuarantinedOp>? onQuarantine = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tx = await db.GetNextCrudTransaction().ConfigureAwait(false);
            if (tx is null)
            {
                return;
            }

            foreach (var op in tx.Crud)
            {
                var local = await LocalRowAsync(db, op).ConfigureAwait(false);
                try
                {
                    await ApplyAsync(writer, op.Table, op.Id, op.Op, local, cancellationToken).ConfigureAwait(false);
                    // A retried transaction may now accept an op it rejected on an earlier attempt.
                    await db.Execute($"DELETE FROM {AppSchema.FailedUploads} WHERE id = ?", [QuarantineId(op)]).ConfigureAwait(false);
                }
                catch (RemoteWriteException error) when (IsPermanentError(error.Code))
                {
                    // A rejected insert or update of a row this device has since deleted (a discarded photo: its
                    // DELETE is queued after this op) is dropped, since replaying it from the quarantine would bring
                    // back what the user deleted. A row that only vanished through sync is quarantined as before.
                    if (op.Op != UpdateType.DELETE && await DeletedLaterAsync(db, op).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await QuarantineAsync(db, op, local, error, clock).ConfigureAwait(false);
                    onQuarantine?.Invoke(new QuarantinedOp(op.Table, op.Op, error.Code));
                }
            }

            await tx.Complete().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Converts a row from its SQLite form to what Postgres expects. Without this, a jsonb column would
    /// be uploaded as a JSON string inside JSON, and a boolean as a number.
    /// </summary>
    public static JsonObject ToServerRow(string table, JsonObject local)
    {
        ArgumentNullException.ThrowIfNull(local);
        var row = (JsonObject)local.DeepClone();
        if (!AppSchema.ServerColumnTypes.TryGetValue(table, out var types))
        {
            return row;
        }

        foreach (var (column, type) in types)
        {
            if (row[column] is not JsonValue value)
            {
                continue;
            }

            if (type == ServerColumnType.Boolean)
            {
                row[column] = IsTruthy(value);
            }
            else if (value.TryGetValue<string>(out var text))
            {
                try
                {
                    row[column] = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    // Can never succeed on retry: quarantine it (22P02 is Postgres' invalid_text_representation).
                    throw new RemoteWriteException($"{table}.{column} is not valid JSON", "22P02");
                }
            }
        }

        return row;
    }

    /// <summary>Number of writes waiting to upload, for the "unsynced entries" badge.</summary>
    public static async Task<long> PendingUploadCountAsync(PowerSyncDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return (await db.GetUploadQueueStats().ConfigureAwait(false)).Count;
    }

    /// <summary>Number of writes the server rejected, kept for the user to retry or discard.</summary>
    public static async Task<long> FailedUploadCountAsync(IPowerSyncDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return (await db.Get<CountRow>($"SELECT count(*) AS N FROM {AppSchema.FailedUploads}").ConfigureAwait(false)).N;
    }

    /// <summary>Drops a quarantined write for good.</summary>
    public static async Task DiscardFailedUploadAsync(IPowerSyncDatabase db, string id)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Execute($"DELETE FROM {AppSchema.FailedUploads} WHERE id = ?", [id]).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a quarantined write to the server again, directly through <paramref name="writer"/>, after
    /// the user has fixed what was wrong. It is not replayed into the local database: an edit whose
    /// values are still in the local row would queue an empty PATCH there and upload nothing.
    /// </summary>
    public static async Task<RetryResult> RetryFailedUploadAsync(
        IPowerSyncDatabase db,
        string id,
        IRemoteWriter writer,
        TimeProvider clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(clock);

        var row = await db.GetOptional<FailedUploadRow>(
            $"SELECT table_name AS TableName, row_id AS RowId, op AS Op, data AS Data FROM {AppSchema.FailedUploads} WHERE id = ?",
            [id]).ConfigureAwait(false);
        if (row is null)
        {
            return RetryResult.Sent;
        }

        try
        {
            var op = Enum.Parse<UpdateType>(row.Op);
            var local = JsonNode.Parse(row.Data) as JsonObject ?? [];
            await ApplyAsync(writer, row.TableName, row.RowId, op, local, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteWriteException error) when (IsPermanentError(error.Code))
        {
            await db.Execute(
                $"UPDATE {AppSchema.FailedUploads} SET error_code = ?, error_message = ?, failed_at = ? WHERE id = ?",
                [error.Code, error.Message, Timestamp(clock), id]).ConfigureAwait(false);
            return RetryResult.Rejected;
        }
        catch (Exception error) when (error is RemoteWriteException or HttpRequestException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return RetryResult.Offline;
        }

        await db.Execute($"DELETE FROM {AppSchema.FailedUploads} WHERE id = ?", [id]).ConfigureAwait(false);
        return RetryResult.Sent;
    }

    private static async Task ApplyAsync(IRemoteWriter writer, string table, string id, UpdateType op, JsonObject local, CancellationToken cancellationToken)
    {
        var data = ToServerRow(table, local);
        switch (op)
        {
            case UpdateType.PUT:
                if (AppSchema.NullableOnPut.TryGetValue(table, out var nullable))
                {
                    foreach (var column in nullable.Where(c => !data.ContainsKey(c)))
                    {
                        data[column] = null;
                    }
                }

                await writer.PutAsync(table, id, data, cancellationToken).ConfigureAwait(false);
                break;
            case UpdateType.PATCH:
                // An UPDATE that assigns the values a row already has (re-selecting the current unit)
                // still queues a PATCH, with nothing in it. There is nothing to send, and no row to check for.
                if (data.Count > 0)
                {
                    await writer.PatchAsync(table, id, data, cancellationToken).ConfigureAwait(false);
                }

                break;
            case UpdateType.DELETE:
                await writer.DeleteAsync(table, id, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown CRUD operation.");
        }
    }

    /// <summary>
    /// Keyed by the op's position in the upload queue, so when a transaction is retried (a later op hit
    /// a network error) the same rejected op replaces its entry instead of adding a duplicate.
    /// </summary>
    private static string QuarantineId(CrudEntry op) => $"crud-{op.ClientId.ToString(CultureInfo.InvariantCulture)}";

    private static async Task<bool> DeletedLaterAsync(IPowerSyncDatabase db, CrudEntry op) =>
        await db.GetOptional<CountRow>(
            "SELECT 1 AS N FROM ps_crud WHERE id > ? AND json_extract(data, '$.op') = 'DELETE' " +
            "AND json_extract(data, '$.type') = ? AND json_extract(data, '$.id') = ? LIMIT 1",
            [op.ClientId, op.Table, op.Id]).ConfigureAwait(false) is not null;

    private static async Task QuarantineAsync(IPowerSyncDatabase db, CrudEntry op, JsonObject local, RemoteWriteException error, TimeProvider clock) =>
        await db.Execute(
            $"""
            INSERT OR REPLACE INTO {AppSchema.FailedUploads} (id, table_name, row_id, op, data, error_code, error_message, failed_at)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            [QuarantineId(op), op.Table, op.Id, op.Op.ToString(), local.ToJsonString(), error.Code, error.Message, Timestamp(clock)]).ConfigureAwait(false);

    /// <summary>
    /// The op's column values exactly as PowerSync queued them. <see cref="CrudEntry.OpData"/> can't be
    /// used: the SDK parses it with Newtonsoft, which turns any text that looks like an ISO date-time
    /// (a <c>created_at</c>, or a note that happens to read like one) into a <see cref="DateTime"/>,
    /// losing the original text and its UTC offset.
    /// </summary>
    private static async Task<JsonObject> LocalRowAsync(IPowerSyncDatabase db, CrudEntry op)
    {
        var row = await db.GetOptional<QueuedRow>("SELECT data AS Data FROM ps_crud WHERE id = ?", [op.ClientId]).ConfigureAwait(false)
            // Only if the queue was cleared mid-upload: PowerSync retries, and finds the queue empty.
            ?? throw new InvalidOperationException("A queued write disappeared during upload.");
        return JsonNode.Parse(row.Data)?["data"] is JsonObject data ? (JsonObject)data.DeepClone() : [];
    }

    private static bool IsTruthy(JsonValue value) =>
        (value.TryGetValue<bool>(out var b) && b)
        || (value.TryGetValue<long>(out var l) && l == 1)
        || (value.TryGetValue<double>(out var d) && d == 1)
        || (value.TryGetValue<string>(out var s) && s is "1" or "true");

    private static string Timestamp(TimeProvider clock) =>
        clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    [GeneratedRegex("^(22|23|42)[0-9A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex SqlStateRegex();

    [GeneratedRegex(@"^PGRST[12]\d\d$", RegexOptions.CultureInvariant)]
    private static partial Regex PostgrestRegex();

    internal sealed class FailedUploadRow
    {
        public string TableName { get; set; } = "";

        public string RowId { get; set; } = "";

        public string Op { get; set; } = "";

        public string Data { get; set; } = "{}";
    }

    internal sealed class QueuedRow
    {
        public string Data { get; set; } = "{}";
    }

    internal sealed class CountRow
    {
        public long N { get; set; }
    }
}

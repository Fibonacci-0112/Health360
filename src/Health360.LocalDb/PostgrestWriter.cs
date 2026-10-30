using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Health360.LocalDb;

/// <summary>
/// Uploads through PostgREST with the user's own session, so every write passes the same RLS the
/// pgTAP tests check. PUT is an upsert because PowerSync replays a whole row for inserts. Ports
/// <c>packages/api/src/writer.ts</c>.
/// </summary>
/// <param name="http">A client whose base address is the Supabase project URL.</param>
/// <param name="session">The signed-in session whose access token every request carries.</param>
/// <param name="apiKey">The publishable (anon) key, sent as the <c>apikey</c> header.</param>
public sealed class PostgrestWriter(HttpClient http, ISupabaseSession session, string apiKey) : IRemoteWriter
{
    /// <summary>PostgREST's code for "no rows"; <see cref="Uploads.IsPermanentError"/> treats it as permanent.</summary>
    public const string NoRow = "PGRST116";

    /// <summary>Postgres' insufficient_privilege, the same code an RLS-rejected insert gets.</summary>
    public const string Denied = "42501";

    public async Task PutAsync(string table, string id, JsonObject data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var row = (JsonObject)data.DeepClone();
        row["id"] = id;
        // Insert-only tables (consents) have no UPDATE grant; a replayed insert must then be a no-op.
        var resolution = AppSchema.InsertOnlyTables.Contains(table) ? "ignore-duplicates" : "merge-duplicates";
        using var request = await RequestAsync(HttpMethod.Post, $"{Path(table)}?on_conflict=id", cancellationToken).ConfigureAwait(false);
        request.Headers.Add("Prefer", $"resolution={resolution},return=minimal");
        request.Content = Json(row);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task PatchAsync(string table, string id, JsonObject data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        // PostgREST answers an UPDATE that matched nothing (RLS hid the row, or another device deleted
        // it) with success. Ask for the row back so that case is quarantined instead of silently lost.
        using var request = await RequestAsync(HttpMethod.Patch, $"{ById(table, id)}&select=id", cancellationToken).ConfigureAwait(false);
        request.Headers.Add("Prefer", "return=representation");
        request.Content = Json(data);
        if (await RowCountAsync(request, cancellationToken).ConfigureAwait(false) == 0)
        {
            throw new RemoteWriteException($"{table}: the update matched no row", NoRow);
        }
    }

    public async Task DeleteAsync(string table, string id, CancellationToken cancellationToken)
    {
        // Like UPDATE, a DELETE that RLS blocks "succeeds" with no rows. Deleting a row that is already
        // gone is fine; one that is still there means the delete wasn't allowed (consents have no delete
        // policy), so it must be quarantined rather than reported as done.
        using (var delete = await RequestAsync(HttpMethod.Delete, $"{ById(table, id)}&select=id", cancellationToken).ConfigureAwait(false))
        {
            delete.Headers.Add("Prefer", "return=representation");
            if (await RowCountAsync(delete, cancellationToken).ConfigureAwait(false) > 0)
            {
                return;
            }
        }

        using var read = await RequestAsync(HttpMethod.Get, $"{ById(table, id)}&select=id", cancellationToken).ConfigureAwait(false);
        if (await RowCountAsync(read, cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new RemoteWriteException($"{table}: the delete was not allowed", Denied);
        }
    }

    private static string Path(string table) => $"rest/v1/{Uri.EscapeDataString(table)}";

    private static string ById(string table, string id) => $"{Path(table)}?id=eq.{Uri.EscapeDataString(id)}";

    private static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        // No session means nobody to write as. Not permanent: PowerSync retries once the user is back.
        var token = await session.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new RemoteWriteException("Not signed in.", (string?)null);
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("apikey", apiKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RowCountAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(body) is JsonArray rows
            ? rows.Count
            : throw new RemoteWriteException("PostgREST returned something other than a list of rows.", (string?)null);
    }

    /// <summary>
    /// Turns a PostgREST error into a <see cref="RemoteWriteException"/> with its code. A response
    /// without one (a gateway error page, say) gets no code, so it is retried rather than quarantined.
    /// </summary>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? code = null;
        string? message = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject error)
            {
                code = (error["code"] as JsonValue)?.ToString();
                message = (error["message"] as JsonValue)?.ToString();
            }
        }
        catch (JsonException)
        {
        }

        throw new RemoteWriteException(message ?? $"PostgREST answered {(int)response.StatusCode}.", string.IsNullOrEmpty(code) ? null : code);
    }
}

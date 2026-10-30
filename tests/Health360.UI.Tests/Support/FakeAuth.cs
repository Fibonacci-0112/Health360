using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Health360.UI.Tests.Support;

/// <summary>
/// Stands in for Supabase Auth (GoTrue): records each request and answers with <see cref="Respond"/>.
/// The default answers follow Auth's documented responses: OTP and logout succeed, verify and refresh
/// return a session for <see cref="UserId"/>.
/// </summary>
public sealed class FakeAuth : HttpMessageHandler
{
    public const string ApiKey = "publishable-key";

    public string UserId { get; set; } = "11111111-1111-1111-1111-111111111111";

    public List<Recorded> Requests { get; } = [];

    /// <summary>Overrides the answer for a request; return null to use the default.</summary>
    public Func<Recorded, HttpResponseMessage?> Respond { get; set; } = _ => null;

    private int issued;

    public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new Uri("http://auth.test/") };

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message) =>
        Json(status, new JsonObject { ["code"] = (int)status, ["error_code"] = code, ["msg"] = message }.ToJsonString());

    /// <summary>A session body as Auth returns it, with numbered tokens so tests can tell refreshes apart.</summary>
    public string SessionJson(long expiresAt)
    {
        var n = Interlocked.Increment(ref issued);
        return new JsonObject
        {
            ["access_token"] = $"access-{n}",
            ["token_type"] = "bearer",
            ["expires_in"] = 3600,
            ["expires_at"] = expiresAt,
            ["refresh_token"] = $"refresh-{n}",
            ["user"] = new JsonObject { ["id"] = UserId, ["email"] = "sam@example.com" },
        }.ToJsonString();
    }

    /// <summary>What expires_at the default session answers carry (Unix seconds).</summary>
    public long ExpiresAt { get; set; } = DateTimeOffset.Parse("2026-10-30T13:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) as JsonObject;
        var recorded = new Recorded(
            request.Method.Method,
            request.RequestUri!.PathAndQuery,
            request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null,
            request.Headers.Authorization?.Parameter,
            body);
        lock (Requests)
        {
            Requests.Add(recorded);
        }

        if (Respond(recorded) is { } custom)
        {
            return custom;
        }

        return recorded.Path switch
        {
            "/auth/v1/otp" => Json(HttpStatusCode.OK, "{}"),
            "/auth/v1/verify" or "/auth/v1/token?grant_type=refresh_token" => Json(HttpStatusCode.OK, SessionJson(ExpiresAt)),
            "/auth/v1/logout?scope=local" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => Error(HttpStatusCode.NotFound, "not_found", "Unknown path"),
        };
    }

    public sealed record Recorded(string Method, string Path, string? ApiKey, string? Bearer, JsonObject? Body);
}

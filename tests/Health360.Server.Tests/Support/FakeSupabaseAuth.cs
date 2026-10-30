using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Health360.Server.Tests.Support;

/// <summary>
/// Stands in for Supabase Auth: serves the JWKS and answers <c>GET /auth/v1/user</c> like Auth
/// does, for every token except the ones marked signed out.
/// </summary>
public sealed class FakeSupabaseAuth
{
    private int _userCalls;

    /// <summary>Tokens whose session was signed out, or whose user was deleted.</summary>
    public ConcurrentDictionary<string, bool> SignedOut { get; } = new();

    /// <summary>When set, Auth answers 503 to user lookups.</summary>
    public bool Down { get; set; }

    /// <summary>When set, the JWKS endpoint answers 500.</summary>
    public bool JwksDown { get; set; }

    /// <summary>When set, user lookups answer 200 with this body instead of the user.</summary>
    public string? UserBodyOverride { get; set; }

    public int UserCalls => _userCalls;

    /// <summary>A fresh handler each time: the HTTP client factory disposes the handlers it is given.</summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    private HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/auth/v1/.well-known/jwks.json")
        {
            return JwksDown ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Json(HttpStatusCode.OK, new { keys = new[] { PublicJwk() } });
        }

        if (path != "/auth/v1/user")
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        Interlocked.Increment(ref _userCalls);
        if (Down)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        var token = request.Headers.Authorization?.Parameter;
        if (!request.Headers.Contains("apikey") || token is null || SignedOut.ContainsKey(token))
        {
            return Json(HttpStatusCode.Forbidden, new { code = 403, error_code = "session_not_found" });
        }

        if (UserBodyOverride is not null)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserBodyOverride, Encoding.UTF8, "application/json") };
        }

        // The real Auth checks the signature too; the API has already done that before asking.
        var jwt = new JsonWebToken(token);
        return jwt.TryGetPayloadValue<string>("role", out var role) && role == "authenticated"
            ? Json(HttpStatusCode.OK, new { id = jwt.Subject, aud = "authenticated", role })
            : Json(HttpStatusCode.Forbidden, new { code = 403, error_code = "bad_jwt" });
    }

    private static Dictionary<string, string> PublicJwk()
    {
        var p = TestTokens.ProjectKey.ECDsa.ExportParameters(includePrivateParameters: false);
        return new()
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["alg"] = "ES256",
            ["use"] = "sig",
            ["kid"] = TestTokens.ProjectKey.KeyId,
            ["x"] = Base64UrlEncoder.Encode(p.Q.X),
            ["y"] = Base64UrlEncoder.Encode(p.Q.Y),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(FakeSupabaseAuth auth) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(auth.Respond(request));
    }
}

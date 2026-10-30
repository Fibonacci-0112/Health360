using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Health360.UI.Auth;

/// <summary>A signed-in Supabase session. Never log it: <see cref="ToString"/> leaves the tokens out.</summary>
public sealed record AuthSession(string UserId, string? Email, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"AuthSession {{ UserId = {UserId}, ExpiresAt = {ExpiresAt:O} }}";
}

public enum AuthErrorKind
{
    /// <summary>Auth answered and said no: a wrong or expired code, a dead refresh token. Retrying won't help.</summary>
    Rejected,

    /// <summary>Too many requests (for example, codes sent to one address). Waiting will help.</summary>
    RateLimited,

    /// <summary>Auth couldn't be reached or failed (offline, timeout, 5xx, a page that isn't Auth's JSON).</summary>
    Unavailable,

    /// <summary>This build has no Supabase URL or publishable key.</summary>
    NotConfigured,
}

/// <summary>A failed Auth request. <see cref="Code"/> is Auth's <c>error_code</c>, such as <c>otp_expired</c>.</summary>
public sealed class AuthException : Exception
{
    public AuthException()
    {
    }

    public AuthException(string message)
        : base(message)
    {
    }

    public AuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AuthException(AuthErrorKind kind, string? code, string message, Exception? innerException = null, int? status = null)
        : base(message, innerException)
    {
        Kind = kind;
        Code = code;
        Status = status;
    }

    public AuthErrorKind Kind { get; } = AuthErrorKind.Unavailable;

    public string? Code { get; }

    /// <summary>The HTTP status, when Auth answered.</summary>
    public int? Status { get; }
}

/// <summary>
/// The Supabase Auth (GoTrue) calls the app makes: sign-in by emailed 6-digit code (PLAN.md §2.1), token
/// refresh and sign-out. The same flow creates the account the first time. Every request carries the
/// publishable key; nothing here logs a token, code or email address.
/// </summary>
/// <param name="http">A client whose base address is the Supabase project URL.</param>
/// <param name="apiKey">The publishable (anon) key. Never the service-role key.</param>
/// <param name="clock">For the session's expiry when Auth sends only <c>expires_in</c>.</param>
public sealed class SupabaseAuthClient(HttpClient http, string apiKey, TimeProvider clock)
{
    /// <summary>Emails a sign-in code, creating the account if there is none.</summary>
    public async Task SendCodeAsync(string email, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["email"] = email, ["create_user"] = true };
        using var response = await SendAsync(HttpMethod.Post, "auth/v1/otp", body, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exchanges the emailed code for a session.</summary>
    public async Task<AuthSession> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["type"] = "email", ["email"] = email, ["token"] = code };
        using var response = await SendAsync(HttpMethod.Post, "auth/v1/verify", body, null, cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A new session from the refresh token. Auth rotates refresh tokens, so the old one must be replaced
    /// by the returned one straight away.
    /// </summary>
    public async Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject { ["refresh_token"] = refreshToken };
        using var response = await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token", body, null, cancellationToken).ConfigureAwait(false);
        return await ReadSessionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends this device's session on the server (<c>scope=local</c>: the user's other devices stay signed in).
    /// A session Auth no longer knows counts as signed out.
    /// </summary>
    public async Task SignOutAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "auth/v1/logout?scope=local", null, accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthException error) when (error.Status is 401 or 403 or 404)
        {
            // Already gone (expired or revoked), as supabase-js treats it.
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject? body, string? accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(apiKey) || http.BaseAddress is null)
        {
            throw new AuthException(AuthErrorKind.NotConfigured, null, "This build has no Supabase URL or publishable key.");
        }

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("apikey", apiKey);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new AuthException(AuthErrorKind.Unavailable, null, "Supabase Auth could not be reached.", error);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AuthException(AuthErrorKind.Unavailable, null, "Supabase Auth did not answer in time.", error);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await ErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Auth's error body is <c>{"code":400,"error_code":"otp_expired","msg":"..."}</c> (older versions:
    /// <c>error</c> and <c>error_description</c>). Anything else (a gateway page) is treated as unavailable.
    /// </summary>
    private static async Task<AuthException> ErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        JsonObject? error = null;
        try
        {
            error = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
        }
        catch (JsonException)
        {
        }

        var code = Text(error, "error_code") ?? Text(error, "error");
        var message = Text(error, "msg") ?? Text(error, "message") ?? Text(error, "error_description") ?? $"Supabase Auth answered {status}.";
        var kind = response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => AuthErrorKind.RateLimited,
            HttpStatusCode.RequestTimeout => AuthErrorKind.Unavailable,
            _ when status is >= 400 and < 500 && error is not null => AuthErrorKind.Rejected,
            _ => AuthErrorKind.Unavailable,
        };
        return new AuthException(kind, code, message, null, status);
    }

    private async Task<AuthSession> ReadSessionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        JsonObject? body = null;
        try
        {
            body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;
        }
        catch (JsonException)
        {
        }

        var accessToken = Text(body, "access_token");
        var refreshToken = Text(body, "refresh_token");
        var userId = Text(body?["user"] as JsonObject, "id");
        if (accessToken is null || refreshToken is null || userId is null)
        {
            // A captive portal or proxy page served with 200.
            throw new AuthException(AuthErrorKind.Unavailable, null, "Supabase Auth returned something other than a session.");
        }

        DateTimeOffset expiresAt;
        if (body!["expires_at"] is JsonValue at && at.TryGetValue<long>(out var unix))
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        else if (body["expires_in"] is JsonValue expiresIn && expiresIn.TryGetValue<long>(out var seconds))
        {
            expiresAt = clock.GetUtcNow().AddSeconds(seconds);
        }
        else
        {
            // Unknown expiry: refresh on next use rather than trust it.
            expiresAt = clock.GetUtcNow();
        }

        return new AuthSession(userId, Text(body["user"] as JsonObject, "email"), accessToken, refreshToken, expiresAt);
    }

    private static string? Text(JsonObject? obj, string key) =>
        obj?[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;
}

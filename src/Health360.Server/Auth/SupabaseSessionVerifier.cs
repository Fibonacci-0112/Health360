using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Health360.Server.Auth;

public enum SessionStatus
{
    /// <summary>Auth knows the token and it belongs to the expected user.</summary>
    Active,

    /// <summary>Signed out, deleted, banned, or a token Auth doesn't accept.</summary>
    Rejected,

    /// <summary>Auth couldn't be asked. The request fails with 503, not 401, so the app keeps its session.</summary>
    Unavailable,
}

/// <summary>
/// A valid signature only proves Auth issued the token at some point. Asking Auth for the user
/// also rejects tokens of signed-out sessions
/// and deleted users before they expire. Confirmed sessions are cached briefly, so a sign-out takes
/// effect within <see cref="SupabaseAuthOptions.SessionCacheDuration"/>.
/// </summary>
public sealed partial class SupabaseSessionVerifier(
    HttpClient http,
    IMemoryCache cache,
    TimeProvider time,
    IOptions<SupabaseAuthOptions> options,
    ILogger<SupabaseSessionVerifier> logger)
{
    public const string HttpClientName = "supabase-auth";

    public async Task<SessionStatus> VerifyAsync(string token, Guid userId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        // Key on a hash, so the cache never holds a usable token.
        var key = "supabase-session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        if (cache.TryGetValue(key, out Guid cached) && cached == userId)
        {
            return SessionStatus.Active;
        }

        var status = await AskAuthAsync(token, userId, cancellationToken).ConfigureAwait(false);
        if (status == SessionStatus.Active)
        {
            var until = time.GetUtcNow() + options.Value.SessionCacheDuration;
            cache.Set(key, userId, until < expiresAt ? until : expiresAt);
        }

        return status;
    }

    private async Task<SessionStatus> AskAuthAsync(string token, Guid userId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.Value.AuthUrl, "user"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("apikey", options.Value.ApiKey);

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return SessionStatus.Rejected;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogAuthStatus(logger, (int)response.StatusCode);
                return SessionStatus.Unavailable;
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var user = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (user.RootElement.ValueKind != JsonValueKind.Object
                || !user.RootElement.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.String)
            {
                // A 2xx that isn't Auth's user object (a proxy page, say): not an answer about this user.
                LogAuthUnexpectedBody(logger, user.RootElement.ValueKind.ToString());
                return SessionStatus.Unavailable;
            }

            return id.TryGetGuid(out var authUserId) && authUserId == userId ? SessionStatus.Active : SessionStatus.Rejected;
        }
        // A TaskCanceledException without our token cancelled is HttpClient's timeout.
        catch (Exception e) when (e is HttpRequestException or JsonException
            || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogAuthUnreachable(logger, e.GetType().Name);
            return SessionStatus.Unavailable;
        }
    }

    // Status codes and exception types only: never the token or the user.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Supabase Auth answered {StatusCode} when verifying a session")]
    private static partial void LogAuthStatus(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Supabase Auth answered a session check with an unexpected body ({RootKind})")]
    private static partial void LogAuthUnexpectedBody(ILogger logger, string rootKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Supabase Auth could not be reached to verify a session ({ExceptionType})")]
    private static partial void LogAuthUnreachable(ILogger logger, string exceptionType);
}

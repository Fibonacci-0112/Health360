using System.Text.Json;
using System.Text.Json.Nodes;
using Health360.UI.Auth;

namespace Health360.Maui.Data;

/// <summary>
/// Keeps the Supabase session in platform secure storage: Android Keystore-encrypted preferences
/// (excluded from backups: <c>allowBackup="false"</c>), and on Windows a DPAPI-encrypted file in the
/// app's data folder (unpackaged apps).
/// </summary>
public sealed class SecureSessionStore : ISessionStore
{
    private const string Key = "health360.session";

    public async Task<AuthSession?> LoadAsync()
    {
        try
        {
            var text = await SecureStorage.Default.GetAsync(Key).ConfigureAwait(false);
            return text is null ? null : Parse(text);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException
            or System.Security.Cryptography.CryptographicException or IOException)
        {
            // Unreadable (keys changed, data corrupted): sign in again rather than fail to start.
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    public Task SaveAsync(AuthSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var json = new JsonObject
        {
            ["user_id"] = session.UserId,
            ["email"] = session.Email,
            ["access_token"] = session.AccessToken,
            ["refresh_token"] = session.RefreshToken,
            ["expires_at"] = session.ExpiresAt.ToUnixTimeSeconds(),
        };
        return SecureStorage.Default.SetAsync(Key, json.ToJsonString());
    }

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }

    private static AuthSession? Parse(string text)
    {
        if (JsonNode.Parse(text) is not JsonObject json)
        {
            return null;
        }

        var userId = (string?)json["user_id"];
        var accessToken = (string?)json["access_token"];
        var refreshToken = (string?)json["refresh_token"];
        var expiresAt = (long?)json["expires_at"];
        if (userId is null || accessToken is null || refreshToken is null || expiresAt is null)
        {
            return null;
        }

        return new AuthSession(userId, (string?)json["email"], accessToken, refreshToken, DateTimeOffset.FromUnixTimeSeconds(expiresAt.Value));
    }
}

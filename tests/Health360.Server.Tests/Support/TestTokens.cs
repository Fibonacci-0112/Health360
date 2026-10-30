using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Health360.Server.Tests.Support;

/// <summary>Mints tokens shaped like Supabase Auth's, signed with test keys only.</summary>
public static class TestTokens
{
    public const string SupabaseUrl = "http://supabase.test";
    public const string Issuer = SupabaseUrl + "/auth/v1";
    public const string ApiKey = "test-publishable-key";
    public const string JwtSecret = "test-only-hs256-secret-with-at-least-32-characters";

    /// <summary>The project's asymmetric signing key, published by <see cref="FakeSupabaseAuth"/> as JWKS.</summary>
    public static readonly ECDsaSecurityKey ProjectKey = new(ECDsa.Create(ECCurve.NamedCurves.nistP256)) { KeyId = "test-es256" };

    public static SigningCredentials Es256 => new(ProjectKey, SecurityAlgorithms.EcdsaSha256);

    public static SigningCredentials Hs256 => Hmac(JwtSecret);

    public static SigningCredentials Hmac(string secret) =>
        new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256);

    /// <summary>A signed-in user's access token, as Auth issues it.</summary>
    public static string User(
        Guid userId,
        SigningCredentials? credentials = null,
        DateTimeOffset? expires = null,
        string issuer = Issuer,
        string audience = "authenticated",
        string role = "authenticated",
        string? subject = null)
    {
        var now = DateTimeOffset.UtcNow;
        return Create(
            new Dictionary<string, object>
            {
                ["iss"] = issuer,
                ["aud"] = audience,
                ["sub"] = subject ?? userId.ToString(),
                ["role"] = role,
                ["email"] = "user@example.com",
                ["session_id"] = Guid.NewGuid().ToString(),
                ["aal"] = "aal1",
            },
            expires ?? now.AddHours(1),
            credentials ?? Es256);
    }

    /// <summary>The project's public anon key: a valid signature, but no user (legacy HS256 shape).</summary>
    public static string AnonKey(SigningCredentials? credentials = null) => Create(
        new Dictionary<string, object> { ["iss"] = "supabase", ["ref"] = "test", ["role"] = "anon" },
        DateTimeOffset.UtcNow.AddYears(10),
        credentials ?? Hs256);

    public static string ServiceRoleKey(SigningCredentials? credentials = null) => Create(
        new Dictionary<string, object> { ["iss"] = "supabase", ["ref"] = "test", ["role"] = "service_role" },
        DateTimeOffset.UtcNow.AddYears(10),
        credentials ?? Hs256);

    /// <summary>A token with <c>alg: none</c>.</summary>
    public static string AlgNone(Guid userId) => new JsonWebTokenHandler().CreateToken(
        $$"""{"iss":"{{Issuer}}","aud":"authenticated","sub":"{{userId}}","role":"authenticated","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}""");

    private static string Create(Dictionary<string, object> claims, DateTimeOffset expires, SigningCredentials credentials)
    {
        var issued = expires.AddHours(-1) < DateTimeOffset.UtcNow ? expires.AddHours(-1) : DateTimeOffset.UtcNow;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            IssuedAt = issued.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = credentials,
        });
    }
}

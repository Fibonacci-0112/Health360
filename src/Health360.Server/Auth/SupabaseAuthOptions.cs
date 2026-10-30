using System.ComponentModel.DataAnnotations;

namespace Health360.Server.Auth;

/// <summary>The <c>Supabase</c> configuration section.</summary>
public sealed class SupabaseAuthOptions
{
    public const string Section = "Supabase";

    /// <summary>Project URL, such as <c>https://abc.supabase.co</c> or <c>http://127.0.0.1:54321</c>.</summary>
    [Required]
    public Uri? Url { get; set; }

    /// <summary>
    /// The publishable (anon) key. Auth needs it as the <c>apikey</c> header when the API asks who a
    /// token belongs to. It is public, and is never accepted as a user's token.
    /// </summary>
    [Required]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Legacy HS256 JWT secret, for projects that still sign with one (the local <c>supabase start</c>
    /// stack does by default). Leave it unset when the project uses asymmetric signing keys: tokens
    /// are then checked against the project's JWKS only, and HS256 tokens are rejected.
    /// </summary>
    public string? JwtSecret { get; set; }

    /// <summary>How long a confirmed session is trusted before Auth is asked again.</summary>
    public TimeSpan SessionCacheDuration { get; set; } = TimeSpan.FromSeconds(60);

    internal Uri AuthUrl => new(Url!, "auth/v1/");

    /// <summary>The <c>iss</c> claim Supabase Auth puts in its tokens.</summary>
    internal string Issuer => AuthUrl.AbsoluteUri.TrimEnd('/');
}

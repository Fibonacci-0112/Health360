using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Health360.Server.Auth;

/// <summary>
/// Every endpoint requires a Supabase user unless it opts out with <c>AllowAnonymous</c>. A request
/// gets through only if its bearer token
/// <list type="number">
/// <item>is signed by the project's Auth (JWKS; or the legacy HS256 secret, when configured),</item>
/// <item>has the project's issuer, the <c>authenticated</c> audience and an unexpired <c>exp</c>,</item>
/// <item>has <c>role = authenticated</c>, a user id in <c>sub</c> and a session id in <c>session_id</c> (this rejects the anon and service-role keys), and</item>
/// <item>belongs to a session Auth still accepts (<see cref="SupabaseSessionVerifier"/>).</item>
/// </list>
/// </summary>
public static class SupabaseAuthentication
{
    public const string AuthenticatedRole = "authenticated";

    private const string AuthUnavailableKey = "Health360.AuthUnavailable";

    public static IServiceCollection AddSupabaseAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SupabaseAuthOptions>()
            .Bind(configuration.GetSection(SupabaseAuthOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient<SupabaseSessionVerifier>(SupabaseSessionVerifier.HttpClientName)
            .ConfigureHttpClient(http => http.Timeout = TimeSpan.FromSeconds(10));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<SupabaseAuthOptions>, IHttpClientFactory>((jwt, supabaseOptions, httpFactory) =>
                Configure(jwt, supabaseOptions.Value, httpFactory));

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim("role", AuthenticatedRole)
                .Build());

        return services;
    }

    private static void Configure(JwtBearerOptions jwt, SupabaseAuthOptions supabase, IHttpClientFactory httpFactory)
    {
        var jwksUrl = new Uri(supabase.AuthUrl, ".well-known/jwks.json");
        var https = jwksUrl.Scheme == Uri.UriSchemeHttps;

        jwt.MapInboundClaims = false;
        jwt.RequireHttpsMetadata = https;
        jwt.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            jwksUrl.AbsoluteUri,
            new SupabaseJwksRetriever(supabase.Issuer),
            new HttpDocumentRetriever(httpFactory.CreateClient(SupabaseSessionVerifier.HttpClientName)) { RequireHttps = https });

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = supabase.Issuer,
            ValidAudience = AuthenticatedRole,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "role",
            // Asymmetric keys come from the JWKS. Accept HS256 only when a secret is configured, so a
            // token "signed" with the public key as an HMAC secret can never pass.
            ValidAlgorithms = string.IsNullOrEmpty(supabase.JwtSecret)
                ? [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256]
                : [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256, SecurityAlgorithms.HmacSha256],
        };
        if (!string.IsNullOrEmpty(supabase.JwtSecret))
        {
            parameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(supabase.JwtSecret));
        }

        jwt.TokenValidationParameters = parameters;
        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = OnMessageReceivedAsync,
            OnTokenValidated = OnTokenValidatedAsync,
            OnChallenge = OnChallengeAsync,
        };
    }

    /// <summary>
    /// Loads the signing keys before the token is validated, so an Auth outage answers 503 like a
    /// failed session check, instead of failing as if the token were bad. Once loaded, keys are cached
    /// and a failed refresh keeps the old ones.
    /// </summary>
    private static async Task OnMessageReceivedAsync(MessageReceivedContext context)
    {
        if (context.Options.ConfigurationManager is not { } keys
            || !context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await keys.GetConfigurationAsync(context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The configuration manager wraps every retrieval failure (network, status, bad JSON) in this.
            context.HttpContext.Items[AuthUnavailableKey] = true;
            context.Fail("Supabase Auth's signing keys are unavailable.");
        }
    }

    private static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        // Auth checks the session only for tokens that name one (session_id), and every token it
        // issues to a signed-in user does, so a token without one is not a user session.
        if (context.SecurityToken is not JsonWebToken token
            || !token.TryGetPayloadValue<string>("role", out var role) || role != AuthenticatedRole
            || !Guid.TryParse(token.Subject, out var userId) || userId == Guid.Empty
            || !token.TryGetPayloadValue<string>("session_id", out var sessionId)
            || !Guid.TryParse(sessionId, out var session) || session == Guid.Empty)
        {
            context.Fail("Not a signed-in user's token.");
            return;
        }

        var verifier = context.HttpContext.RequestServices.GetRequiredService<SupabaseSessionVerifier>();
        switch (await verifier.VerifyAsync(token.EncodedToken, userId, token.ValidTo, context.HttpContext.RequestAborted).ConfigureAwait(false))
        {
            case SessionStatus.Active:
                context.HttpContext.Features.Set(new SignedInUser(userId, Base64UrlEncoder.Decode(token.EncodedPayload)));
                break;
            case SessionStatus.Unavailable:
                context.HttpContext.Items[AuthUnavailableKey] = true;
                context.Fail("Supabase Auth is unavailable.");
                break;
            default:
                context.Fail("The session is no longer active.");
                break;
        }
    }

    private static Task OnChallengeAsync(JwtBearerChallengeContext context)
    {
        if (!context.HttpContext.Items.ContainsKey(AuthUnavailableKey))
        {
            return Task.CompletedTask;
        }

        // Not the client's fault: a 401 here could make the app treat the user as signed out.
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        return Task.CompletedTask;
    }
}

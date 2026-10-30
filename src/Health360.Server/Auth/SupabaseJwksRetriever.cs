using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Health360.Server.Auth;

/// <summary>
/// Loads Supabase Auth's signing keys from <c>/auth/v1/.well-known/jwks.json</c>. JwtBearer expects
/// an OpenID configuration document; Supabase publishes the key set on its own, so this wraps it in
/// one. The configuration manager refreshes it when a token names a key it hasn't seen.
/// </summary>
internal sealed class SupabaseJwksRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var config = new OpenIdConnectConfiguration { Issuer = issuer, JwksUri = address };
        foreach (var key in new JsonWebKeySet(json).GetSigningKeys())
        {
            config.SigningKeys.Add(key);
        }

        return config;
    }
}

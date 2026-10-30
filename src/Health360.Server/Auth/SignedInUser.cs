using System.Reflection;

namespace Health360.Server.Auth;

/// <summary>
/// The caller, after the token's signature, issuer, audience and expiry were checked and Auth
/// confirmed the session. <see cref="ClaimsJson"/> is the token's validated payload; it becomes
/// <c>request.jwt.claims</c> in the database, which is what <c>auth.uid()</c> and RLS read.
/// </summary>
public sealed record SignedInUser(Guid UserId, string ClaimsJson)
{
    /// <summary>
    /// Lets endpoints take a <see cref="SignedInUser"/> parameter. There is none on anonymous
    /// endpoints, so binding fails (400) instead of handing out an empty user.
    /// </summary>
    public static ValueTask<SignedInUser?> BindAsync(HttpContext context, ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(context.Features.Get<SignedInUser>());
    }
}

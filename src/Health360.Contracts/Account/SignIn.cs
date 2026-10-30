using System.Text.RegularExpressions;

namespace Health360.Contracts.Account;

/// <summary>What the sign-in form accepts before it calls Auth (the prototype's zod checks).</summary>
public static partial class SignInRules
{
    /// <summary>A plausible email address, by the same pattern as zod's <c>z.email()</c>.</summary>
    public static bool IsEmail(string? value) => value is not null && EmailRegex().IsMatch(value.Trim());

    /// <summary>The emailed code: exactly 6 digits (Supabase's default code length).</summary>
    public static bool IsCode(string? value) => value is not null && CodeRegex().IsMatch(value.Trim());

    [GeneratedRegex(@"^(?!\.)(?!.*\.\.)([A-Za-z0-9_'+\-\.]*)[A-Za-z0-9_+-]@([A-Za-z0-9][A-Za-z0-9\-]*\.)+[A-Za-z]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex("^[0-9]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodeRegex();
}

using System.Globalization;
using FluentValidation;
using Health360.Domain.Units;

namespace Health360.Contracts.Profiles;

public static class SexForCalcs
{
    public const string Female = "female";
    public const string Male = "male";

    public static IReadOnlyList<string> All { get; } = [Female, Male];
}

public static class ActivityLevels
{
    /// <summary>The stored codes, from <see cref="Domain.Physiology.ActivityLevel"/> (each maps to a PAL there).</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. Enum.GetValues<Domain.Physiology.ActivityLevel>().Select(Domain.Physiology.ActivityLevels.Code)];
}

/// <summary>
/// What onboarding collects, matching the <c>profiles</c> columns it writes. Ports the prototype's
/// <c>onboardingSchema</c>; the checks also match the table's constraints, so a valid form is never
/// rejected into <c>failed_uploads</c>.
/// </summary>
/// <param name="DisplayName">Trimmed before saving.</param>
/// <param name="BirthDate">The user's local calendar date, <c>YYYY-MM-DD</c>.</param>
/// <param name="SexForCalcs">One of <see cref="Profiles.SexForCalcs.All"/>, or null when not given.</param>
/// <param name="Height">As typed: cm, or total inches for feet and inches.</param>
/// <param name="HeightUnit"><c>cm</c> or <c>in</c>; set exactly when <paramref name="Height"/> is.</param>
/// <param name="Timezone">IANA time zone, such as <c>Europe/London</c>.</param>
/// <param name="Currency">ISO 4217 code.</param>
/// <param name="ActivityLevel">One of <see cref="ActivityLevels.All"/>, or null when not given.</param>
/// <param name="UnitPrefs">Every measurement type's unit (PLAN.md §2.6).</param>
public sealed record OnboardingForm(
    string DisplayName,
    string? BirthDate,
    string? SexForCalcs,
    double? Height,
    string? HeightUnit,
    string Timezone,
    string Currency,
    string? ActivityLevel,
    UnitPrefs UnitPrefs) : IProfileDetails;

/// <summary>The details calculations use, which onboarding collects and Settings edits.</summary>
public interface IProfileDetails
{
    /// <summary>The user's local calendar date, <c>YYYY-MM-DD</c>, or null when not given.</summary>
    string? BirthDate { get; }

    /// <summary>One of <see cref="Profiles.SexForCalcs.All"/>, or null when not given.</summary>
    string? SexForCalcs { get; }

    /// <summary>As typed: cm, or total inches for feet and inches.</summary>
    double? Height { get; }

    /// <summary><c>cm</c> or <c>in</c>; set exactly when <see cref="Height"/> is.</summary>
    string? HeightUnit { get; }

    /// <summary>One of <see cref="ActivityLevels.All"/>, or null when not given.</summary>
    string? ActivityLevel { get; }
}

/// <summary>
/// Profile details edited after onboarding (Settings). Null clears a value. <see cref="HeightCm"/> is the
/// canonical height for the local row; the server recomputes it from <see cref="Height"/> and <see cref="HeightUnit"/>.
/// </summary>
public sealed record ProfileDetailsForm(
    string? BirthDate,
    string? SexForCalcs,
    double? Height,
    string? HeightUnit,
    double? HeightCm,
    string? ActivityLevel) : IProfileDetails;

/// <summary>The checks on <see cref="IProfileDetails"/>, matching the <c>profiles</c> table's constraints.</summary>
public sealed class ProfileDetailsValidator : AbstractValidator<IProfileDetails>
{
    public ProfileDetailsValidator()
    {
        RuleFor(x => x.BirthDate).Must(OnboardingFormValidator.IsLocalDate!).When(x => x.BirthDate is not null)
            .WithMessage("'Birth Date' must be a date like 1990-04-02.");
        RuleFor(x => x.SexForCalcs).Must(SexForCalcs.All.Contains!).When(x => x.SexForCalcs is not null);
        RuleFor(x => x.Height).Must(h => double.IsFinite(h!.Value) && h > 0 && h <= 300).When(x => x.Height is not null)
            .WithMessage("'Height' must be more than 0 and at most 300.");
        RuleFor(x => x.HeightUnit).Must(unit => unit is "cm" or "in").When(x => x.HeightUnit is not null);
        RuleFor(x => x.HeightUnit).Must((form, unit) => (form.Height is null) == (unit is null))
            .WithMessage("'Height' and 'Height Unit' must be given together.");
        RuleFor(x => x.ActivityLevel).Must(ActivityLevels.All.Contains!).When(x => x.ActivityLevel is not null);
    }
}

public sealed class ProfileDetailsFormValidator : AbstractValidator<ProfileDetailsForm>
{
    public ProfileDetailsFormValidator()
    {
        Include(new ProfileDetailsValidator());
        RuleFor(x => x.HeightCm).Must((form, cm) => (form.Height is null) == (cm is null))
            .WithMessage("'Height Cm' must be given exactly when 'Height' is.");
    }
}

public sealed class OnboardingFormValidator : AbstractValidator<OnboardingForm>
{
    public OnboardingFormValidator()
    {
        RuleFor(x => x.DisplayName).Must(name => name?.Trim().Length is >= 1 and <= 80)
            .WithMessage("'Display Name' must be 1 to 80 characters.");
        Include(new ProfileDetailsValidator());
        RuleFor(x => x.Timezone).NotEmpty();
        RuleFor(x => x.Currency).Must(c => c is { Length: 3 } && c.All(char.IsAsciiLetterUpper))
            .WithMessage("'Currency' must be a three-letter ISO code.");
        RuleFor(x => x.UnitPrefs).NotNull();
    }

    /// <summary>A real calendar date written as <c>YYYY-MM-DD</c>, like zod's <c>z.iso.date()</c>.</summary>
    public static bool IsLocalDate(string value) =>
        value.Length == 10 && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>A height ready to store: the value as entered, its unit, and the canonical cm (PLAN.md §2.6).</summary>
public sealed record HeightValue(double Height, string HeightUnit, double HeightCm);

/// <summary>
/// The height fields as typed. Ports the prototype's <c>height.ts</c>: numbers are read
/// as JavaScript's <c>Number</c> reads them after a decimal comma becomes a point.
/// </summary>
public sealed record HeightEntry(string? Cm = null, string? Feet = null, string? Inches = null)
{
    /// <summary>Plausible human heights, as in the prototype.</summary>
    public const double MinCm = 50;

    public const double MaxCm = 272;

    /// <summary>
    /// The stored trio, or null when nothing was entered. Feet and inches are stored as total inches, so
    /// the entry can be shown again exactly. Throws <see cref="FormatException"/> on an implausible value.
    /// </summary>
    public HeightValue? Parse(HeightUnit preference)
    {
        if (preference == Domain.Units.HeightUnit.Centimetre)
        {
            if (string.IsNullOrWhiteSpace(Cm))
            {
                return null;
            }

            var cm = Number(ReplaceFirstComma(Cm));
            Check(cm);
            return new(cm, "cm", cm);
        }

        if (string.IsNullOrWhiteSpace(Feet) && string.IsNullOrWhiteSpace(Inches))
        {
            return null;
        }

        var ft = string.IsNullOrEmpty(Feet) ? 0 : Number(Feet);
        var inches = Number(ReplaceFirstComma(string.IsNullOrEmpty(Inches) ? "0" : Inches));
        if (!double.IsInteger(ft) || inches < 0 || inches >= 12)
        {
            throw new FormatException("Use whole feet and 0–11 inches.");
        }

        var heightCm = Compound.FeetInchesToCm(new FeetInches(ft, inches));
        Check(heightCm);
        var total = (ft * 12) + inches;
        return new(total, "in", QuantityKinds.ToCanonical(QuantityKind.Height, total, Unit.Inch));
    }

    private static void Check(double cm)
    {
        if (!double.IsFinite(cm) || cm < MinCm || cm > MaxCm)
        {
            throw new FormatException("That height looks wrong.");
        }
    }

    private static string ReplaceFirstComma(string text)
    {
        var i = text.IndexOf(',', StringComparison.Ordinal);
        return i < 0 ? text : string.Concat(text.AsSpan(0, i), ".", text.AsSpan(i + 1));
    }

    /// <summary>Like JavaScript's <c>Number(text)</c> for decimal text: blank is 0, anything unreadable is NaN.</summary>
    private static double Number(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return double.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }
}

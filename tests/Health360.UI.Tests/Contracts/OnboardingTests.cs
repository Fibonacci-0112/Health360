using FsCheck;
using FsCheck.Fluent;
using Health360.Contracts.Account;
using Health360.Contracts.Profiles;
using Health360.Domain.Units;

namespace Health360.UI.Tests.Contracts;

public class OnboardingTests
{
    private static readonly OnboardingFormValidator Validator = new();

    private static OnboardingForm Valid() =>
        new(" Sam ", "1990-04-02", "female", 65, "in", "America/Chicago", "USD", "moderate", UnitPrefs.DefaultFor("en-US"));

    [Fact]
    public void AcceptsATypicalProfile() => Assert.True(Validator.Validate(Valid()).IsValid);

    [Fact]
    public void AcceptsOnlyTheRequiredParts() =>
        Assert.True(Validator.Validate(Valid() with { BirthDate = null, SexForCalcs = null, Height = null, HeightUnit = null, ActivityLevel = null }).IsValid);

    public static TheoryData<string, OnboardingForm> Invalid() => new()
    {
        { "DisplayName", Valid() with { DisplayName = "   " } },
        { "DisplayName", Valid() with { DisplayName = new string('a', 81) } },
        { "BirthDate", Valid() with { BirthDate = "02/04/1990" } },
        { "BirthDate", Valid() with { BirthDate = "1990-02-30" } },
        { "BirthDate", Valid() with { BirthDate = "1990-4-2" } },
        { "SexForCalcs", Valid() with { SexForCalcs = "other" } },
        { "Height", Valid() with { Height = 0 } },
        { "Height", Valid() with { Height = 301 } },
        { "Height", Valid() with { Height = double.NaN } },
        { "HeightUnit", Valid() with { HeightUnit = "ft" } },
        // profiles_height_pair: both or neither.
        { "HeightUnit", Valid() with { HeightUnit = null } },
        { "HeightUnit", Valid() with { Height = null } },
        { "Timezone", Valid() with { Timezone = "" } },
        // profiles.currency check: ^[A-Z]{3}$.
        { "Currency", Valid() with { Currency = "usd" } },
        { "Currency", Valid() with { Currency = "US" } },
        { "ActivityLevel", Valid() with { ActivityLevel = "extreme" } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void RejectsWhatTheServerWould(string property, OnboardingForm form)
    {
        var result = Validator.Validate(form);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == property);
    }

    [Fact]
    public void LeapDays() =>
        Assert.Equal((true, false), (OnboardingFormValidator.IsLocalDate("2024-02-29"), OnboardingFormValidator.IsLocalDate("2023-02-29")));

    [Fact]
    public void NoHeightEnteredIsNull()
    {
        Assert.Null(new HeightEntry(Cm: "  ").Parse(HeightUnit.Centimetre));
        Assert.Null(new HeightEntry(Feet: "", Inches: " ").Parse(HeightUnit.FeetInches));
    }

    [Theory]
    [InlineData("180", 180)]
    [InlineData("172,5", 172.5)]
    [InlineData(" 165.25 ", 165.25)]
    public void CentimetresAreStoredAsTyped(string typed, double cm) =>
        Assert.Equal(new HeightValue(cm, "cm", cm), new HeightEntry(Cm: typed).Parse(HeightUnit.Centimetre));

    [Fact]
    public void FeetAndInchesAreStoredAsTotalInches()
    {
        var height = new HeightEntry(Feet: "5", Inches: "10").Parse(HeightUnit.FeetInches)!;

        Assert.Equal((70, "in"), (height.Height, height.HeightUnit));
        // The same canonical value the server trigger computes (length_to_cm), via Domain's exact factor.
        Assert.Equal(QuantityKinds.ToCanonical(QuantityKind.Height, 70, Unit.Inch), height.HeightCm);
        Assert.Equal(177.8, height.HeightCm, 9);
    }

    [Fact]
    public void InchesAloneAndHalfInchesWork()
    {
        Assert.Equal(64.5, new HeightEntry(Feet: "5", Inches: "4,5").Parse(HeightUnit.FeetInches)!.Height);
        Assert.Equal(60, new HeightEntry(Feet: "5").Parse(HeightUnit.FeetInches)!.Height);
    }

    [Theory]
    [InlineData("5.5", "0", "Use whole feet and 0–11 inches.")]
    [InlineData("5", "12", "Use whole feet and 0–11 inches.")]
    [InlineData("5", "-1", "Use whole feet and 0–11 inches.")]
    [InlineData("abc", "1", "Use whole feet and 0–11 inches.")]
    [InlineData("5", "abc", "That height looks wrong.")]
    [InlineData("1", "0", "That height looks wrong.")]
    [InlineData("9", "0", "That height looks wrong.")]
    public void ImplausibleFeetAndInchesAreRefused(string feet, string inches, string message) =>
        Assert.Equal(message, Assert.Throws<FormatException>(() => new HeightEntry(Feet: feet, Inches: inches).Parse(HeightUnit.FeetInches)).Message);

    [Theory]
    [InlineData("49.9")]
    [InlineData("272.1")]
    [InlineData("1.8m")]
    [InlineData("Infinity")]
    public void ImplausibleCentimetresAreRefused(string typed) =>
        Assert.Throws<FormatException>(() => new HeightEntry(Cm: typed).Parse(HeightUnit.Centimetre));

    [Fact]
    public void EveryAcceptedHeightPassesTheValidator() =>
        Prop.ForAll(
                Gen.Choose(500, 2720).Select(tenths => tenths / 10.0).ToArbitrary(),
                cm =>
                {
                    var typed = cm.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var height = new HeightEntry(Cm: typed).Parse(HeightUnit.Centimetre)!;
                    return height.Height == cm && Validator.Validate(Valid() with { Height = height.Height, HeightUnit = height.HeightUnit }).IsValid;
                })
            .QuickCheckThrowOnFailure();

    [Theory]
    [InlineData("sam@example.com", true)]
    [InlineData(" sam.o'neil+health@mail.example.co.uk ", true)]
    [InlineData("sam@example", false)]
    [InlineData("sam..x@example.com", false)]
    [InlineData(".sam@example.com", false)]
    [InlineData("sam example.com", false)]
    [InlineData("", false)]
    public void EmailsAreCheckedLikeZod(string email, bool valid) => Assert.Equal(valid, SignInRules.IsEmail(email));

    [Theory]
    [InlineData("123456", true)]
    [InlineData(" 012345 ", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    [InlineData("١٢٣٤٥٦", false)]
    public void CodesAreSixDigits(string code, bool valid) => Assert.Equal(valid, SignInRules.IsCode(code));
}

public class ProfileDetailsTests
{
    private static readonly ProfileDetailsFormValidator Validator = new();

    private static ProfileDetailsForm Valid() => new("1990-04-02", "male", 180, "cm", 180, "active");

    [Fact]
    public void AcceptsDetailsAndAllCleared()
    {
        Assert.True(Validator.Validate(Valid()).IsValid);
        Assert.True(Validator.Validate(new ProfileDetailsForm(null, null, null, null, null, null)).IsValid);
    }

    public static TheoryData<string, ProfileDetailsForm> Invalid() => new()
    {
        { "BirthDate", Valid() with { BirthDate = "1990-02-30" } },
        { "SexForCalcs", Valid() with { SexForCalcs = "other" } },
        { "Height", Valid() with { Height = 301 } },
        { "HeightUnit", Valid() with { HeightUnit = null } },
        { "HeightCm", Valid() with { HeightCm = null } },
        { "ActivityLevel", Valid() with { ActivityLevel = "extreme" } },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void RejectsWhatTheTableWouldReject(string property, ProfileDetailsForm form) =>
        Assert.Contains(Validator.Validate(form).Errors, e => e.PropertyName == property);
}

using AngleSharp.Dom;
using Bunit;
using Health360.Contracts.Profiles;
using Health360.Domain.Units;
using Health360.UI.Data;
using Health360.UI.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

public sealed class OnboardingTests : AppTestContext
{
    private async Task<IRenderedComponent<Routes>> OnboardingAsync(string culture = "en-GB")
    {
        Services.AddSingleton<IDeviceLocale>(new FixedLocale(culture, "Europe/London", "GBP"));
        App.Store.Saved = App.SavedSession();
        await App.Database.SeedProfileAsync(ProfileId, App.Auth.UserId);
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => Assert.Equal("Set up your profile", cut.Find("h1").TextContent), Wait);
        return cut;
    }

    private static IElement Chip(IRenderedComponent<Routes> cut, string group, string label) =>
        cut.FindAll($"[aria-labelledby='{group}'] [role=radio]").Single(b => b.TextContent.Trim() == label);

    private static bool Checked(IElement chip) => chip.GetAttribute("aria-checked") == "true";

    [Fact]
    public async Task UnitsStartFromTheLocaleAndEveryTypeIsListed()
    {
        var cut = await OnboardingAsync();

        Assert.Equal(14, cut.FindAll(".unit-prefs .field").Count);
        Assert.True(Checked(Chip(cut, "onboarding-unit-bodyWeight", "st + lb")));
        Assert.True(Checked(Chip(cut, "onboarding-unit-height", "ft + in")));
        Assert.True(Checked(Chip(cut, "onboarding-unit-load", "kg")));
        Assert.True(Checked(Chip(cut, "onboarding-unit-proteinRatio", "g/lb")));
        Assert.NotNull(cut.Find("input[aria-label=Feet]"));
    }

    [Fact]
    public async Task ChangingAUnitChangesOnlyThatTypeAndItsFollowers()
    {
        var cut = await OnboardingAsync();

        await ClickAsync(() => Chip(cut, "onboarding-unit-bodyWeight", "kg"));
        await ClickAsync(() => Chip(cut, "onboarding-unit-height", "cm"));

        Assert.True(Checked(Chip(cut, "onboarding-unit-bodyWeight", "kg")));
        Assert.True(Checked(Chip(cut, "onboarding-unit-proteinRatio", "g/kg")));
        Assert.True(Checked(Chip(cut, "onboarding-unit-distance", "mi")));
        Assert.NotNull(cut.Find("input#height-cm"));
    }

    [Fact]
    public async Task ContinueNeedsAName()
    {
        var cut = await OnboardingAsync();

        Assert.True(cut.Find("button.primary").HasAttribute("disabled"));
        await TypeAsync(cut, "input#name", "Sam");
        Assert.False(cut.Find("button.primary").HasAttribute("disabled"));
    }

    [Fact]
    public async Task SavesTheProfileAndOpensTheTabs()
    {
        var cut = await OnboardingAsync();

        await TypeAsync(cut, "input#name", " Sam ");
        await ChangeAsync(cut, "input[aria-label=Feet]", "5");
        await ChangeAsync(cut, "input[aria-label=Inches]", "10");
        await ChangeAsync(cut, "input#birth", "1990-04-02");
        await ClickAsync(() => Chip(cut, "sex-label", "Female"));
        await ClickAsync(() => Chip(cut, "activity-label", "Moderate"));
        await ClickAsync(() => Chip(cut, "onboarding-unit-energy", "kJ"));
        await SubmitAsync(cut);

        cut.WaitForAssertion(() => Assert.Equal("Today", cut.Find("h1").TextContent), Wait);
        var row = await App.Database.Db.Get<SavedProfile>(
            "SELECT display_name AS DisplayName, birth_date AS BirthDate, sex_for_calcs AS Sex, height AS Height, height_unit AS HeightUnit, " +
            "height_cm AS HeightCm, timezone AS Timezone, currency AS Currency, activity_level AS Activity, unit_prefs AS UnitPrefs, " +
            "onboarded_at AS OnboardedAt FROM profiles WHERE id = ?", [ProfileId]);
        Assert.Equal(("Sam", "1990-04-02", "female", "moderate"), (row.DisplayName, row.BirthDate, row.Sex, row.Activity));
        Assert.Equal((70.0, "in"), (row.Height, row.HeightUnit));
        Assert.Equal(QuantityKinds.ToCanonical(QuantityKind.Height, 70, Unit.Inch), row.HeightCm);
        Assert.Equal(("Europe/London", "GBP"), (row.Timezone, row.Currency));
        Assert.Equal(UnitPrefs.DefaultFor("en-GB") with { Energy = EnergyUnit.Kilojoule }, UnitPrefsJson.Parse(row.UnitPrefs, UnitPrefs.Metric));
        Assert.Equal("2026-10-30T12:00:00.000Z", row.OnboardedAt);
    }

    [Fact]
    public async Task OptionalChoicesCanBeCleared()
    {
        var cut = await OnboardingAsync();

        await ClickAsync(() => Chip(cut, "sex-label", "Male"));
        await ClickAsync(() => Chip(cut, "sex-label", "Male"));

        Assert.DoesNotContain(cut.FindAll("[aria-labelledby='sex-label'] [role=radio]"), Checked);
    }

    [Theory]
    [InlineData("5.5", "0", "Use whole feet and 0–11 inches.")]
    [InlineData("12", "0", "That height looks wrong.")]
    public async Task AnImplausibleHeightIsNotSaved(string feet, string inches, string message)
    {
        var cut = await OnboardingAsync();
        await TypeAsync(cut, "input#name", "Sam");
        await ChangeAsync(cut, "input[aria-label=Feet]", feet);
        await ChangeAsync(cut, "input[aria-label=Inches]", inches);

        await SubmitAsync(cut);

        Assert.Equal(message, cut.Find("[role=alert]").TextContent);
        Assert.Null((await App.Database.Db.Get<SavedProfile>("SELECT onboarded_at AS OnboardedAt FROM profiles WHERE id = ?", [ProfileId])).OnboardedAt);
    }

    [Fact]
    public async Task ABadBirthDateIsExplained()
    {
        var cut = await OnboardingAsync();
        await TypeAsync(cut, "input#name", "Sam");
        await ChangeAsync(cut, "input#birth", "02/04/1990");

        await SubmitAsync(cut);

        Assert.Equal("Birth date must look like 1990-04-02.", cut.Find("[role=alert]").TextContent);
    }

    [Fact]
    public async Task UsLocaleStartsImperial()
    {
        var cut = await OnboardingAsync("en-US");

        Assert.True(Checked(Chip(cut, "onboarding-unit-bodyWeight", "lb")));
        Assert.True(Checked(Chip(cut, "onboarding-unit-temperature", "°F")));
        Assert.True(Checked(Chip(cut, "onboarding-unit-labValues", "US conventional")));
    }

    public sealed class SavedProfile
    {
        public string? DisplayName { get; set; }

        public string? BirthDate { get; set; }

        public string? Sex { get; set; }

        public double? Height { get; set; }

        public string? HeightUnit { get; set; }

        public double? HeightCm { get; set; }

        public string? Timezone { get; set; }

        public string? Currency { get; set; }

        public string? Activity { get; set; }

        public string? UnitPrefs { get; set; }

        public string? OnboardedAt { get; set; }
    }
}

using System.Text.Json.Nodes;
using Health360.Contracts.Profiles;
using Health360.Domain.Units;
using Health360.LocalDb;
using Health360.UI.Auth;
using Health360.UI.Data;
using Health360.UI.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Health360.UI.Tests.Data;

public sealed class ProfileServiceTests : IAsyncLifetime
{
    private const string ProfileId = "0192f0c0-0000-7000-8000-000000000001";
    private App app = null!;

    public async ValueTask InitializeAsync() => app = await App.StartAsync();

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    private ProfileService Profiles(AuthService auth) =>
        new(app.Database.Db, auth, app.Locale, app.Clock, NullLogger<ProfileService>.Instance);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the watched query.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task SignedOutThereIsNoProfile()
    {
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        using var profiles = Profiles(auth);

        Assert.False(profiles.Loaded);
        Assert.Null(profiles.Profile);
    }

    [Fact]
    public async Task FollowsTheSignedInUsersRowAsItSyncs()
    {
        app.Store.Saved = app.SavedSession();
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        using var profiles = Profiles(auth);

        await Until(() => profiles.Loaded);
        Assert.Null(profiles.Profile);

        // Another user's row (a household member, say) is never taken for the user's own.
        await app.Database.SeedProfileAsync("0192f0c0-0000-7000-8000-000000000009", "someone-else");
        await app.Database.SeedProfileAsync(ProfileId, app.Auth.UserId);
        await Until(() => profiles.Profile is not null);

        Assert.Equal(ProfileId, profiles.Profile!.Id);
        Assert.Null(profiles.Profile.OnboardedAt);
    }

    [Fact]
    public async Task UnitsDefaultToTheLocaleUntilSaved()
    {
        app.Store.Saved = app.SavedSession();
        app.Locale = new FixedLocale("en-GB");
        await app.Database.SeedProfileAsync(ProfileId, app.Auth.UserId);
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        using var profiles = Profiles(auth);
        await Until(() => profiles.Profile is not null);

        Assert.Equal(UnitPrefs.DefaultFor("en-GB"), profiles.UnitPrefs);

        var chosen = UnitPrefs.DefaultFor("en-GB") with { Energy = EnergyUnit.Kilojoule };
        await profiles.SetUnitPrefsAsync(chosen);
        await Until(() => profiles.UnitPrefs == chosen);
    }

    [Fact]
    public async Task OnboardingIsSavedLocallyAndQueuedForUpload()
    {
        app.Store.Saved = app.SavedSession();
        await app.Database.SeedProfileAsync(ProfileId, app.Auth.UserId);
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        using var profiles = Profiles(auth);
        await Until(() => profiles.Profile is not null);
        var form = new OnboardingForm(" Sam ", "1990-04-02", "female", 70, "in", "America/Chicago", "USD", "moderate", UnitPrefs.UsCustomary);

        await profiles.CompleteOnboardingAsync(form, 177.8);
        await Until(() => profiles.Profile?.OnboardedAt is not null);

        Assert.Equal("2026-10-30T12:00:00.000Z", profiles.Profile!.OnboardedAt);
        Assert.Equal(("Sam", 70.0, "in"), (profiles.Profile.DisplayName, profiles.Profile.Height, profiles.Profile.HeightUnit));
        // The queued write uploads unit_prefs as a JSON object, not a string (SERVER_COLUMN_TYPES).
        var tx = await app.Database.Db.GetNextCrudTransaction();
        var op = Assert.Single(tx!.Crud);
        Assert.Equal(("profiles", ProfileId), (op.Table, op.Id));
        var queued = await app.Database.Db.Get<QueuedRow>("SELECT data AS Data FROM ps_crud WHERE id = ?", [op.ClientId]);
        var row = Uploads.ToServerRow("profiles", (JsonObject)JsonNode.Parse(queued.Data)!["data"]!);
        Assert.Equal("lb", (string?)row["unit_prefs"]!["bodyWeight"]);
        Assert.Equal(("Sam", "America/Chicago", 177.8), ((string?)row["display_name"], (string?)row["timezone"], (double?)row["height_cm"]));
    }

    [Fact]
    public async Task SavingWithoutTheProfileRowFails()
    {
        app.Store.Saved = app.SavedSession();
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        using var profiles = Profiles(auth);
        await Until(() => profiles.Loaded);

        await Assert.ThrowsAsync<InvalidOperationException>(() => profiles.SetUnitPrefsAsync(UnitPrefs.Metric));
    }

    [Fact]
    public async Task SigningOutStopsShowingTheProfile()
    {
        app.Store.Saved = app.SavedSession();
        await app.Database.SeedProfileAsync(ProfileId, app.Auth.UserId);
        using var auth = app.NewAuthService(out _);
        await auth.InitializeAsync();
        using var profiles = Profiles(auth);
        await Until(() => profiles.Profile is not null);

        await auth.SignOutAsync(discardUnsynced: true);

        Assert.False(profiles.Loaded);
        Assert.Null(profiles.Profile);
    }
}

public sealed class QueuedRow
{
    public string Data { get; set; } = "{}";
}

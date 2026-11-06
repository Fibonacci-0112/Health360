using Bunit;
using Health360.LocalDb;
using Health360.UI.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Health360.UI.Tests.Components;

public sealed class TrainingCompletionTests : AppTestContext
{
    private async Task<IRenderedComponent<Routes>> SetupAsync()
    {
        await SignedInAndOnboardedAsync();
        await App.Database.Db.Execute("INSERT INTO households (id, name) VALUES ('h', 'Household')");
        await App.Database.Db.Execute("INSERT INTO household_members (id, household_id, profile_id) VALUES ('hm', 'h', ?)", [ProfileId]);
        var cut = Render<Routes>();
        cut.WaitForAssertion(() => cut.Find("nav.tabs"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("train/setup");
        cut.WaitForAssertion(() => cut.Find("#save-location"), Wait);
        return cut;
    }

    [Fact]
    public async Task Setup_creates_and_edits_a_location_program_and_weekly_schedule()
    {
        var cut = await SetupAsync();
        await ChangeAsync(cut, "#location-name", "Garage");
        await ChangeAsync(cut, "#location-bar", "20");
        await SubmitAsync(cut, "#save-location");
        Assert.Equal("Garage", Assert.Single(await TrainingLocations.ListAsync(App.Database.Db)).Name);
        await ChangeAsync(cut, "#location-name", "Gym");
        await SubmitAsync(cut, "#save-location");
        Assert.Equal("Gym", Assert.Single(await TrainingLocations.ListAsync(App.Database.Db)).Name);
        var template = await WorkoutPlans.CreateTemplateAsync(App.Database.Db, ProfileId, "Legs", [], App.Clock);
        // Reload to pick up a template synced while this form was open.
        Services.GetRequiredService<NavigationManager>().NavigateTo("train");
        cut.WaitForAssertion(() => cut.Find("#start-empty"), Wait);
        Services.GetRequiredService<NavigationManager>().NavigateTo("train/setup");
        cut.WaitForAssertion(() => cut.Find("#save-program"), Wait);
        await ChangeAsync(cut, "#program-name", "Base");
        await SubmitAsync(cut, "#save-program");
        var program = Assert.Single(await WorkoutPlans.ProgramsAsync(App.Database.Db, ProfileId));
        await ChangeAsync(cut, "#schedule-template", template);
        await CheckAsync(cut, "[aria-label=Schedule] input[type=checkbox]", true);
        await SubmitAsync(cut, "#add-schedule");
        Assert.Equal(1, Assert.Single(await WorkoutPlans.ScheduleEntriesAsync(App.Database.Db, ProfileId, program.Id)).Weekday);
        Assert.Contains("Legs", cut.Find("[aria-labelledby=calendar-title]").TextContent);
    }

    [Fact]
    public async Task Rest_timer_restores_the_deadline_and_cancels_on_profile_change()
    {
        var notifications = new Notifications();
        Services.AddSingleton<IRestNotifications>(notifications);
        _ = await SetupAsync();
        var workouts = Services.GetRequiredService<WorkoutService>();
        var profiles = Services.GetRequiredService<ProfileService>();
        using var original = new RestTimerService(workouts, profiles, notifications, App.Clock);
        await original.StartAsync("session", 120);
        Assert.Equal(App.Clock.Now.AddSeconds(120), notifications.Deadline);
        App.Clock.Advance(TimeSpan.FromSeconds(75));
        using var restored = new RestTimerService(workouts, profiles, notifications, App.Clock);
        await restored.RestoreAsync("session");
        Assert.Equal(45, restored.RemainingSeconds);
        await App.Database.Db.Execute("DELETE FROM profiles WHERE id = ?", [ProfileId]);
        // ProfileService publishes local changes asynchronously.
        for (var i = 0; i < 100 && restored.Deadline is not null; i++) await Task.Delay(10, Xunit.TestContext.Current.CancellationToken);
        Assert.Null(restored.Deadline);
        Assert.Null(notifications.Deadline);
    }

    [Fact]
    public void E1rm_chart_handles_empty_single_and_constant_values_with_a_real_date_axis()
    {
        var cut = Render<UI.Components.E1rmChart>();
        Assert.Contains("No estimated 1RM", cut.Markup);
        cut.Render(p => p.Add(c => c.Points, [new E1rmPoint { LocalDate = "2026-11-01", E1rmKg = 100 }]));
        Assert.Single(cut.FindAll("circle"));
        Assert.Empty(cut.FindAll("polyline"));
        cut.Render(p => p.Add(c => c.Unit, "lb").Add(c => c.Points,
            [new E1rmPoint { LocalDate = "2026-11-01", E1rmKg = 100 }, new E1rmPoint { LocalDate = "2026-11-02", E1rmKg = 100 }, new E1rmPoint { LocalDate = "2026-11-11", E1rmKg = 100 }]));
        Assert.Equal("89.6", cut.FindAll("circle")[1].GetAttribute("cx"));
        Assert.DoesNotContain("NaN", cut.Markup);
        Assert.Contains("220.5", cut.Find("table").TextContent);
    }

    private sealed class Notifications : IRestNotifications
    {
        public DateTimeOffset? Deadline { get; private set; }
        public Task<bool> ScheduleAsync(DateTimeOffset deadline) { Deadline = deadline; return Task.FromResult(true); }
        public Task CancelAsync() { Deadline = null; return Task.CompletedTask; }
    }
}

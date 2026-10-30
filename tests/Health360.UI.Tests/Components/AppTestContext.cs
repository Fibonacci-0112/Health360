using AngleSharp.Dom;
using Bunit;
using Health360.UI.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Health360.UI.Tests.Components;

/// <summary>A bUnit context with the app's services (<see cref="Support.App"/>) registered.</summary>
public abstract class AppTestContext : BunitContext, IAsyncLifetime
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    protected const string ProfileId = "0192f0c0-0000-7000-8000-000000000001";

    protected App App { get; private set; } = null!;

    public virtual async ValueTask InitializeAsync()
    {
        App = await App.StartAsync();
        App.Register(this);
        // FocusOnNavigate and NavLink call into JS; nothing here depends on their result.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await base.DisposeAsync();
        await App.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    // The app re-renders from background work (sync status, watched queries), so events are awaited:
    // bUnit's synchronous triggers can land on a handler a concurrent render just replaced.
    protected static Task TypeAsync<T>(IRenderedComponent<T> cut, string selector, string value)
        where T : IComponent =>
        cut.Find(selector).TriggerEventAsync("oninput", new ChangeEventArgs { Value = value });

    protected static Task ChangeAsync<T>(IRenderedComponent<T> cut, string selector, string value)
        where T : IComponent =>
        cut.Find(selector).TriggerEventAsync("onchange", new ChangeEventArgs { Value = value });

    protected static Task ClickAsync(IElement element) => element.ClickAsync(new MouseEventArgs());

    protected static Task ClickAsync<T>(IRenderedComponent<T> cut, string selector)
        where T : IComponent => ClickAsync(cut.Find(selector));

    protected static Task SubmitAsync<T>(IRenderedComponent<T> cut)
        where T : IComponent => cut.Find("form").SubmitAsync(EventArgs.Empty);

    /// <summary>Signed in on an earlier launch, onboarded, profile synced.</summary>
    protected async Task SignedInAndOnboardedAsync(string unitPrefs = "{}")
    {
        App.Store.Saved = App.SavedSession();
        await App.Database.SeedProfileAsync(ProfileId, App.Auth.UserId, unitPrefs, "2026-10-29T08:00:00.000Z", "Sam");
    }
}

#if DEBUG
using Microsoft.Extensions.Logging;
#endif
using Health360.Maui.Data;
using Health360.UI;
using Health360.UI.Auth;

namespace Health360.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton(AppEndpoints.FromBuild());
        builder.Services.AddSingleton<ISessionStore, SecureSessionStore>();
        builder.Services.AddMauiLocalDatabase();
        builder.Services.AddHealth360Ui();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}

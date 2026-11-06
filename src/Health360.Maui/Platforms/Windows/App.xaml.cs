namespace Health360.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        Health360.Maui.Data.MauiRestNotifications.UseAppUserModelId();
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}

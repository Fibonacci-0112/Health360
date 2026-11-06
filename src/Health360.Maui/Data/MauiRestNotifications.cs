using Health360.UI.Data;
#if ANDROID
using Android.App;
using Android.Content;
using AndroidX.Core.App;
#elif WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Windows.AppNotifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
#endif

namespace Health360.Maui.Data;

/// <summary>Generic OS reminders: no owner, exercise, load or health information leaves the app.</summary>
public sealed class MauiRestNotifications : IRestNotifications
{
#if ANDROID
    internal const int NotificationId = 3602;
    internal const string ChannelId = "rest";

    private static PendingIntent AlarmIntent()
    {
        var context = Android.App.Application.Context;
        var intent = new Intent(context, typeof(RestAlarmReceiver));
        return PendingIntent.GetBroadcast(context, NotificationId, intent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
    }

    public async Task<bool> ScheduleAsync(DateTimeOffset deadline)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<RestNotificationPermission>) != PermissionStatus.Granted)
        {
            return false;
        }

        var context = Android.App.Application.Context;
        if (!NotificationManagerCompat.From(context).AreNotificationsEnabled())
        {
            return false;
        }
        var alarms = (AlarmManager)context.GetSystemService(Context.AlarmService)!;
        using var pending = AlarmIntent();
        if (!OperatingSystem.IsAndroidVersionAtLeast(31) || alarms.CanScheduleExactAlarms())
        {
            alarms.SetExactAndAllowWhileIdle(AlarmType.RtcWakeup, deadline.ToUnixTimeMilliseconds(), pending);
            return true;
        }

        // The user can enable Alarms & reminders in Android settings for exact background delivery.
        // Report that exact scheduling is unavailable, while still scheduling the best-effort reminder.
        alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, deadline.ToUnixTimeMilliseconds(), pending);
        return false;
    }

    public Task CancelAsync()
    {
        var context = Android.App.Application.Context;
        using var pending = AlarmIntent();
        ((AlarmManager)context.GetSystemService(Context.AlarmService)!).Cancel(pending);
        NotificationManagerCompat.From(context).Cancel(NotificationId);
        return Task.CompletedTask;
    }
#elif WINDOWS
    // Unpackaged desktop apps have no package identity, so toasts need an explicit, registered AppUserModelID.
    // AppNotificationManager.Register() registers the process's explicit ID (with its activator) under
    // HKCU\Software\Classes\AppUserModelId, and every toast call below uses the same ID.
    internal const string AppUserModelId = "Health360.Desktop";
    private static bool appIdSet;
    private bool registered;

    /// <summary>Call once at startup, before any window is shown.</summary>
    public static void UseAppUserModelId()
    {
        try
        {
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            appIdSet = true;
        }
        catch (COMException)
        {
            // Reminders report themselves unavailable rather than preventing startup.
        }
    }

    private bool Register()
    {
        if (!appIdSet)
        {
            return false;
        }
        if (registered)
        {
            return true;
        }
        AppNotificationManager.Default.NotificationInvoked += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
            {
                window.Activate();
            }
        });
        AppNotificationManager.Default.Register();
        registered = true;
        return true;
    }

    public Task<bool> ScheduleAsync(DateTimeOffset deadline)
    {
        if (!Register())
        {
            return Task.FromResult(false);
        }
        var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        if (notifier.Setting != NotificationSetting.Enabled)
        {
            return Task.FromResult(false);
        }
        var xml = new XmlDocument();
        xml.LoadXml("<toast><visual><binding template=\"ToastGeneric\"><text>Health360</text><text>Rest complete</text></binding></visual></toast>");
        notifier.AddToSchedule(new ScheduledToastNotification(xml, deadline) { Tag = "rest", Group = "health360" });
        return Task.FromResult(true);
    }

    public Task CancelAsync()
    {
        if (!Register())
        {
            return Task.CompletedTask;
        }
        var notifier = ToastNotificationManager.CreateToastNotifier(AppUserModelId);
        foreach (var item in notifier.GetScheduledToastNotifications().Where(n => n.Tag == "rest" && n.Group == "health360"))
        {
            notifier.RemoveFromSchedule(item);
        }
        ToastNotificationManager.History.Remove("rest", "health360", AppUserModelId);
        return Task.CompletedTask;
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void SetCurrentProcessExplicitAppUserModelID(string appId);
    }
#else
    public Task<bool> ScheduleAsync(DateTimeOffset deadline) => Task.FromResult(false);
    public Task CancelAsync() => Task.CompletedTask;
#endif
}

#if ANDROID
public sealed class RestNotificationPermission : Permissions.BasePlatformPermission
{
    public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
        OperatingSystem.IsAndroidVersionAtLeast(33) ? [(Android.Manifest.Permission.PostNotifications, true)] : [];
}

[BroadcastReceiver(Enabled = true, Exported = false)]
public sealed class RestAlarmReceiver : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || !NotificationManagerCompat.From(context).AreNotificationsEnabled())
        {
            return;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(33)
            && context.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Android.Content.PM.Permission.Granted)
        {
            return;
        }
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            manager.CreateNotificationChannel(new NotificationChannel(MauiRestNotifications.ChannelId, "Rest timer", NotificationImportance.Default));
        }
        var launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName!);
        var tap = launch is null ? null : PendingIntent.GetActivity(context, 0, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new NotificationCompat.Builder(context, MauiRestNotifications.ChannelId)
            .SetContentTitle("Health360")
            .SetContentText("Rest complete")
            .SetSmallIcon(Android.Resource.Drawable.IcLockIdleAlarm)
            .SetContentIntent(tap)
            .SetAutoCancel(true)
            .Build();
        NotificationManagerCompat.From(context).Notify(MauiRestNotifications.NotificationId, notification);
    }
}
#endif

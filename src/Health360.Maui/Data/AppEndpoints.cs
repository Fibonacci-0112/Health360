using System.Reflection;
using Health360.LocalDb;

namespace Health360.Maui.Data;

/// <summary>
/// Where this build syncs and signs in, set at build time (<c>Health360SupabaseUrl</c>,
/// <c>Health360SupabaseApiKey</c>, <c>Health360PowerSyncUrl</c>; see docs/phase0b.md). None of these are
/// secrets: the key is the publishable (anon) key, never the service-role key.
/// </summary>
public static class AppEndpoints
{
    public static SyncEndpoints FromBuild()
    {
        var metadata = typeof(AppEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        string Read(string key) => metadata.TryGetValue(key, out var value) ? value ?? "" : "";

        return new SyncEndpoints(
            ForDevice(new Uri(Read("Health360.SupabaseUrl"))),
            Read("Health360.SupabaseApiKey"),
            ForDevice(new Uri(Read("Health360.PowerSyncUrl"))));
    }

    /// <summary>
    /// The Android emulator reaches the host computer at 10.0.2.2, not 127.0.0.1, so a local stack's
    /// loopback address is rewritten there. Physical devices and Windows keep the address as built.
    /// </summary>
    private static Uri ForDevice(Uri uri)
    {
        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.DeviceType == DeviceType.Virtual && uri.IsLoopback)
        {
            return new UriBuilder(uri) { Host = "10.0.2.2" }.Uri;
        }

        return uri;
    }
}

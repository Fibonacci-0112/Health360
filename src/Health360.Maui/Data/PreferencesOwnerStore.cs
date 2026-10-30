using Health360.LocalDb;

namespace Health360.Maui.Data;

/// <summary>
/// Keeps the local database's owner in app preferences, which live in the app's private storage
/// (excluded from backups on Android: <c>allowBackup="false"</c>).
/// </summary>
public sealed class PreferencesOwnerStore : IDatabaseOwnerStore
{
    private const string Key = "health360.local-db-owner";

    public string? Load() => Preferences.Default.Get<string?>(Key, null);

    public void Save(string userId) => Preferences.Default.Set(Key, userId);

    public void Forget() => Preferences.Default.Remove(Key);
}

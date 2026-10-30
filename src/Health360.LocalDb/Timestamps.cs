using System.Globalization;

namespace Health360.LocalDb;

/// <summary>The text forms local writes store, matching what the prototype writes.</summary>
internal static class Timestamps
{
    /// <summary>UTC with milliseconds, as JavaScript's <c>toISOString()</c> writes it.</summary>
    public static string UtcNow(TimeProvider clock) =>
        clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Today in the clock's local time zone, as <c>YYYY-MM-DD</c> (event rows store a local day).</summary>
    public static string LocalDate(TimeProvider clock) =>
        clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

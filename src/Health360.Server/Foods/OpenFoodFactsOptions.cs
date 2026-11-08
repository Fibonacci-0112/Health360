using System.ComponentModel.DataAnnotations;

namespace Health360.Server.Foods;

/// <summary>The <c>OpenFoodFacts</c> configuration section. The product API needs no key.</summary>
public sealed class OpenFoodFactsOptions
{
    public const string Section = "OpenFoodFacts";

    public Uri BaseAddress { get; set; } = new("https://world.openfoodfacts.org/api/v2/");

    /// <summary>
    /// Sent as the User-Agent, which Open Food Facts asks every client to set so it can reach the app's owner.
    /// Keep it generic: no personal contact details.
    /// </summary>
    [Required]
    public string UserAgent { get; set; } = "Health360/1.0 (+https://github.com/Fibonacci-0112/health360)";

    /// <summary>
    /// Barcode lookups each signed-in user may make per hour before getting 429. A backstop held in memory per
    /// API process; the shared per-minute budget below is what protects the upstream limit.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RequestsPerUserPerHour { get; set; } = 120;

    /// <summary>
    /// Open Food Facts requests this API process may make in any rolling minute, across all users, before lookups
    /// answer 503. Open Food Facts allows about 100 product reads a minute per IP and one lookup can take two
    /// requests (a code is asked for in each form it fits), so the per-user limit alone cannot keep a burst from
    /// getting every user's lookups throttled. The default leaves headroom below 100. Held in memory per API
    /// process: with several processes behind one address, divide the allowance between them.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxUpstreamRequestsPerMinute { get; set; } = 80;
}

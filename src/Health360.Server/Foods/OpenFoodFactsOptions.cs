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
    /// Barcode lookups each signed-in user may make per hour before getting 429. Open Food Facts allows about 100
    /// product reads a minute per IP, so this backstop, held in memory per API process, keeps one user from
    /// spending a household's share.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RequestsPerUserPerHour { get; set; } = 120;
}

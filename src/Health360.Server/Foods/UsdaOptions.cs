using System.ComponentModel.DataAnnotations;

namespace Health360.Server.Foods;

/// <summary>The <c>Usda</c> configuration section.</summary>
public sealed class UsdaOptions
{
    public const string Section = "Usda";

    /// <summary>
    /// The FoodData Central API key. Keep it in user secrets or the host's secret store (<c>Usda__ApiKey</c>, or
    /// <c>FDC_API_KEY</c>), never in <c>appsettings*.json</c>. Without it the API still starts and the USDA step
    /// of a barcode lookup answers 503.
    /// </summary>
    public string? ApiKey { get; set; }

    public Uri BaseAddress { get; set; } = new("https://api.nal.usda.gov/fdc/v1/");

    /// <summary>
    /// Barcode lookups each signed-in user may make per hour before getting 429. Every lookup spends the shared
    /// key's allowance, so this is a backstop held in memory per API process.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RequestsPerUserPerHour { get; set; } = 120;

    /// <summary>
    /// USDA requests this API process may make in any rolling hour, across all users, before lookups answer 503. USDA blocks a
    /// key for a while past 1,000 requests an hour per IP address, and one lookup can take several requests (a
    /// search per padded form of the code), so the per-user limit alone cannot protect the shared key. Held in
    /// memory per API process: with several processes behind one address, divide the allowance between them.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxUpstreamRequestsPerHour { get; set; } = 900;

    /// <summary>The most search pages read looking for a GTIN (each holds up to 50 foods).</summary>
    [Range(1, 10)]
    public int MaxPages { get; set; } = 3;

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

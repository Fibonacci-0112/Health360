using System.ComponentModel.DataAnnotations;

namespace Health360.Server.Ai;

/// <summary>The <c>Anthropic</c> configuration section.</summary>
public sealed class AnthropicOptions
{
    public const string Section = "Anthropic";

    /// <summary>
    /// The API key. Keep it in user secrets or the host's secret store (<c>Anthropic__ApiKey</c>, or
    /// <c>ANTHROPIC_API_KEY</c>), never in <c>appsettings*.json</c>. Without it the API still starts,
    /// and AI endpoints answer 503.
    /// </summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Retries of rate limits, overloads and connection errors, done by the SDK.</summary>
    [Range(0, 10)]
    public int MaxRetries { get; set; } = 2;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// AI requests each signed-in user may make per hour, across all AI endpoints, before getting 429.
    /// Held in memory per API process: a cost backstop until per-profile quotas land in <c>ai_generations</c>.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int RequestsPerUserPerHour { get; set; } = 20;

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

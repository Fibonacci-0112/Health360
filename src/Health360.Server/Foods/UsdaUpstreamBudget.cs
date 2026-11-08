using Microsoft.Extensions.Options;

namespace Health360.Server.Foods;

/// <summary>A fixed one-hour window of USDA requests shared by every user of this API process.</summary>
public sealed class UsdaUpstreamBudget(TimeProvider time, IOptions<UsdaOptions> options)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly object gate = new();
    private DateTimeOffset windowStart = time.GetUtcNow();
    private int used;

    /// <summary>Takes one request from the budget; false once this hour's allowance is spent.</summary>
    public bool TryTake()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            if (now - windowStart >= Window)
            {
                windowStart = now;
                used = 0;
            }

            if (used >= options.Value.MaxUpstreamRequestsPerHour)
            {
                return false;
            }

            used++;
            return true;
        }
    }
}

/// <summary>This process has made all the USDA requests it may this hour.</summary>
public sealed class UsdaBudgetExhaustedException() : Exception("The hourly USDA request budget is spent.");

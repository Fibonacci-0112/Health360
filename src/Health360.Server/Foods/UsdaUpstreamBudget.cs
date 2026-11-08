using Microsoft.Extensions.Options;

namespace Health360.Server.Foods;

/// <summary>
/// A rolling one-hour allowance of USDA requests shared by every user of this API process. USDA counts a rolling
/// hour, so a fixed window that resets all at once could let through twice the allowance across its boundary.
/// </summary>
public sealed class UsdaUpstreamBudget(TimeProvider time, IOptions<UsdaOptions> options)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> taken = new();

    /// <summary>Takes one request from the budget; false while the last hour already holds the whole allowance.</summary>
    public bool TryTake()
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            while (taken.Count > 0 && now - taken.Peek() >= Window)
            {
                taken.Dequeue();
            }

            if (taken.Count >= options.Value.MaxUpstreamRequestsPerHour)
            {
                return false;
            }

            taken.Enqueue(now);
            return true;
        }
    }
}

/// <summary>This process has made all the USDA requests it may this hour.</summary>
public sealed class UsdaBudgetExhaustedException() : Exception("The hourly USDA request budget is spent.");

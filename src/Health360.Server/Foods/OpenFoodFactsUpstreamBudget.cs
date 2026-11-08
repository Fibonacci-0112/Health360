using Microsoft.Extensions.Options;

namespace Health360.Server.Foods;

/// <summary>
/// A rolling one-minute allowance of Open Food Facts requests shared by every user of this API process, so a burst
/// from one user cannot get every user throttled by the upstream per-IP limit. Rolling, not a fixed window that
/// resets all at once, so no boundary lets through twice the allowance.
/// </summary>
public sealed class OpenFoodFactsUpstreamBudget(TimeProvider time, IOptions<OpenFoodFactsOptions> options)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object gate = new();
    private readonly Queue<long> taken = new();

    /// <summary>Takes one request from the budget; false while the last minute already holds the whole allowance.</summary>
    public bool TryTake()
    {
        lock (gate)
        {
            // A monotonic clock, not wall time, so a stepped system clock can neither keep old requests counting
            // nor expire new ones early.
            var now = time.GetTimestamp();
            while (taken.Count > 0 && time.GetElapsedTime(taken.Peek(), now) >= Window)
            {
                taken.Dequeue();
            }

            if (taken.Count >= options.Value.MaxUpstreamRequestsPerMinute)
            {
                return false;
            }

            taken.Enqueue(now);
            return true;
        }
    }
}

/// <summary>This process has made all the Open Food Facts requests it may this minute.</summary>
public sealed class OpenFoodFactsBudgetExhaustedException() : Exception("The per-minute Open Food Facts request budget is spent.");

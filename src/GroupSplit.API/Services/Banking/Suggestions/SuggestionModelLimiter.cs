using System.Threading.RateLimiting;
using GroupSplit.API.Extensions;
using Microsoft.Extensions.Options;

namespace GroupSplit.API.Services.Banking.Suggestions;

/// <summary>Limits paid model calls per user; cached answers do not consume this allowance.</summary>
public sealed class SuggestionModelLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> _limiter;

    public SuggestionModelLimiter(IOptions<ApiRateLimits> options)
    {
        var limit = options.Value.SuggestionModelCalls;
        _limiter = PartitionedRateLimiter.Create<Guid, Guid>(userId =>
            RateLimitPartition.GetFixedWindowLimiter(userId, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                Window = limit.Window,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    public bool TryAcquire(Guid userId)
    {
        using var lease = _limiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}

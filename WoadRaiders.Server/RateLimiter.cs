namespace WoadRaiders.Server;

/// <summary>
/// A token-bucket rate limiter. Starts full (a burst of <c>capacity</c> is allowed),
/// refills at <c>refillPerSecond</c>, and hands out one token per call — so sustained
/// traffic is capped at the refill rate while short bursts pass. Time is passed in
/// (a monotonic clock) rather than read internally, so it is deterministic and
/// unit-testable. Not thread-safe: driven only from the single network/loop thread.
/// </summary>
internal sealed class RateLimiter
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private double _tokens;
    private TimeSpan _last;

    public RateLimiter(double capacity, double refillPerSecond, TimeSpan start)
    {
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _tokens = capacity;
        _last = start;
    }

    /// <summary>Consume one token; returns false (drop) when the bucket is empty.</summary>
    public bool TryConsume(TimeSpan now)
    {
        var elapsedSeconds = (now - _last).TotalSeconds;
        if (elapsedSeconds > 0)
        {
            _tokens = Math.Min(_capacity, _tokens + elapsedSeconds * _refillPerSecond);
            _last = now;
        }

        if (_tokens < 1.0)
            return false;

        _tokens -= 1.0;
        return true;
    }
}

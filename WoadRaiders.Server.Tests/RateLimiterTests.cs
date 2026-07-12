using WoadRaiders.Server;

namespace WoadRaiders.Server.Tests;

public class RateLimiterTests
{
    private static TimeSpan Sec(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void Allows_a_burst_up_to_capacity_then_blocks()
    {
        var r = new RateLimiter(capacity: 5, refillPerSecond: 1, start: TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
            Assert.True(r.TryConsume(TimeSpan.Zero)); // five in the same instant
        Assert.False(r.TryConsume(TimeSpan.Zero));    // sixth blocked — bucket empty
    }

    [Fact]
    public void Refills_over_time()
    {
        var r = new RateLimiter(capacity: 5, refillPerSecond: 10, start: TimeSpan.Zero);
        for (var i = 0; i < 5; i++) r.TryConsume(TimeSpan.Zero); // drain
        Assert.False(r.TryConsume(TimeSpan.Zero));

        // 10 tokens/sec → half a second later at least one is back.
        Assert.True(r.TryConsume(Sec(0.5)));
    }

    [Fact]
    public void Caps_sustained_traffic_at_the_refill_rate()
    {
        var r = new RateLimiter(capacity: 10, refillPerSecond: 100, start: TimeSpan.Zero);
        for (var i = 0; i < 10; i++) r.TryConsume(TimeSpan.Zero); // spend the burst

        // Hammer once a millisecond for a second: only ~refill-rate should pass, not 1000.
        var allowed = 0;
        for (var ms = 0; ms <= 1000; ms++)
            if (r.TryConsume(TimeSpan.FromMilliseconds(ms)))
                allowed++;

        Assert.InRange(allowed, 95, 110); // ≈100/s sustained
    }

    [Fact]
    public void Never_exceeds_capacity_no_matter_how_long_it_idles()
    {
        var r = new RateLimiter(capacity: 3, refillPerSecond: 100, start: TimeSpan.Zero);

        // Idle an hour, then the burst is still only `capacity`, not an hour of refill.
        var allowed = 0;
        for (var i = 0; i < 100; i++)
            if (r.TryConsume(Sec(3600)))
                allowed++;

        Assert.Equal(3, allowed);
    }
}

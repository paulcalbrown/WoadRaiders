using WoadRaiders.Server;

namespace WoadRaiders.Server.Tests;

public class FixedTimestepSchedulerTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // 10 ms ticks, 25 ms snapshots, catch-up capped at 5.
    private static FixedTimestepScheduler Make() =>
        new(Ms(10), Ms(25), maxCatchUpTicks: 5);

    [Fact]
    public void Runs_one_tick_per_interval_in_steady_state()
    {
        var s = Make();
        Assert.Equal(1, s.Advance(Ms(0)).Ticks); // first frame fires the initial tick

        for (var t = 10; t <= 100; t += 10)
            Assert.Equal(1, s.Advance(Ms(t)).Ticks); // exactly one per interval — no runaway, no drift
    }

    [Fact]
    public void Caps_catch_up_and_drops_the_lost_time_after_a_stall()
    {
        var s = Make();
        s.Advance(Ms(0)); // prime

        var stalled = s.Advance(Ms(1000)); // a one-second hitch
        Assert.Equal(5, stalled.Ticks);         // capped, not ~100
        Assert.True(stalled.Stalled);
        Assert.True(stalled.Dropped > Ms(900)); // most of the second is abandoned

        // Resumes the normal cadence instead of replaying the backlog.
        Assert.True(s.Advance(Ms(1010)).Ticks <= 2);
        Assert.Equal(1, s.Advance(Ms(1020)).Ticks);
        Assert.Equal(1, s.Advance(Ms(1030)).Ticks);
    }

    [Fact]
    public void No_time_dropped_while_keeping_up()
    {
        var s = Make();
        for (var t = 0; t <= 200; t += 10)
            Assert.False(s.Advance(Ms(t)).Stalled);
    }

    [Fact]
    public void Snapshots_fire_on_their_own_cadence_independent_of_ticks()
    {
        var s = Make();
        Assert.True(s.Advance(Ms(0)).Snapshot);   // first frame
        Assert.False(s.Advance(Ms(10)).Snapshot); // 10 < 25
        Assert.False(s.Advance(Ms(20)).Snapshot); // 20 < 25
        Assert.True(s.Advance(Ms(30)).Snapshot);  // 30 >= 25
        Assert.False(s.Advance(Ms(40)).Snapshot); // 40 < 55
        Assert.True(s.Advance(Ms(60)).Snapshot);  // 60 >= 55
    }

    [Fact]
    public void A_stall_does_not_bank_a_burst_of_overdue_snapshots()
    {
        var s = Make();
        s.Advance(Ms(0));

        // After a long gap only ONE snapshot is due, not one per missed interval.
        Assert.True(s.Advance(Ms(1000)).Snapshot);
        Assert.False(s.Advance(Ms(1010)).Snapshot); // next isn't due until 1025
    }
}

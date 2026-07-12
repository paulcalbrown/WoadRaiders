namespace WoadRaiders.Server;

/// <summary>
/// The game loop's timing decisions, pulled out of the loop so they can be tested
/// without a real clock. Fed the elapsed time each frame, it reports how many
/// fixed-step ticks to run (bounded, so a stall can't spiral), how much time it had
/// to drop when it hit that bound, and whether a snapshot is due.
///
/// Ticks accumulate on a fixed grid (so the sim stays phase-locked and catch-up
/// works); snapshots re-anchor to "now" (so a stall can't bank a burst of overdue
/// snapshots). Both behaviours are exercised by unit tests.
/// </summary>
internal sealed class FixedTimestepScheduler
{
    private readonly TimeSpan _tickInterval;
    private readonly TimeSpan _snapshotInterval;
    private readonly int _maxCatchUpTicks;

    private TimeSpan _nextTick;
    private TimeSpan _nextSnapshot;

    public FixedTimestepScheduler(
        TimeSpan tickInterval, TimeSpan snapshotInterval, int maxCatchUpTicks, TimeSpan start = default)
    {
        _tickInterval = tickInterval;
        _snapshotInterval = snapshotInterval;
        _maxCatchUpTicks = maxCatchUpTicks;
        _nextTick = start;
        _nextSnapshot = start;
    }

    /// <summary>What the loop should do this frame.</summary>
    /// <param name="Ticks">Fixed-step ticks to run (0..maxCatchUpTicks).</param>
    /// <param name="Dropped">Sim time abandoned because the catch-up bound was hit; zero when keeping up.</param>
    /// <param name="Snapshot">Whether to broadcast a world snapshot this frame.</param>
    public readonly record struct Plan(int Ticks, TimeSpan Dropped, bool Snapshot)
    {
        /// <summary>True when the loop fell so far behind that time had to be dropped.</summary>
        public bool Stalled => Dropped > TimeSpan.Zero;
    }

    /// <summary>Advance the schedule to <paramref name="now"/> and report the plan for this frame.</summary>
    public Plan Advance(TimeSpan now)
    {
        var ticks = 0;
        while (now >= _nextTick && ticks < _maxCatchUpTicks)
        {
            ticks++;
            _nextTick += _tickInterval;
        }

        // Still behind after the cap (a long hitch): drop the lost time and resume at
        // the normal cadence rather than replaying every missed tick.
        var dropped = TimeSpan.Zero;
        if (now >= _nextTick)
        {
            dropped = now - _nextTick;
            _nextTick = now;
        }

        var snapshot = now >= _nextSnapshot;
        if (snapshot)
            _nextSnapshot = now + _snapshotInterval;

        return new Plan(ticks, dropped, snapshot);
    }
}

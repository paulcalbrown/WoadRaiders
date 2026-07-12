using System.Collections.Concurrent;

namespace WoadRaiders.Server;

/// <summary>
/// A non-blocking logger. Producers (the game loop, packet handlers) enqueue a
/// line and return immediately; a single background thread drains the queue to the
/// console. This keeps synchronous console I/O off the simulation loop — a client
/// that provokes frequent log lines (loot, equips, bad packets) can't stall the
/// tick by making stdout back up.
///
/// The queue is bounded and <em>drops</em> (counting how many) rather than blocking
/// when full, so a log flood can never grow memory without bound or push back on
/// the loop — the whole point. The dropped count is reported on shutdown.
/// </summary>
public sealed class ServerLog : IDisposable
{
    private const int Capacity = 4096;

    private readonly BlockingCollection<(bool Error, string Text)> _queue = new(Capacity);
    private readonly Thread _drain;
    private long _dropped;

    public ServerLog()
    {
        _drain = new Thread(DrainLoop) { IsBackground = true, Name = "server-log" };
        _drain.Start();
    }

    /// <summary>Queue a line for stdout. Never blocks; drops (counted) if the queue is full.</summary>
    public void Info(string message) => Enqueue(false, message);

    /// <summary>Queue a line for stderr. Never blocks; drops (counted) if the queue is full.</summary>
    public void Error(string message) => Enqueue(true, message);

    private void Enqueue(bool error, string message)
    {
        if (!_queue.TryAdd((error, message)))
            Interlocked.Increment(ref _dropped);
    }

    private void DrainLoop()
    {
        foreach (var (error, text) in _queue.GetConsumingEnumerable())
            (error ? Console.Error : Console.Out).WriteLine(text);
    }

    /// <summary>Flush the queue, stop the drain thread, and report any dropped lines.</summary>
    public void Dispose()
    {
        _queue.CompleteAdding();      // let the drain thread finish the backlog, then exit
        _drain.Join(TimeSpan.FromSeconds(2));

        var dropped = Interlocked.Read(ref _dropped);
        if (dropped > 0)
            Console.Error.WriteLine($"[log] {dropped} log line(s) dropped under load.");

        _queue.Dispose();
    }
}

namespace WoadRaiders.Core;

/// <summary>
/// A per-player jitter buffer for authoritative input processing.
///
/// The server consumes exactly one input per player per simulation tick, in
/// sequence order, so it replays the client's input stream 1:1 — which is what
/// makes reconciliation drift-free. (The old "apply the latest input each tick"
/// model silently dropped an input when two arrived between ticks and re-applied
/// a stale one when none did, so the server's <c>LastProcessedInput</c> never
/// lined up with the client's prediction — a small correction popped in on every
/// snapshot.)
///
/// A small cushion (<see cref="TargetBuffer"/> ticks) rides out network/scheduler
/// jitter so the buffer rarely starves; a hard cap (<see cref="MaxBuffer"/>)
/// fast-forwards when a client's inputs pile up, bounding how far behind "now"
/// the authoritative state can drift. Engine-free and deterministic → unit-tested.
///
/// Assumes in-order, loss-free delivery (inputs are sent ReliableOrdered); the
/// sequence guard below is only a defensive backstop.
/// </summary>
public sealed class ServerInputBuffer
{
    /// <summary>Ticks of input held in reserve before flowing, to absorb jitter (~66 ms at 30 Hz).</summary>
    public const int TargetBuffer = 2;

    /// <summary>Hard cap; beyond it the oldest inputs are dropped to bound authoritative latency.</summary>
    public const int MaxBuffer = 8;

    private readonly Queue<PlayerInput> _queue = new();
    private uint _lastAccepted;
    private bool _flowing;

    /// <summary>How many inputs are currently buffered.</summary>
    public int Count => _queue.Count;

    /// <summary>Buffer an incoming input. Stale or duplicate sequences are ignored.</summary>
    public void Enqueue(in PlayerInput input)
    {
        if (input.Sequence <= _lastAccepted)
            return; // out-of-order or duplicate — a newer input already won

        _lastAccepted = input.Sequence;
        _queue.Enqueue(input);

        // Fell too far behind (e.g. a burst arriving after a stall): drop the
        // oldest to bound how stale the authoritative player can get. This costs
        // one reconciliation correction, but only under sustained congestion.
        while (_queue.Count > MaxBuffer)
            _queue.Dequeue();
    }

    /// <summary>
    /// The input to apply on this tick. Returns false to <em>hold</em> — while
    /// priming the cushion, or when starved — in which case the caller should
    /// freeze the player (keeping their last processed sequence) rather than
    /// re-apply a stale input. A hold is a no-op the client reconciles exactly.
    /// </summary>
    public bool TryDequeue(out PlayerInput input)
    {
        if (!_flowing)
        {
            if (_queue.Count < TargetBuffer)
            {
                input = default;
                return false; // still building the cushion
            }
            _flowing = true;
        }

        if (_queue.Count == 0)
        {
            input = default;
            return false; // starved this tick — hold instead of re-applying stale intent
        }

        input = _queue.Dequeue();
        return true;
    }
}

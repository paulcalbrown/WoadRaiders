using LiteNetLib;

namespace WoadRaiders.Server;

/// <summary>
/// The server-side transport state that lives and dies with a connected peer: the
/// peer handle and its message-rate budget. (The player's simulation state and input
/// buffer live in the <see cref="GameSession"/>.)
/// </summary>
internal sealed class Connection
{
    // Generous headroom over legitimate traffic (input ~30/s plus the odd equip);
    // a flooding peer is capped at the refill rate, with short bursts up to capacity.
    private const double RateCapacity = 400;
    private const double RateRefillPerSecond = 200;

    private readonly RateLimiter _rate;

    public Connection(NetPeer peer, TimeSpan now)
    {
        Peer = peer;
        _rate = new RateLimiter(RateCapacity, RateRefillPerSecond, now);
    }

    public NetPeer Peer { get; }

    /// <summary>The instance this peer raids, set when its join request lands
    /// (null until then — a connected-but-not-joined peer is in no instance).</summary>
    public int? Instance { get; set; }

    /// <summary>False when this peer is over its message-rate budget — drop the message.</summary>
    public bool AllowMessage(TimeSpan now) => _rate.TryConsume(now);
}

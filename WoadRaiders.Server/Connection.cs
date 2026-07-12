using LiteNetLib;
using WoadRaiders.Core;

namespace WoadRaiders.Server;

/// <summary>
/// The server-side state that lives and dies with a connected peer: the transport
/// handle and its per-player input jitter buffer. Bundling them keeps the
/// connect/disconnect bookkeeping in one place — one add, one remove — and gives
/// future per-connection state (rate limits, last-seen time) an obvious home.
/// </summary>
internal sealed class Connection
{
    public Connection(NetPeer peer) => Peer = peer;

    public NetPeer Peer { get; }

    public ServerInputBuffer Buffer { get; } = new();
}

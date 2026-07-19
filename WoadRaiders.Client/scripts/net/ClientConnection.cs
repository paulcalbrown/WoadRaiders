using Godot;
using LiteNetLib;
using LiteNetLib.Utils;
using WoadRaiders.Shared;

namespace WoadRaiders.Client;

/// <summary>Where the client is in its connection lifecycle.</summary>
public enum ConnectionState
{
    /// <summary>Socket dialing the server (initial connect or a retry).</summary>
    Connecting,

    /// <summary>Transport is up, no join sent — browsing the instance list.</summary>
    Lobby,

    /// <summary>JoinRequest sent, waiting for the Welcome (or a denial).</summary>
    Joining,

    /// <summary>Welcomed — we have a player id and are receiving snapshots.</summary>
    Playing,

    /// <summary>Lost (or never had) the server; a retry is counting down.</summary>
    Disconnected,

    /// <summary>The server refused this build's version. Terminal — retrying cannot
    /// fix it; the player needs a newer client (see <see cref="ClientConnection.RefusalMessage"/>).</summary>
    Incompatible,
}

/// <summary>
/// The client's transport layer — the only code that touches LiteNetLib. It owns
/// the socket, frames/deframes packets, dispatches inbound messages through a
/// handler table (the mirror of the server's), and runs the connection
/// lifecycle: Connecting → Lobby (peer up; <see cref="Connected"/> fires and the
/// owner decides what to send — the raid browser lists instances, the game screen
/// joins) → Joining (<see cref="SendJoin"/>) → Playing (Welcome received), dropping
/// to Disconnected and auto-retrying on loss. A denied join falls back to Lobby;
/// a version-refused connect parks in Incompatible (terminal — see <see cref="RefusalMessage"/>).
/// Everything above this class deals in typed packets and events only.
/// </summary>
public sealed class ClientConnection
{
    // All gameplay traffic rides channel 0 for now, matching the server.
    private const byte Channel = 0;
    private const double RetrySeconds = 3.0;

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly SnapshotAssembler _snapshots = new(); // rebuilds chunked snapshots
    private readonly Dictionary<MessageType, Action<NetPacketReader>> _handlers;
    private readonly string _host;
    private readonly int _port;
    private NetPeer? _server;
    private double _retryIn;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>Why the server refused us, straight from its ConnectDenied reject
    /// payload (or a stock line for a bare rejection). Null once connected, and
    /// while nothing has been refused. The UI shows it verbatim.</summary>
    public string? RefusalMessage { get; private set; }

    /// <summary>Transport is up (fires on every connect, including retries). The owner
    /// sends what its screen needs: a list request, a join, or nothing yet.</summary>
    public event Action? Connected;

    // One event per server → client message. Handlers fire on the main thread
    // (from Poll), so subscribers may touch the scene tree freely.
    public event Action<DungeonGeometryPacket>? GeometryReceived;
    public event Action<WelcomePacket>? Welcomed;
    public event Action<WorldSnapshotPacket>? SnapshotReceived;
    public event Action<ItemPickedUpPacket>? ItemPickedUp;
    public event Action<EquipmentUpdatePacket>? EquipmentUpdated;
    public event Action<InstanceListPacket>? InstanceListReceived;
    public event Action<JoinDeniedPacket>? JoinDenied;
    public event Action<RunCompletePacket>? RunCompleted;

    public ClientConnection(string host, int port)
    {
        _host = host;
        _port = port;
        // AutoRecycle pools packet readers instead of allocating per received packet.
        _net = new NetManager(_listener) { AutoRecycle = true };

        // Dispatch table for server → client messages, mirroring the server's:
        // adding a message is a line here plus its event — no growing switch.
        _handlers = new Dictionary<MessageType, Action<NetPacketReader>>
        {
            [MessageType.DungeonGeometry] = r => GeometryReceived?.Invoke(Read<DungeonGeometryPacket>(r)),
            [MessageType.Welcome] = r =>
            {
                State = ConnectionState.Playing;
                Welcomed?.Invoke(Read<WelcomePacket>(r));
            },
            [MessageType.WorldSnapshot] = r =>
            {
                // Snapshots ride Unreliable while the Welcome rides ReliableOrdered —
                // independent delivery streams with no cross-ordering guarantee. A
                // snapshot that outraces the Welcome (e.g. the Welcome was lost and is
                // being retransmitted) would be applied with no/a stale local player
                // id, wrongly creating the local player's view with the remote skin.
                // Pre-Welcome snapshots carry nothing usable, so drop them.
                // Each packet is one chunk of a possibly-split snapshot; the assembler
                // fires once the snapshot is whole and never delivers a stale tick.
                if (State == ConnectionState.Playing && _snapshots.TryAdd(r, out var snapshot))
                    SnapshotReceived?.Invoke(snapshot);
            },
            [MessageType.ItemPickedUp] = r => ItemPickedUp?.Invoke(Read<ItemPickedUpPacket>(r)),
            [MessageType.EquipmentUpdate] = r => EquipmentUpdated?.Invoke(Read<EquipmentUpdatePacket>(r)),
            [MessageType.InstanceList] = r => InstanceListReceived?.Invoke(Read<InstanceListPacket>(r)),
            [MessageType.RunComplete] = r => RunCompleted?.Invoke(Read<RunCompletePacket>(r)),
            [MessageType.JoinDenied] = r =>
            {
                // The server refused the join but kept the connection — back to
                // browsing; the owner decides where to send the player.
                State = ConnectionState.Lobby;
                JoinDenied?.Invoke(Read<JoinDeniedPacket>(r));
            },
        };

        _listener.PeerConnectedEvent += peer =>
        {
            _server = peer;
            State = ConnectionState.Lobby;
            RefusalMessage = null; // whatever turned us away before, we're in now
            // A fresh connection can be to a restarted server whose ticks begin
            // again at zero — stale-tick tracking from the old session would
            // swallow every snapshot, so it starts over too.
            _snapshots.Reset();
            Connected?.Invoke();
        };

        // Fires for a lost connection AND for a dial that never landed, so this one
        // arm drives every retry. Rejoining is a brand-new join server-side (fresh
        // player id and state); the Welcome handler upstream resets accordingly.
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            _server = null;

            // A v13+ server's rejection says why, via a ConnectDenied payload
            // whose format is frozen across version gates. A key mismatch is
            // terminal — no retry makes this build compatible — while everything
            // else (full server, plain loss, old server's bare reject) keeps the
            // retry loop alive.
            if (ReadDenial(info) is { } denied)
            {
                RefusalMessage = denied.Message;
                if (denied.ServerKey != NetConfig.ConnectionKey)
                {
                    State = ConnectionState.Incompatible;
                    GD.PrintErr($"Server refused this build: {denied.Message}");
                    return;
                }
            }
            else if (info.Reason == DisconnectReason.ConnectionRejected)
            {
                // A bare reject (pre-v13 server): full or version-skewed, it won't say.
                RefusalMessage = "The server turned us away — it may be full, or running a different build.";
            }

            State = ConnectionState.Disconnected;
            _retryIn = RetrySeconds;
            GD.Print($"Disconnected from server ({info.Reason}) — retrying in {RetrySeconds:0}s.");
        };

        _listener.NetworkReceiveEvent += OnReceive;
    }

    /// <summary>Open the socket and dial the server.</summary>
    public void Start()
    {
        _net.Start();
        Connect();
    }

    /// <summary>Pump network events; drives the retry countdown while disconnected.</summary>
    public void Poll(double delta)
    {
        _net.PollEvents();

        if (State != ConnectionState.Disconnected)
            return;
        _retryIn -= delta;
        if (_retryIn <= 0)
            Connect();
    }

    public void Stop() => _net.Stop();

    /// <summary>Refuse the session from THIS side: the server is healthy, but this
    /// build cannot play what it is hosting (a realm whose scene we don't ship).
    /// Terminal, exactly like a version refusal — retrying cannot conjure a map
    /// that isn't in the build — so it parks in Incompatible and the UI shows
    /// <paramref name="why"/> verbatim.</summary>
    public void RefuseLocally(string why)
    {
        RefusalMessage = why;
        State = ConnectionState.Incompatible;
        Stop();
    }

    /// <summary>Frame and send a packet. Silently dropped while not connected.</summary>
    public void Send(MessageType type, INetSerializable packet, DeliveryMethod delivery) =>
        _server?.Send(NetProtocol.Frame(type, packet), Channel, delivery);

    /// <summary>Ask the server for its live instances; the reply raises <see cref="InstanceListReceived"/>.</summary>
    public void RequestInstances() =>
        Send(MessageType.InstanceListRequest, new InstanceListRequestPacket(), DeliveryMethod.ReliableOrdered);

    /// <summary>Send the join (forge or enter an instance); Welcome or JoinDenied answers it.</summary>
    public void SendJoin(JoinRequest join)
    {
        State = ConnectionState.Joining;
        Send(MessageType.JoinRequest, join, DeliveryMethod.ReliableOrdered);
    }

    private void Connect()
    {
        State = ConnectionState.Connecting;
        _server = _net.Connect(_host, _port, NetConfig.ConnectionKey);
        GD.Print($"Connecting to dedicated server on {_host}:{_port} ...");
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        // The server is trusted, but a version-skewed or corrupt stream must not
        // take the client down mid-frame; drop the connection and let the retry
        // path (plus the ConnectionKey version gate) sort it out.
        try
        {
            var type = (MessageType)reader.GetByte();
            if (_handlers.TryGetValue(type, out var handler))
                handler(reader);
        }
        catch (Exception e)
        {
            GD.PrintErr($"Bad packet from server — disconnecting ({e.GetType().Name}: {e.Message})");
            peer.Disconnect();
        }
    }

    /// <summary>The ConnectDenied payload off a rejection, or null — absent (an old
    /// server, a plain connection loss) and unparseable (hostile wire) look the same.</summary>
    private static ConnectDeniedPacket? ReadDenial(DisconnectInfo info)
    {
        if (info.Reason != DisconnectReason.ConnectionRejected
            || info.AdditionalData is not { AvailableBytes: > 0 } data)
            return null;
        try
        {
            return Read<ConnectDeniedPacket>(data);
        }
        catch
        {
            return null;
        }
    }

    private static T Read<T>(NetDataReader reader) where T : INetSerializable, new()
    {
        var packet = new T();
        packet.Deserialize(reader);
        return packet;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Server;

/// <summary>
/// The authoritative dedicated server. It owns the one true <see cref="GameWorld"/>,
/// steps it at a fixed rate, applies validated client input, and broadcasts
/// snapshots. Clients may only ever send input — the server decides everything else.
/// </summary>
public sealed class GameServer
{
    // All gameplay traffic rides channel 0 for now; delivery method varies per packet.
    private const byte Channel = 0;

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Dictionary<int, Connection> _connections = new(); // per-peer transport state
    private readonly Stopwatch _clock = Stopwatch.StartNew(); // monotonic; drives the loop and rate limits
    private readonly string _mapPath;
    private readonly ServerLog _log = new(); // non-blocking: keeps console I/O off the game loop
    private readonly Dictionary<MessageType, Action<NetPeer, NetPacketReader>> _handlers;
    private GameSession _session = null!; // the authoritative match (world, spawns, input buffers)
    private DungeonGeometryPacket _geometryPacket = null!; // immutable geometry — built once, sent per join
    private volatile bool _running;

    public GameServer(string mapPath)
    {
        _mapPath = mapPath;
        // AutoRecycle returns each NetPacketReader to LiteNetLib's pool after the
        // receive event — without it every received packet allocates fresh garbage.
        _net = new NetManager(_listener) { AutoRecycle = true };

        // Dispatch table for client → server messages. Adding a new message is a line
        // here plus its handler — no growing switch, and each handler stands alone.
        _handlers = new Dictionary<MessageType, Action<NetPeer, NetPacketReader>>
        {
            [MessageType.JoinRequest] = HandleJoin,
            [MessageType.Input] = HandleInput,
            [MessageType.EquipRequest] = HandleEquip,
        };
    }

    /// <summary>Locates the bundled test arena for map-less dev runs (repo root or bin dir).</summary>
    public static string? FindDefaultMap()
    {
        string[] candidates =
        {
            Path.Combine("WoadRaiders.Client", "maps", "TestArena.json"),
            Path.Combine("..", "WoadRaiders.Client", "maps", "TestArena.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WoadRaiders.Client", "maps", "TestArena.json"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Runs the server until stopped. Returns false if startup failed.</summary>
    public bool Run(int port = NetConfig.DefaultPort)
    {
        var started = false;
        try
        {
            // Validate the map first — fail fast with no side effects (no socket, no threads).
            DungeonGeometry dungeon;
            try
            {
                dungeon = DungeonGeometryFile.Load(_mapPath);
            }
            catch (Exception e)
            {
                _log.Error($"Failed to load map '{_mapPath}': {e.Message}");
                return false;
            }
            _log.Info($"Loaded map '{_mapPath}' " +
                      $"({dungeon.Solids.Count} solids, {dungeon.EnemySpawns.Count} enemy spawns" +
                      $"{(dungeon.ScenePath is null ? "" : $", scene {dungeon.ScenePath}")}).");

            // Build the match before opening the socket. The geometry packet is immutable,
            // so it is built once here and reused for every join.
            _geometryPacket = DungeonSnapshot.From(dungeon);
            _session = new GameSession(dungeon, new Random());
            _session.Notice += e => _log.Info(e.Message); // relay match events; no domain knowledge here

            var spawned = _session.SpawnInitial();
            _log.Info($"Spawned {spawned} enemies (map has {dungeon.EnemySpawns.Count} spawn markers).");

            // Only now, with the world fully built, open the socket — so a joining peer
            // can never race a half-initialized server.
            WireEvents();
            if (!_net.Start(port))
            {
                _log.Error($"Failed to bind udp/{port}. Is another server already running?");
                return false;
            }
            started = true;

            _running = true;
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };
            // A container orchestrator stops the process with SIGTERM (docker stop,
            // kubectl delete). Handle it so shutdown runs the same graceful path as
            // Ctrl+C — flush logs, send disconnects — instead of being hard-killed.
            using var sigterm = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; _running = false; });
            // Raise the OS timer resolution so the loop's Sleep(1) wakes ~1 ms late, not
            // ~15 ms — otherwise 30 Hz ticks arrive in bursts. Released on shutdown.
            using var frameTimer = FrameTimer.HighResolution();

            _log.Info(
                $"WoadRaiders dedicated server listening on udp/{port} " +
                $"(sim {SimConstants.TickRate}Hz, snapshots {NetConfig.SnapshotsPerSecond}Hz). Ctrl+C to stop.");

            Loop();
            return true;
        }
        finally
        {
            // The same graceful shutdown whether Loop returned or threw: close the socket
            // (clients get a disconnect, not a timeout), then flush and stop the logger.
            if (started)
            {
                _net.Stop(sendDisconnectMessages: true);
                _log.Info("Server stopped.");
            }
            _log.Dispose();
        }
    }

    private void WireEvents()
    {
        _listener.ConnectionRequestEvent += request =>
        {
            if (_net.ConnectedPeersCount < NetConfig.MaxPlayers)
                request.AcceptIfKey(NetConfig.ConnectionKey);
            else
                request.Reject();
        };

        _listener.PeerConnectedEvent += peer =>
        {
            _connections[peer.Id] = new Connection(peer, _clock.Elapsed);
            _log.Info($"[+] Player {peer.Id} connected  ({_net.ConnectedPeersCount} online)");

            // The dungeon can start crossing the wire now; the player itself is
            // spawned by HandleJoin, once the join request says who they are —
            // so no snapshot ever shows a placeholder body with the wrong class.
            peer.Send(NetProtocol.Frame(MessageType.DungeonGeometry, _geometryPacket), Channel, DeliveryMethod.ReliableOrdered);
        };

        _listener.PeerDisconnectedEvent += (peer, _) =>
        {
            _connections.Remove(peer.Id);
            _session.RemovePlayer(peer.Id);
            _log.Info($"[-] Player {peer.Id} left    ({_net.ConnectedPeersCount} online)");
        };

        _listener.NetworkReceiveEvent += OnReceive;
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        // Cap how fast any one peer can make the server work. Legit traffic sits far
        // under the budget; a flooder's excess is dropped rather than processed.
        if (_connections.TryGetValue(peer.Id, out var connection) && !connection.AllowMessage(_clock.Elapsed))
            return;

        // The wire is hostile territory: a truncated or malicious packet must never
        // take the server down. Anything that fails to parse costs the sender their
        // connection, nothing more.
        try
        {
            HandleMessage(peer, reader);
        }
        catch (Exception e)
        {
            _log.Error($"[!] Bad packet from player {peer.Id} — disconnecting ({e.GetType().Name}: {e.Message})");
            peer.Disconnect();
        }
    }

    private void HandleMessage(NetPeer peer, NetPacketReader reader)
    {
        var type = (MessageType)reader.GetByte();
        // One handler per client-sendable message. Unknown/unexpected types are ignored
        // (a client-only or unregistered type is not a reason to drop the connection).
        if (_handlers.TryGetValue(type, out var handler))
            handler(peer, reader);
    }

    private void HandleJoin(NetPeer peer, NetPacketReader reader)
    {
        var join = new JoinRequest();
        join.Deserialize(reader);
        // Class is client-supplied and untrusted like the name: an unknown byte
        // (version skew, tampering) falls back to Knight rather than faulting.
        var cls = join.Class <= (byte)CharacterClass.Ranger ? (CharacterClass)join.Class : CharacterClass.Knight;
        var name = SanitizeName(join.Name, peer.Id);
        _session.AddPlayer(peer.Id, name, cls);

        // The welcome follows the join, so by the time the client starts playing
        // its player exists — classed — in every snapshot from the first one.
        var welcome = new WelcomePacket { PlayerId = peer.Id, ServerTick = _session.Tick };
        peer.Send(NetProtocol.Frame(MessageType.Welcome, welcome), Channel, DeliveryMethod.ReliableOrdered);
        _log.Info($"[join] Player {peer.Id} raids as a {cls} named \"{name}\"");
    }

    private void HandleInput(NetPeer peer, NetPacketReader reader)
    {
        var input = new InputPacket();
        input.Deserialize(reader);
        // Buffer the intent; the session applies it one-per-tick, in order. Trust
        // nothing but the intent — the simulation clamps/normalizes it.
        _session.EnqueueInput(peer.Id, new PlayerInput
        {
            MoveX = input.MoveX,
            MoveZ = input.MoveZ,
            AimX = input.AimX,
            AimZ = input.AimZ,
            Sequence = input.Sequence,
            Attack = input.Attack,
        });
    }

    private void HandleEquip(NetPeer peer, NetPacketReader reader)
    {
        var req = new EquipRequestPacket();
        req.Deserialize(reader);
        if (_session.TryEquip(peer.Id, req.ItemId) is not { } equipped)
            return;

        var packet = new EquipmentUpdatePacket
        {
            WeaponItemId = equipped.WeaponId,
            ArmorItemId = equipped.ArmorId,
            TrinketItemId = equipped.TrinketId,
        };
        peer.Send(NetProtocol.Frame(MessageType.EquipmentUpdate, packet), Channel, DeliveryMethod.ReliableOrdered);
        _log.Info($"[equip] Player {peer.Id} equipment: " +
                  $"W{equipped.WeaponId} A{equipped.ArmorId} T{equipped.TrinketId} " +
                  $"(atk {equipped.AttackDamage:0}, armor {equipped.DamageReduction:0.0})");
    }

    /// <summary>Client-supplied names are untrusted: strip control chars, cap the length.</summary>
    internal static string SanitizeName(string name, int playerId)
    {
        const int maxLength = 24;
        var cleaned = string.Concat(name.Where(c => !char.IsControl(c))).Trim();
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength];
        return cleaned.Length == 0 ? $"Raider-{playerId}" : cleaned;
    }

    private void Loop()
    {
        var scheduler = new FixedTimestepScheduler(
            TimeSpan.FromSeconds(SimConstants.TickDelta),
            TimeSpan.FromSeconds(1.0 / NetConfig.SnapshotsPerSecond),
            maxCatchUpTicks: 10, // ~1/3 s at 30 Hz — bounds catch-up after a stall
            start: _clock.Elapsed);

        while (_running)
        {
            _net.PollEvents();

            var plan = scheduler.Advance(_clock.Elapsed);

            // The session applies one buffered input per player and advances spawn
            // policy each tick, so it rides out a stall cleanly under the catch-up bound.
            for (var i = 0; i < plan.Ticks; i++)
                _session.Step();
            if (plan.Stalled)
                _log.Info($"[!] Sim stalled {plan.Dropped.TotalMilliseconds:0} ms beyond the catch-up cap — dropping the lost time.");

            DeliverPickups();

            if (plan.Snapshot)
                BroadcastSnapshot();

            // Yield a slice so we poll often (low input latency) without busy-waiting.
            // With the raised timer resolution this wakes in ~1-2 ms, not ~15 ms.
            Thread.Sleep(1);
        }
    }

    private void BroadcastSnapshot()
    {
        if (_connections.Count == 0)
            return;

        // Unreliable delivery tops out at one MTU and never fragments, while the
        // world (and so the snapshot) grows without bound — so each snapshot is
        // split into as many packets as it needs, sized to the smallest connected
        // peer's limit so one send plan fits everyone. The client's assembler
        // rebuilds it and drops stale ticks, which is what Sequenced used to buy
        // us; losing any chunk just costs that one snapshot.
        var budget = int.MaxValue;
        foreach (var connection in _connections.Values)
            budget = Math.Min(budget, connection.Peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable));

        foreach (var chunk in SnapshotChunks.Split(_session.Snapshot(), budget))
            _net.SendToAll(chunk, Channel, DeliveryMethod.Unreliable);
    }

    private void DeliverPickups()
    {
        foreach (var pickup in _session.ConsumePickups())
        {
            if (!_connections.TryGetValue(pickup.PlayerId, out var connection))
                continue;

            var item = pickup.Item; // null for gold and potions
            var packet = new ItemPickedUpPacket
            {
                Kind = (byte)pickup.Kind,
                Amount = pickup.Amount,
                ItemId = item?.Id ?? 0,
                Name = item?.Name ?? "",
                Rarity = (byte)(item?.Rarity ?? ItemRarity.Common),
                Type = (byte)(item?.Type ?? default(ItemType)),
                Power = item?.Power ?? 0,
            };
            connection.Peer.Send(NetProtocol.Frame(MessageType.ItemPickedUp, packet), Channel, DeliveryMethod.ReliableOrdered);
            _log.Info(pickup.Kind switch
            {
                LootKind.Gold => $"[loot] Player {pickup.PlayerId} picked up {pickup.Amount} gold",
                LootKind.HealthPotion => $"[loot] Player {pickup.PlayerId} drank a potion (+{pickup.Amount} health)",
                _ => $"[loot] Player {pickup.PlayerId} picked up {item!.Name} (power {item.Power})",
            });
        }
    }

}

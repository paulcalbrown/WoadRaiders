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
    private readonly GameWorld _world = new();
    private readonly Random _rng = new();
    private readonly Dictionary<int, Connection> _connections = new(); // per-peer transport + input buffer
    private readonly string _mapPath;
    private readonly ServerLog _log = new(); // non-blocking: keeps console I/O off the game loop
    private readonly Dictionary<MessageType, Action<NetPeer, NetPacketReader>> _handlers;
    private DungeonGeometry _dungeon = null!;
    private DungeonGeometryPacket _geometryPacket = null!; // immutable geometry — built once, sent per join
    private SpawnDirector _director = null!; // owns enemy population + boss lifecycle (Core policy)
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
            try
            {
                _dungeon = DungeonGeometryFile.Load(_mapPath);
            }
            catch (Exception e)
            {
                _log.Error($"Failed to load map '{_mapPath}': {e.Message}");
                return false;
            }
            _log.Info($"Loaded map '{_mapPath}' " +
                      $"({_dungeon.Solids.Count} solids, {_dungeon.EnemySpawns.Count} enemy spawns" +
                      $"{(_dungeon.ScenePath is null ? "" : $", scene {_dungeon.ScenePath}")}).");

            // Build the world before opening the socket. The geometry packet is immutable,
            // so it is built once here and reused for every join.
            _world.Geometry = _dungeon;
            _geometryPacket = DungeonSnapshot.From(_dungeon);

            _director = new SpawnDirector(_world, _dungeon, _rng);
            _director.BossFell += () => _log.Info(
                $"[boss] The Barrow King has fallen! He returns in {SpawnDirector.BossRespawnDelayTicks / SimConstants.TickRate}s.");
            _director.BossRose += () => _log.Info("[boss] The Barrow King rises again.");

            var spawned = _director.SpawnInitial();
            _log.Info($"Spawned {spawned} enemies (map has {_dungeon.TypedEnemySpawns.Count} spawn markers).");
            if (_dungeon.BossSpawn is not null)
                _log.Info("The Barrow King waits in his chamber.");

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
            _connections[peer.Id] = new Connection(peer);
            var player = _world.AddPlayer(peer.Id, $"Raider-{peer.Id}");
            player.Position = _dungeon.SpawnPoint;
            _log.Info($"[+] Player {peer.Id} joined  ({_net.ConnectedPeersCount} online)");

            // Send the dungeon first, then the welcome (both reliable-ordered on the same channel).
            peer.Send(NetProtocol.Frame(MessageType.DungeonGeometry, _geometryPacket), Channel, DeliveryMethod.ReliableOrdered);
            var welcome = new WelcomePacket { PlayerId = peer.Id, ServerTick = _world.Tick };
            peer.Send(NetProtocol.Frame(MessageType.Welcome, welcome), Channel, DeliveryMethod.ReliableOrdered);
        };

        _listener.PeerDisconnectedEvent += (peer, _) =>
        {
            _connections.Remove(peer.Id);
            _world.RemovePlayer(peer.Id);
            _log.Info($"[-] Player {peer.Id} left    ({_net.ConnectedPeersCount} online)");
        };

        _listener.NetworkReceiveEvent += OnReceive;
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
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
        if (_world.Players.TryGetValue(peer.Id, out var player))
            player.Name = SanitizeName(join.Name, peer.Id);
    }

    private void HandleInput(NetPeer peer, NetPacketReader reader)
    {
        var input = new InputPacket();
        input.Deserialize(reader);
        // Buffer the intent; it is applied one-per-tick, in order, by the sim loop
        // (see ApplyBufferedInputs). Trust nothing but the intent — the simulation
        // clamps/normalizes it.
        if (_connections.TryGetValue(peer.Id, out var connection))
            connection.Buffer.Enqueue(new PlayerInput
            {
                MoveX = input.MoveX,
                MoveZ = input.MoveZ,
                Sequence = input.Sequence,
                Attack = input.Attack,
            });
    }

    private void HandleEquip(NetPeer peer, NetPacketReader reader)
    {
        var req = new EquipRequestPacket();
        req.Deserialize(reader);
        if (_world.Players.TryGetValue(peer.Id, out var player) && player.TryEquip(req.ItemId))
            SendEquipment(peer, player);
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
            maxCatchUpTicks: 10); // ~1/3 s at 30 Hz — bounds catch-up after a stall

        var clock = Stopwatch.StartNew();
        while (_running)
        {
            _net.PollEvents();

            var plan = scheduler.Advance(clock.Elapsed);

            // Spawn policy advances with the sim (one Update per tick), so it too rides
            // out a stall cleanly under the catch-up bound.
            for (var i = 0; i < plan.Ticks; i++)
            {
                ApplyBufferedInputs();
                _world.Step();
                _director.Update();
            }
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

    /// <summary>
    /// Hand the simulation exactly one buffered input per player for this tick, in
    /// sequence order, so the server replays each client's input stream 1:1 and
    /// reconciliation stays drift-free. A priming or starved buffer <em>holds</em>
    /// the player — a zero-move input tagged with their last processed sequence, so
    /// <c>LastProcessedInput</c> never regresses and the client reconciles the freeze
    /// exactly instead of fighting a re-applied stale input.
    /// </summary>
    private void ApplyBufferedInputs()
    {
        foreach (var player in _world.Players.Values)
        {
            if (_connections.TryGetValue(player.Id, out var connection) && connection.Buffer.TryDequeue(out var input))
                _world.SetInput(player.Id, input);
            else
                _world.SetInput(player.Id, new PlayerInput { Sequence = player.LastProcessedInput });
        }
    }

    private void BroadcastSnapshot()
    {
        if (_net.ConnectedPeersCount == 0)
            return;

        var snapshot = WorldSnapshot.From(_world);

        // Sequenced: unreliable but never delivers a stale snapshot after a newer one.
        _net.SendToAll(NetProtocol.Frame(MessageType.WorldSnapshot, snapshot), Channel, DeliveryMethod.Sequenced);
    }

    private void SendEquipment(NetPeer peer, PlayerState player)
    {
        var packet = new EquipmentUpdatePacket
        {
            WeaponItemId = EquippedId(player, EquipSlot.Weapon),
            ArmorItemId = EquippedId(player, EquipSlot.Armor),
            TrinketItemId = EquippedId(player, EquipSlot.Trinket),
        };
        peer.Send(NetProtocol.Frame(MessageType.EquipmentUpdate, packet), Channel, DeliveryMethod.ReliableOrdered);
        _log.Info($"[equip] Player {player.Id} equipment: " +
                  $"W{packet.WeaponItemId} A{packet.ArmorItemId} T{packet.TrinketItemId} " +
                  $"(atk {player.AttackDamage:0}, armor {player.DamageReduction:0.0})");
    }

    private static int EquippedId(PlayerState player, EquipSlot slot) =>
        player.Equipped.TryGetValue(slot, out var item) ? item.Id : 0;

    private void DeliverPickups()
    {
        foreach (var pickup in _world.ConsumePickups())
        {
            if (!_connections.TryGetValue(pickup.PlayerId, out var connection))
                continue;

            var item = pickup.Item;
            var packet = new ItemPickedUpPacket
            {
                ItemId = item.Id,
                Name = item.Name,
                Rarity = (byte)item.Rarity,
                Type = (byte)item.Type,
                Power = item.Power,
            };
            connection.Peer.Send(NetProtocol.Frame(MessageType.ItemPickedUp, packet), Channel, DeliveryMethod.ReliableOrdered);
            _log.Info($"[loot] Player {pickup.PlayerId} picked up {item.Name} (power {item.Power})");
        }
    }

}

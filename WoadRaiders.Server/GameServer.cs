using System.Diagnostics;
using System.Runtime.InteropServices;
using LiteNetLib;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Server;

/// <summary>
/// The authoritative dedicated server. It loads every shipping dungeon's map at
/// startup, then hosts player-forged <em>instances</em> of them: a join request
/// either forges a fresh instance — its own <see cref="GameSession"/> with its own
/// enemy population — or enters a live one by id, so separate warbands never share
/// a world. The server steps every live instance at a fixed rate, applies validated
/// client input, and broadcasts each instance's snapshots to the peers inside it.
/// An emptied instance lingers briefly (a disconnected raider can rejoin), then is
/// reaped. Clients may only ever send input — the server decides everything else.
/// </summary>
public sealed class GameServer
{
    // All gameplay traffic rides channel 0 for now; delivery method varies per packet.
    private const byte Channel = 0;

    /// <summary>An empty instance lingers this long (a disconnect-retry can rejoin), then is reaped.</summary>
    private static readonly TimeSpan EmptyLinger = TimeSpan.FromSeconds(60);

    /// <summary>One loaded map: its display name, parsed geometry, and the immutable packet sent on join.</summary>
    private sealed record LoadedMap(string Name, RealmDefinition Realm, IRealmGeometry? Movement,
                                    RealmGeometryPacket Packet);

    /// <summary>One live raid: a player-forged run of a dungeon with its own world.</summary>
    private sealed class Instance(
        int id, DungeonId dungeon, string dungeonName, string name,
        GameSession session, RealmGeometryPacket geometry)
    {
        public int Id { get; } = id;
        public DungeonId Dungeon { get; } = dungeon;
        public string DungeonName { get; } = dungeonName;
        public string Name { get; } = name;
        public GameSession Session { get; } = session;
        public RealmGeometryPacket Geometry { get; } = geometry;

        /// <summary>Live connections bound to this instance.</summary>
        public int Players;

        /// <summary>When the last raider left — starts the reaper countdown. Null while occupied.</summary>
        public TimeSpan? EmptySince;
    }

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Dictionary<int, Connection> _connections = new(); // per-peer transport state
    private readonly Stopwatch _clock = Stopwatch.StartNew(); // monotonic; drives the loop and rate limits
    private readonly IReadOnlyDictionary<DungeonId, string> _mapPaths;
    private readonly ServerLog _log = new(); // non-blocking: keeps console I/O off the game loop
    private readonly Dictionary<MessageType, Action<NetPeer, NetPacketReader>> _handlers;
    private readonly Dictionary<DungeonId, LoadedMap> _maps = new(); // every map this server can instantiate
    private readonly Dictionary<int, Instance> _instances = new();   // every live instance, by id
    private readonly List<NetPeer> _peerScratch = new(); // reused per-instance send list
    private int _nextInstanceId = 1;
    private volatile bool _running;

    public GameServer(IReadOnlyDictionary<DungeonId, string> mapPaths)
    {
        _mapPaths = mapPaths;
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
            [MessageType.InstanceListRequest] = HandleInstanceList,
        };
    }

    /// <summary>
    /// The maps directory: always beside the binary. The csproj copies the
    /// canonical JSON (WoadRaiders.Client/maps) there on every build and publish,
    /// so this holds wherever the server runs from — no fallback paths.
    /// </summary>
    public static string MapsDirectory => Path.Combine(AppContext.BaseDirectory, "maps");

    /// <summary>Runs the server until stopped. Returns false if startup failed.</summary>
    public bool Run(int port = NetConfig.DefaultPort)
    {
        var started = false;
        try
        {
            // Validate and load every map first — fail fast with no side effects
            // (no socket, no threads). Each geometry packet is immutable, so it is
            // built once here and shared by every instance forged from that map.
            foreach (var (id, mapPath) in _mapPaths)
            {
                RealmDefinition realm;
                IRealmGeometry? movement = null;
                byte[]? navMesh = null;
                try
                {
                    realm = RealmDefinitionFile.Load(mapPath);
                    // Built realms get their navmesh baked HERE, once — the sim
                    // moves on it and every client receives these exact bytes, so
                    // no peer ever bakes its own (identical polygons everywhere).
                    // The boss-width mesh stays server-side: only the server
                    // moves the boss. Soupless maps keep the flat-arena rules.
                    if (realm.Soup is { } soup)
                    {
                        var characters = NavMeshBuilder.BuildMeshData(soup);
                        var boss = NavMeshBuilder.BuildMeshData(soup, EnemyArchetypes.Of(EnemyType.Boss).Radius);
                        navMesh = NavMeshBuilder.Serialize(characters);
                        movement = new RealmGeometry(soup, realm.SpawnPoint,
                            (SimConstants.CharacterRadius, NavMeshBuilder.ToNavMesh(characters)),
                            (EnemyArchetypes.Of(EnemyType.Boss).Radius, NavMeshBuilder.ToNavMesh(boss)));
                    }
                }
                catch (Exception e)
                {
                    _log.Error($"Failed to load map '{mapPath}': {e.Message}");
                    return false;
                }

                var name = Path.GetFileNameWithoutExtension(mapPath);
                _maps[id] = new LoadedMap(name, realm, movement, RealmSnapshot.From(realm, navMesh));
                _log.Info($"[map] Loaded '{mapPath}' " +
                          $"({(realm.Soup is { } s ? $"{s.Triangles.Length / 3} triangles" : "flat arena")}, " +
                          $"{realm.EnemySpawns.Count} spawn markers" +
                          $"{(navMesh is null ? "" : $", {navMesh.Length / 1024} KB navmesh")}).");
            }

            // Only now, with every map loaded, open the socket — so a joining peer
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
                $"({_maps.Count} maps, up to {NetConfig.MaxInstances} instances of " +
                $"{NetConfig.MaxPlayersPerInstance} raiders; " +
                $"sim {SimConstants.TickRate}Hz, snapshots {NetConfig.SnapshotsPerSecond}Hz). Ctrl+C to stop.");

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
            // Refusals say why: a ConnectDenied payload rides the connection-layer
            // reject, so even a build this server won't talk to can read it (the
            // payload's format is frozen). The version check runs first — when a
            // stale client hits a full server, "update your build" is the answer
            // that still helps once a slot frees up.
            if (!request.Data.TryGetString(out var clientKey) || clientKey != NetConfig.ConnectionKey)
            {
                _log.Info($"[!] Refused {request.RemoteEndPoint}: client build \"{clientKey}\" (server is {NetConfig.ConnectionKey})");
                request.Reject(ConnectDenied(
                    $"This server runs {NetConfig.ConnectionKey}, which cannot talk to your build — " +
                    $"get the latest at {NetConfig.DownloadUrl}"));
            }
            else if (_net.ConnectedPeersCount >= NetConfig.MaxConnections)
            {
                _log.Info($"[!] Refused {request.RemoteEndPoint}: server full ({_net.ConnectedPeersCount} online)");
                request.Reject(ConnectDenied("Every raid slot is taken — try again shortly."));
            }
            else
            {
                request.Accept();
            }
        };

        _listener.PeerConnectedEvent += peer =>
        {
            // Which instance (and who) arrives with the join request; geometry,
            // welcome, and the player spawn all wait for it in HandleJoin, so no
            // snapshot ever shows a placeholder body in the wrong instance. Until
            // then the peer may only browse the instance list.
            _connections[peer.Id] = new Connection(peer, _clock.Elapsed);
            _log.Info($"[+] Player {peer.Id} connected  ({_net.ConnectedPeersCount} online)");
        };

        _listener.PeerDisconnectedEvent += (peer, _) =>
        {
            if (_connections.Remove(peer.Id, out var connection) && connection.Instance is { } id
                && _instances.TryGetValue(id, out var instance))
            {
                instance.Session.RemovePlayer(peer.Id);
                instance.Players--;
                if (instance.Players <= 0)
                    instance.EmptySince = _clock.Elapsed; // the reaper takes it from here
            }
            _log.Info($"[-] Player {peer.Id} left    ({_net.ConnectedPeersCount} online)");
        };

        _listener.NetworkReceiveEvent += OnReceive;
    }

    /// <summary>Frame a connect refusal for <see cref="ConnectionRequest.Reject(NetDataWriter)"/>.</summary>
    private static NetDataWriter ConnectDenied(string message)
    {
        var writer = new NetDataWriter();
        new ConnectDeniedPacket { ServerKey = NetConfig.ConnectionKey, Message = message }.Serialize(writer);
        return writer;
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

    /// <summary>The session the peer's connection is bound to, or null before its join lands.</summary>
    private GameSession? SessionOf(Connection connection) =>
        connection.Instance is { } id && _instances.TryGetValue(id, out var instance) ? instance.Session : null;

    private GameSession? SessionOf(NetPeer peer) =>
        _connections.TryGetValue(peer.Id, out var connection) ? SessionOf(connection) : null;

    private void HandleJoin(NetPeer peer, NetPacketReader reader)
    {
        var join = new JoinRequest();
        join.Deserialize(reader);
        if (!_connections.TryGetValue(peer.Id, out var connection))
            return;

        // Class, mode, and instance fields are client-supplied and untrusted like
        // the name: an unknown byte (version skew, tampering) falls back to the
        // default rather than faulting. A repeated join must not move a live
        // player between instances (that would be a free escape) — the first
        // binding sticks and a re-join renames at most.
        var cls = join.Class <= (byte)CharacterClass.Ranger ? (CharacterClass)join.Class : CharacterClass.Knight;
        var name = SanitizeName(join.Name, peer.Id);
        var instance = connection.Instance is { } bound
            ? _instances[bound]
            : (JoinMode)join.Mode == JoinMode.Join
                ? TryJoinExisting(peer, join.InstanceId)
                : TryForge(peer, join, name);
        if (instance is null)
            return; // denied — the JoinDenied packet is already on its way

        if (connection.Instance is null)
        {
            connection.Instance = instance.Id;
            instance.Players++;
            instance.EmptySince = null; // a lingering-empty instance is live again
        }
        instance.Session.AddPlayer(peer.Id, name, cls);

        // Geometry first, then the welcome (both reliable-ordered on the same
        // channel), so by the time the client starts playing its player exists —
        // classed, in its chosen instance — in every snapshot from the first one.
        peer.Send(NetProtocol.Frame(MessageType.RealmGeometry, instance.Geometry), Channel, DeliveryMethod.ReliableOrdered);
        var welcome = new WelcomePacket { PlayerId = peer.Id, ServerTick = instance.Session.Tick, InstanceId = instance.Id };
        peer.Send(NetProtocol.Frame(MessageType.Welcome, welcome), Channel, DeliveryMethod.ReliableOrdered);
        _log.Info($"[join] Player {peer.Id} enters \"{instance.Name}\" (#{instance.Id}, {instance.DungeonName}) " +
                  $"as a {cls} named \"{name}\" — {instance.Players}/{NetConfig.MaxPlayersPerInstance} raiders");
    }

    /// <summary>Look up a live instance to join, or deny (gone/full) and return null.</summary>
    private Instance? TryJoinExisting(NetPeer peer, int instanceId)
    {
        if (!_instances.TryGetValue(instanceId, out var instance))
        {
            Deny(peer, JoinDenyReason.InstanceGone, $"asked for instance #{instanceId}, which is gone");
            return null;
        }
        if (instance.Players >= NetConfig.MaxPlayersPerInstance)
        {
            Deny(peer, JoinDenyReason.InstanceFull, $"asked for instance #{instanceId} (\"{instance.Name}\"), which is full");
            return null;
        }
        return instance;
    }

    /// <summary>Forge a fresh instance of the requested dungeon, or deny (server full) and return null.</summary>
    private Instance? TryForge(NetPeer peer, JoinRequest join, string playerName)
    {
        if (_instances.Count >= NetConfig.MaxInstances)
        {
            Deny(peer, JoinDenyReason.ServerFull, "tried to forge an instance, but the server is at its cap");
            return null;
        }

        var requested = DungeonCatalog.Sanitize(join.Dungeon);
        if (!_maps.TryGetValue(requested, out var map))
            (requested, map) = _maps.First(); // a custom --map run hosts one map; every forge uses it

        var id = _nextInstanceId++;
        var session = new GameSession(map.Realm, new Random(), map.Movement);
        session.Notice += e => _log.Info($"[{map.Name}#{id}] {e.Message}"); // relay match events; no domain knowledge here
        var spawned = session.SpawnInitial();

        var instance = new Instance(id, requested, map.Name, SanitizeInstanceName(join.InstanceName, playerName),
                                    session, map.Packet);
        _instances[id] = instance;
        _log.Info($"[forge] Instance #{id} \"{instance.Name}\" of {map.Name} lit " +
                  $"({spawned} foes; {_instances.Count}/{NetConfig.MaxInstances} live)");
        return instance;
    }

    /// <summary>Refuse a join but keep the connection — the client returns to its raid browser.</summary>
    private void Deny(NetPeer peer, JoinDenyReason reason, string detail)
    {
        peer.Send(NetProtocol.Frame(MessageType.JoinDenied, new JoinDeniedPacket { Reason = (byte)reason }),
            Channel, DeliveryMethod.ReliableOrdered);
        _log.Info($"[deny] Player {peer.Id} {detail}");
    }

    /// <summary>Send the peer every live instance, so it can offer a join-or-forge choice.</summary>
    private void HandleInstanceList(NetPeer peer, NetPacketReader reader)
    {
        var list = new InstanceListPacket
        {
            Instances = _instances.Values
                .OrderBy(i => i.Id)
                .Select(i => new InstanceEntry
                {
                    Id = i.Id,
                    Dungeon = (byte)i.Dungeon,
                    Name = i.Name,
                    Players = (byte)Math.Clamp(i.Players, 0, byte.MaxValue),
                    MaxPlayers = NetConfig.MaxPlayersPerInstance,
                })
                .ToArray(),
        };
        peer.Send(NetProtocol.Frame(MessageType.InstanceList, list), Channel, DeliveryMethod.ReliableOrdered);
    }

    private void HandleInput(NetPeer peer, NetPacketReader reader)
    {
        var input = new InputPacket();
        input.Deserialize(reader);
        // Buffer the intent; the session applies it one-per-tick, in order. Trust
        // nothing but the intent — the simulation clamps/normalizes it.
        SessionOf(peer)?.EnqueueInput(peer.Id, new PlayerInput
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
        if (SessionOf(peer)?.TryEquip(peer.Id, req.ItemId) is not { } equipped)
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

    /// <summary>Instance names are untrusted like player names; an empty one is named for its founder.</summary>
    internal static string SanitizeInstanceName(string name, string playerName)
    {
        const int maxLength = 40;
        var cleaned = string.Concat(name.Where(c => !char.IsControl(c))).Trim();
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength];
        return cleaned.Length == 0 ? $"{playerName}'s raid" : cleaned;
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

            // Every instance steps in lockstep: each session applies one buffered
            // input per player and advances its spawn policy per tick, so they all
            // ride out a stall cleanly under the catch-up bound.
            for (var i = 0; i < plan.Ticks; i++)
                foreach (var instance in _instances.Values)
                    instance.Session.Step();
            if (plan.Stalled)
                _log.Info($"[!] Sim stalled {plan.Dropped.TotalMilliseconds:0} ms beyond the catch-up cap — dropping the lost time.");

            foreach (var instance in _instances.Values)
            {
                DeliverPickups(instance.Session);
                DeliverPortalExits(instance);
            }

            if (plan.Snapshot)
            {
                foreach (var (id, instance) in _instances)
                    BroadcastSnapshot(id, instance.Session);
                ReapEmptyInstances(); // snapshot cadence is plenty for a 60 s linger
            }

            // Yield a slice so we poll often (low input latency) without busy-waiting.
            // With the raised timer resolution this wakes in ~1-2 ms, not ~15 ms.
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Walk each raider who stepped through the boss portal out of their instance:
    /// send their run summary, then unbind the connection (it survives — the client
    /// moves on to its summary screen and disconnects, or browses anew). The sim
    /// already removed their player, so no snapshot shows them again.
    /// </summary>
    private void DeliverPortalExits(Instance instance)
    {
        foreach (var report in instance.Session.ConsumePortalExits())
        {
            if (!_connections.TryGetValue(report.PlayerId, out var connection)
                || connection.Instance != instance.Id)
                continue; // exited and vanished in the same beat — nothing to tell

            var packet = new RunCompletePacket
            {
                Dungeon = (byte)instance.Dungeon,
                RaidName = instance.Name,
                DurationSeconds = report.DurationSeconds,
                Gold = report.Gold,
                ItemsLooted = report.ItemsLooted,
                FoesSlain = report.FoesSlain,
            };
            connection.Peer.Send(NetProtocol.Frame(MessageType.RunComplete, packet), Channel, DeliveryMethod.ReliableOrdered);

            connection.Instance = null;
            instance.Players--;
            if (instance.Players <= 0)
                instance.EmptySince = _clock.Elapsed; // the reaper takes it from here

            _log.Info($"[exit] Player {report.PlayerId} \"{report.PlayerName}\" stepped through the portal " +
                      $"out of \"{instance.Name}\" (#{instance.Id}) after {report.DurationSeconds}s — " +
                      $"{report.Gold} gold, {report.ItemsLooted} relics, {report.FoesSlain} foes felled by the warband");
        }
    }

    /// <summary>Remove instances that have stood empty past the linger window.</summary>
    private void ReapEmptyInstances()
    {
        List<Instance>? doomed = null;
        foreach (var instance in _instances.Values)
            if (instance is { Players: <= 0, EmptySince: { } since } && _clock.Elapsed - since >= EmptyLinger)
                (doomed ??= new List<Instance>()).Add(instance);
        if (doomed is null)
            return;

        foreach (var instance in doomed)
        {
            _instances.Remove(instance.Id);
            _log.Info($"[reap] Instance #{instance.Id} \"{instance.Name}\" stood empty " +
                      $"{EmptyLinger.TotalSeconds:0} s — snuffed ({_instances.Count}/{NetConfig.MaxInstances} live)");
        }
    }

    private void BroadcastSnapshot(int instanceId, GameSession session)
    {
        // Only the peers inside this instance receive its snapshots.
        _peerScratch.Clear();
        var budget = int.MaxValue;
        foreach (var connection in _connections.Values)
        {
            if (connection.Instance != instanceId)
                continue;
            _peerScratch.Add(connection.Peer);
            budget = Math.Min(budget, connection.Peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable));
        }
        if (_peerScratch.Count == 0)
            return;

        // Unreliable delivery tops out at one MTU and never fragments, while the
        // world (and so the snapshot) grows without bound — so each snapshot is
        // split into as many packets as it needs, sized to the smallest listening
        // peer's limit so one send plan fits the whole group. The client's
        // assembler rebuilds it and drops stale ticks; losing any chunk just
        // costs that one snapshot.
        foreach (var chunk in SnapshotChunks.Split(session.Snapshot(), budget))
            foreach (var peer in _peerScratch)
                peer.Send(chunk, Channel, DeliveryMethod.Unreliable);
    }

    private void DeliverPickups(GameSession session)
    {
        foreach (var pickup in session.ConsumePickups())
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

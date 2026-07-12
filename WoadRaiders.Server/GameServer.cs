using System.Diagnostics;
using System.Numerics;
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
    private readonly Dictionary<int, NetPeer> _peers = new();
    private readonly Dictionary<int, ServerInputBuffer> _inputBuffers = new();
    private readonly string _mapPath;
    private DungeonGeometry _dungeon = null!;
    private volatile bool _running;

    // Enemy population policy — map-driven: authors control density and mix by
    // placing typed EnemySpawn markers (target = 1 enemy per marker, clamped).
    // The boss is tracked separately and never counts toward the population.
    private int _targetEnemyCount;
    private static readonly TimeSpan EnemyRespawnInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan BossRespawnDelay = TimeSpan.FromSeconds(120);
    private TimeSpan? _bossRespawnAt; // set once when the boss dies; null while alive

    public GameServer(string mapPath)
    {
        _mapPath = mapPath;
        // AutoRecycle returns each NetPacketReader to LiteNetLib's pool after the
        // receive event — without it every received packet allocates fresh garbage.
        _net = new NetManager(_listener) { AutoRecycle = true };
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
        // Validate the map before binding the port — fail fast with no side effects.
        try
        {
            _dungeon = DungeonGeometryFile.Load(_mapPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to load map '{_mapPath}': {e.Message}");
            return false;
        }

        WireEvents();

        if (!_net.Start(port))
        {
            Console.Error.WriteLine($"Failed to bind udp/{port}. Is another server already running?");
            return false;
        }

        _running = true;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };

        Console.WriteLine(
            $"WoadRaiders dedicated server listening on udp/{port} " +
            $"(sim {SimConstants.TickRate}Hz, snapshots {NetConfig.SnapshotsPerSecond}Hz). Ctrl+C to stop.");
        Console.WriteLine($"Loaded map '{_mapPath}' " +
                          $"({_dungeon.Solids.Count} solids, {_dungeon.EnemySpawns.Count} enemy spawns" +
                          $"{(_dungeon.ScenePath is null ? "" : $", scene {_dungeon.ScenePath}")}).");
        _world.Geometry = _dungeon;
        _targetEnemyCount = Math.Clamp(_dungeon.EnemySpawns.Count, 4, 40);

        SpawnInitialEnemies();

        Loop();

        _net.Stop(sendDisconnectMessages: true); // clients see a clean disconnect, not a timeout
        Console.WriteLine("Server stopped.");
        return true;
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
            _peers[peer.Id] = peer;
            _inputBuffers[peer.Id] = new ServerInputBuffer();
            var player = _world.AddPlayer(peer.Id, $"Raider-{peer.Id}");
            player.Position = _dungeon.SpawnPoint;
            Console.WriteLine($"[+] Player {peer.Id} joined  ({_net.ConnectedPeersCount} online)");

            // Send the dungeon first, then the welcome (both reliable-ordered on the same channel).
            peer.Send(NetProtocol.Frame(MessageType.DungeonGeometry, BuildGeometryPacket()), Channel, DeliveryMethod.ReliableOrdered);
            var welcome = new WelcomePacket { PlayerId = peer.Id, ServerTick = _world.Tick };
            peer.Send(NetProtocol.Frame(MessageType.Welcome, welcome), Channel, DeliveryMethod.ReliableOrdered);
        };

        _listener.PeerDisconnectedEvent += (peer, _) =>
        {
            _peers.Remove(peer.Id);
            _inputBuffers.Remove(peer.Id);
            _world.RemovePlayer(peer.Id);
            Console.WriteLine($"[-] Player {peer.Id} left    ({_net.ConnectedPeersCount} online)");
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
            Console.Error.WriteLine($"[!] Bad packet from player {peer.Id} — disconnecting ({e.GetType().Name}: {e.Message})");
            peer.Disconnect();
        }
    }

    private void HandleMessage(NetPeer peer, NetPacketReader reader)
    {
        var type = (MessageType)reader.GetByte();
        switch (type)
        {
            case MessageType.JoinRequest:
            {
                var join = new JoinRequest();
                join.Deserialize(reader);
                if (_world.Players.TryGetValue(peer.Id, out var player))
                    player.Name = SanitizeName(join.Name, peer.Id);
                break;
            }

            case MessageType.Input:
            {
                var input = new InputPacket();
                input.Deserialize(reader);
                // Buffer the intent; it is applied one-per-tick, in order, by the sim
                // loop (see ApplyBufferedInputs). Trust nothing but the intent — the
                // simulation clamps/normalizes it.
                if (_inputBuffers.TryGetValue(peer.Id, out var buffer))
                    buffer.Enqueue(new PlayerInput
                    {
                        MoveX = input.MoveX,
                        MoveZ = input.MoveZ,
                        Sequence = input.Sequence,
                        Attack = input.Attack,
                    });
                break;
            }

            case MessageType.EquipRequest:
            {
                var req = new EquipRequestPacket();
                req.Deserialize(reader);
                if (_world.Players.TryGetValue(peer.Id, out var player) && player.TryEquip(req.ItemId))
                    SendEquipment(peer, player);
                break;
            }
        }
    }

    /// <summary>Client-supplied names are untrusted: strip control chars, cap the length.</summary>
    private static string SanitizeName(string name, int playerId)
    {
        const int maxLength = 24;
        var cleaned = string.Concat(name.Where(c => !char.IsControl(c))).Trim();
        if (cleaned.Length > maxLength)
            cleaned = cleaned[..maxLength];
        return cleaned.Length == 0 ? $"Raider-{playerId}" : cleaned;
    }

    private void Loop()
    {
        var tickInterval = TimeSpan.FromSeconds(SimConstants.TickDelta);
        var snapshotInterval = TimeSpan.FromSeconds(1.0 / NetConfig.SnapshotsPerSecond);

        var clock = Stopwatch.StartNew();
        var nextTick = clock.Elapsed;
        var nextSnapshot = clock.Elapsed;
        var nextEnemyCheck = clock.Elapsed + EnemyRespawnInterval;

        while (_running)
        {
            _net.PollEvents();

            var now = clock.Elapsed;

            // Fixed-step simulation, catching up if the loop fell behind — but bounded.
            // After a long stall (GC pause, debugger break, machine sleep) replaying
            // every missed tick would only stall the loop further, so drop the lost
            // time and resume at the normal cadence instead.
            const int maxCatchUpTicks = 10; // ~1/3 s at 30 Hz
            var ticksRun = 0;
            while (now >= nextTick && ticksRun++ < maxCatchUpTicks)
            {
                ApplyBufferedInputs();
                _world.Step();
                nextTick += tickInterval;
            }
            if (now >= nextTick)
            {
                Console.WriteLine($"[!] Sim stalled {(now - nextTick).TotalMilliseconds:0} ms beyond the catch-up cap — dropping the lost time.");
                nextTick = now;
            }

            DeliverPickups();

            // Top the enemy population back up over time (the boss is separate).
            // Scheduled from "now" rather than accumulated, so a stall can never
            // bank a burst of overdue spawns.
            if (now >= nextEnemyCheck)
            {
                var regulars = _world.Enemies.Values.Count(e => e.Type != EnemyType.Boss);
                if (regulars < _targetEnemyCount)
                {
                    var spawn = RandomEnemySpawn();
                    _world.SpawnEnemy(spawn.Position, spawn.Type);
                }
                UpdateBoss(now);
                nextEnemyCheck = now + EnemyRespawnInterval;
            }

            if (now >= nextSnapshot)
            {
                BroadcastSnapshot();
                nextSnapshot = now + snapshotInterval; // same: no post-stall snapshot burst
            }

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
            if (_inputBuffers.TryGetValue(player.Id, out var buffer) && buffer.TryDequeue(out var input))
                _world.SetInput(player.Id, input);
            else
                _world.SetInput(player.Id, new PlayerInput { Sequence = player.LastProcessedInput });
        }
    }

    private void BroadcastSnapshot()
    {
        if (_net.ConnectedPeersCount == 0)
            return;

        var snapshot = new WorldSnapshotPacket
        {
            ServerTick = _world.Tick,
            Players = _world.Players.Values.Select(p => new PlayerSnapshot
            {
                Id = p.Id,
                X = p.Position.X,
                Y = p.Position.Y,
                Z = p.Position.Z,
                Health = p.Health,
                LastProcessedInput = p.LastProcessedInput,
                Attacking = p.IsAttacking,
            }).ToArray(),
            Enemies = _world.Enemies.Values.Select(e => new EnemySnapshot
            {
                Id = e.Id,
                X = e.Position.X,
                Y = e.Position.Y,
                Z = e.Position.Z,
                Health = e.Health,
                Attacking = e.IsAttacking,
                Type = (byte)e.Type,
            }).ToArray(),
            GroundItems = _world.GroundItems.Values.Select(g => new GroundItemSnapshot
            {
                Id = g.Id,
                X = g.Position.X,
                Y = g.Position.Y,
                Z = g.Position.Z,
                Rarity = (byte)g.Item.Rarity,
            }).ToArray(),
            Projectiles = _world.Projectiles.Values.Select(p => new ProjectileSnapshot
            {
                Id = p.Id,
                X = p.Position.X,
                Y = p.Position.Y,
                Z = p.Position.Z,
            }).ToArray(),
        };

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
        Console.WriteLine($"[equip] Player {player.Id} equipment: " +
                          $"W{packet.WeaponItemId} A{packet.ArmorItemId} T{packet.TrinketItemId} " +
                          $"(atk {player.AttackDamage:0}, armor {player.DamageReduction:0.0})");
    }

    private static int EquippedId(PlayerState player, EquipSlot slot) =>
        player.Equipped.TryGetValue(slot, out var item) ? item.Id : 0;

    private void DeliverPickups()
    {
        foreach (var pickup in _world.ConsumePickups())
        {
            if (!_peers.TryGetValue(pickup.PlayerId, out var peer))
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
            peer.Send(NetProtocol.Frame(MessageType.ItemPickedUp, packet), Channel, DeliveryMethod.ReliableOrdered);
            Console.WriteLine($"[loot] Player {pickup.PlayerId} picked up {item.Name} (power {item.Power})");
        }
    }

    private void SpawnInitialEnemies()
    {
        // One enemy per typed marker (up to the cap) so the authored mix is exact,
        // then top up randomly if the cap exceeds the marker count.
        var markers = _dungeon.TypedEnemySpawns;
        for (var i = 0; i < _targetEnemyCount; i++)
        {
            var spawn = i < markers.Count ? markers[i] : RandomEnemySpawn();
            _world.SpawnEnemy(spawn.Position, spawn.Type);
        }
        Console.WriteLine($"Spawned {_targetEnemyCount} enemies (map has {markers.Count} spawn markers).");

        if (_dungeon.BossSpawn is { } bossPos)
        {
            _world.SpawnEnemy(bossPos, EnemyType.Boss);
            Console.WriteLine("The Barrow King waits in his chamber.");
        }
    }

    /// <summary>Respawn the boss a while after it is slain (so the fight repeats).</summary>
    private void UpdateBoss(TimeSpan now)
    {
        if (_dungeon.BossSpawn is not { } bossPos)
            return;

        if (_world.Enemies.Values.Any(e => e.Type == EnemyType.Boss))
            return;

        if (_bossRespawnAt is null)
        {
            _bossRespawnAt = now + BossRespawnDelay;
            Console.WriteLine($"[boss] The Barrow King has fallen! He returns in {BossRespawnDelay.TotalSeconds:0}s.");
        }
        else if (now >= _bossRespawnAt)
        {
            _bossRespawnAt = null;
            _world.SpawnEnemy(bossPos, EnemyType.Boss);
            Console.WriteLine("[boss] The Barrow King rises again.");
        }
    }

    private EnemySpawnPoint RandomEnemySpawn()
    {
        // A random typed spawn marker, but not right on top of the player spawn.
        const float minDistanceSq = 200f * 200f;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var spawn = _dungeon.RandomEnemySpawn(_rng);
            if (Vector3.DistanceSquared(spawn.Position, _dungeon.SpawnPoint) > minDistanceSq)
                return spawn;
        }
        return _dungeon.RandomEnemySpawn(_rng);
    }

    private DungeonGeometryPacket BuildGeometryPacket()
    {
        var boxes = new float[_dungeon.Solids.Count * 6];
        for (var i = 0; i < _dungeon.Solids.Count; i++)
        {
            var s = _dungeon.Solids[i];
            boxes[i * 6 + 0] = s.Min.X;
            boxes[i * 6 + 1] = s.Min.Y;
            boxes[i * 6 + 2] = s.Min.Z;
            boxes[i * 6 + 3] = s.Max.X;
            boxes[i * 6 + 4] = s.Max.Y;
            boxes[i * 6 + 5] = s.Max.Z;
        }

        return new DungeonGeometryPacket
        {
            SpawnX = _dungeon.SpawnPoint.X,
            SpawnY = _dungeon.SpawnPoint.Y,
            SpawnZ = _dungeon.SpawnPoint.Z,
            ScenePath = _dungeon.ScenePath ?? "",
            Boxes = boxes,
        };
    }
}

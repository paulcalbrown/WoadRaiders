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
    private readonly string _mapPath;
    private DungeonGeometry _dungeon = null!;
    private volatile bool _running;

    // Enemy population policy — map-driven: authors control density by placing
    // EnemySpawn markers (target = 2 enemies per marker, clamped to sane bounds).
    private int _targetEnemyCount;
    private static readonly TimeSpan EnemyRespawnInterval = TimeSpan.FromSeconds(2);

    public GameServer(string mapPath)
    {
        _mapPath = mapPath;
        _net = new NetManager(_listener);
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

        try
        {
            _dungeon = DungeonGeometryFile.Load(_mapPath);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Failed to load map '{_mapPath}': {e.Message}");
            _net.Stop();
            return false;
        }
        Console.WriteLine($"Loaded map '{_mapPath}' " +
                          $"({_dungeon.Solids.Count} solids, {_dungeon.EnemySpawns.Count} enemy spawns" +
                          $"{(_dungeon.ScenePath is null ? "" : $", scene {_dungeon.ScenePath}")}).");
        _world.Geometry = _dungeon;
        _targetEnemyCount = Math.Clamp(_dungeon.EnemySpawns.Count * 2, 4, 24);

        SpawnInitialEnemies();

        Loop();

        _net.Stop();
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
            _world.RemovePlayer(peer.Id);
            Console.WriteLine($"[-] Player {peer.Id} left    ({_net.ConnectedPeersCount} online)");
        };

        _listener.NetworkReceiveEvent += OnReceive;
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        var type = (MessageType)reader.GetByte();
        switch (type)
        {
            case MessageType.JoinRequest:
            {
                var join = new JoinRequest();
                join.Deserialize(reader);
                if (_world.Players.TryGetValue(peer.Id, out var player))
                    player.Name = join.Name;
                break;
            }

            case MessageType.Input:
            {
                var input = new InputPacket();
                input.Deserialize(reader);
                // Trust nothing but the intent; the simulation clamps/normalizes it.
                _world.SetInput(peer.Id, new PlayerInput
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

            // Fixed-step simulation, catching up if the loop fell behind.
            while (now >= nextTick)
            {
                _world.Step();
                nextTick += tickInterval;
            }

            DeliverPickups();

            // Top the enemy population back up over time.
            if (now >= nextEnemyCheck)
            {
                if (_world.Enemies.Count < _targetEnemyCount)
                    _world.SpawnEnemy(RandomEnemySpawn());
                nextEnemyCheck += EnemyRespawnInterval;
            }

            if (now >= nextSnapshot)
            {
                BroadcastSnapshot();
                nextSnapshot += snapshotInterval;
            }

            Thread.Sleep(1);
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
            }).ToArray(),
            GroundItems = _world.GroundItems.Values.Select(g => new GroundItemSnapshot
            {
                Id = g.Id,
                X = g.Position.X,
                Y = g.Position.Y,
                Z = g.Position.Z,
                Rarity = (byte)g.Item.Rarity,
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
        for (var i = 0; i < _targetEnemyCount; i++)
            _world.SpawnEnemy(RandomEnemySpawn());
        Console.WriteLine($"Spawned {_targetEnemyCount} enemies (map has {_dungeon.EnemySpawns.Count} spawn markers).");
    }

    private Vector3 RandomEnemySpawn()
    {
        // A random spawn marker, but not right on top of the player spawn.
        const float minDistanceSq = 200f * 200f;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var pos = _dungeon.RandomSpawnPosition(_rng);
            if (Vector3.DistanceSquared(pos, _dungeon.SpawnPoint) > minDistanceSq)
                return pos;
        }
        return _dungeon.RandomSpawnPosition(_rng);
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

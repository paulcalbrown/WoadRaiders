using System.Collections.Generic;
using Godot;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;
using SysVec2 = System.Numerics.Vector2; // Core positions; mapped to the 3D ground plane (X, 0, Y).

/// <summary>
/// 3D isometric client with prediction, combat, loot, and equipment.
///
/// The simulation is 2D-on-a-plane; this renderer maps each sim Vector2(X, Y) to
/// the ground plane Vector3(X, 0, Y) and views it through an orthographic camera
/// at a fixed isometric angle that eases after the local player. Characters are
/// capsules, the dungeon is MultiMesh floor/walls with shadows, loot gems spin,
/// and enemies carry billboard health bars.
///
/// Arrows move · Space attacks · walk over loot · I = inventory · 1-9 = equip.
/// </summary>
public partial class NetworkClient : Node3D
{
    private const byte Channel = 0;
    private const float RemoteSmoothing = 18f;
    private const float CameraSmoothing = 8f;

    // 3D layout (world units == sim units).
    private const float EntityY = 22f;     // half the capsule height, so feet rest on the floor
    private const float LootY = 14f;
    private const float WallHeight = 70f;
    private static readonly Vector3 CameraOffset = new(600f, 700f, 600f); // 45° yaw, ~40° pitch
    private const float CameraOrthoSize = 720f;

    private static readonly Color LocalColor = Colors.SkyBlue;
    private static readonly Color RemoteColor = Colors.SeaGreen;
    private static readonly Color EnemyColor = Colors.OrangeRed;

    private EventBasedNetListener _listener = null!;
    private NetManager _net = null!;
    private NetPeer? _server;

    private int _localPlayerId = -1;
    private uint _inputSequence;
    private ClientPrediction? _prediction;
    private double _tickAccumulator;
    private double _elapsed;
    private float _localHealth = SimConstants.PlayerMaxHealth;
    private DungeonMap? _map;
    private Vector3 _cameraTarget;
    private bool _cameraInitialised;
    private SysVec2 _prevTickPos;   // predicted position at the previous fixed tick
    private SysVec2 _localRenderPos; // interpolated position actually drawn this frame

    private Camera3D _camera = null!;
    private CapsuleMesh _entityMesh = null!;
    private BoxMesh _lootMesh = null!;
    private QuadMesh _barMesh = null!;

    private MultiMesh? _wallMulti;
    private Vector3[] _wallPositions = System.Array.Empty<Vector3>();

    private readonly Dictionary<int, MeshInstance3D> _playerViews = new();
    private readonly Dictionary<int, Vector3> _remoteTargets = new();
    private readonly Dictionary<int, MeshInstance3D> _enemyViews = new();
    private readonly Dictionary<int, Vector3> _enemyTargets = new();
    private readonly Dictionary<int, MeshInstance3D> _lootViews = new();

    private readonly List<Item> _inventory = new();
    private int _equippedWeaponId;
    private int _equippedArmorId;
    private int _equippedTrinketId;
    private bool _inventoryOpen;

    private Label _hud = null!;
    private Label _invPanel = null!;

    public override void _Ready()
    {
        SetupScene();
        SetupHud();

        _listener = new EventBasedNetListener();
        _net = new NetManager(_listener);
        _net.Start();

        _listener.NetworkReceiveEvent += OnReceive;
        _listener.PeerConnectedEvent += peer =>
        {
            _server = peer;
            peer.Send(NetProtocol.Frame(MessageType.JoinRequest, new JoinRequest { Name = "Woad Raider" }),
                      Channel, DeliveryMethod.ReliableOrdered);
        };

        _server = _net.Connect("127.0.0.1", NetConfig.DefaultPort, NetConfig.ConnectionKey);
        GD.Print($"Connecting to dedicated server on 127.0.0.1:{NetConfig.DefaultPort} ...");
    }

    private void SetupScene()
    {
        _entityMesh = new CapsuleMesh { Radius = 12f, Height = 44f };
        _lootMesh = new BoxMesh { Size = new Vector3(16, 16, 16) };
        _barMesh = new QuadMesh { Size = new Vector2(40, 6) };

        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = CameraOrthoSize,
            Current = true,
            Position = CameraOffset,
        };
        AddChild(_camera);

        // Key light casts shadows (main depth cue); a dim fill keeps back faces readable.
        var key = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -50, 0),
            LightEnergy = 1.1f,
            ShadowEnabled = true,
        };
        AddChild(key);
        var fill = new DirectionalLight3D { RotationDegrees = new Vector3(-25, 130, 0), LightEnergy = 0.4f };
        AddChild(fill);

        AddChild(new WorldEnvironment { Environment = DungeonEnvironment() });
    }

    private static Godot.Environment DungeonEnvironment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Color,
        BackgroundColor = new Color(0.02f, 0.02f, 0.03f),
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color(0.40f, 0.40f, 0.50f),
        AmbientLightEnergy = 0.4f,
        FogEnabled = true,
        FogLightColor = new Color(0.03f, 0.03f, 0.05f),
        FogDensity = 0.0015f,
    };

    private void SetupHud()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hud = new Label { Position = new Vector2(16, 12) };
        canvas.AddChild(_hud);
        _invPanel = new Label { Position = new Vector2(16, 44), Visible = false };
        canvas.AddChild(_invPanel);
    }

    public override void _Process(double delta)
    {
        _elapsed += delta;
        _net.PollEvents();

        if (_prediction is not null)
        {
            _tickAccumulator += delta;
            var catchUp = 5;
            while (_tickAccumulator >= SimConstants.TickDelta && catchUp-- > 0)
            {
                _prevTickPos = _prediction.Position; // remember where we were before stepping
                ClientTick();
                _tickAccumulator -= SimConstants.TickDelta;
            }
        }

        UpdateViews(delta);
        UpdateWallFade();
        UpdateCamera(delta);
        UpdateHud();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        if (key.Keycode == Key.I)
        {
            _inventoryOpen = !_inventoryOpen;
            _invPanel.Visible = _inventoryOpen;
        }
        else if (_inventoryOpen && key.Keycode >= Key.Key1 && key.Keycode <= Key.Key9)
        {
            var index = (int)(key.Keycode - Key.Key1);
            if (index < _inventory.Count && _server is not null)
                _server.Send(
                    NetProtocol.Frame(MessageType.EquipRequest, new EquipRequestPacket { ItemId = _inventory[index].Id }),
                    Channel, DeliveryMethod.ReliableOrdered);
        }
    }

    public override void _ExitTree() => _net.Stop();

    private void ClientTick()
    {
        if (_server is null || _prediction is null)
            return;

        var move = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        var attack = Input.IsActionPressed("ui_accept"); // Space / Enter
        var input = new PlayerInput { MoveX = move.X, MoveY = move.Y, Attack = attack, Sequence = ++_inputSequence };

        // Movement is predicted; damage, loot, and equipment stay server-authoritative.
        _prediction.Predict(input);

        _server.Send(
            NetProtocol.Frame(MessageType.Input, new InputPacket
            {
                MoveX = move.X, MoveY = move.Y, Attack = attack, Sequence = input.Sequence,
            }),
            Channel, DeliveryMethod.Sequenced);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        var type = (MessageType)reader.GetByte();
        switch (type)
        {
            case MessageType.DungeonMap:
                var mapPacket = new DungeonMapPacket();
                mapPacket.Deserialize(reader);
                var floor = new bool[mapPacket.Floor.Length];
                for (var i = 0; i < floor.Length; i++)
                    floor[i] = mapPacket.Floor[i] != 0;
                _map = new DungeonMap(mapPacket.Width, mapPacket.Height, mapPacket.TileSize, floor,
                                      new SysVec2(mapPacket.SpawnX, mapPacket.SpawnY));
                BuildDungeonMesh();
                break;

            case MessageType.Welcome:
                var welcome = new WelcomePacket();
                welcome.Deserialize(reader);
                _localPlayerId = welcome.PlayerId;
                _prediction = new ClientPrediction(_localPlayerId, _map?.SpawnPoint ?? SysVec2.Zero, _map);
                _prevTickPos = _localRenderPos = _prediction.Position;
                GD.Print($"Joined as player {_localPlayerId}");
                break;

            case MessageType.WorldSnapshot:
                var snapshot = new WorldSnapshotPacket();
                snapshot.Deserialize(reader);
                ApplySnapshot(snapshot);
                break;

            case MessageType.ItemPickedUp:
                var loot = new ItemPickedUpPacket();
                loot.Deserialize(reader);
                _inventory.Add(new Item(loot.ItemId, loot.Name, (ItemRarity)loot.Rarity, (ItemType)loot.Type, loot.Power));
                GD.Print($"Looted {loot.Name} (Power {loot.Power})");
                break;

            case MessageType.EquipmentUpdate:
                var eq = new EquipmentUpdatePacket();
                eq.Deserialize(reader);
                _equippedWeaponId = eq.WeaponItemId;
                _equippedArmorId = eq.ArmorItemId;
                _equippedTrinketId = eq.TrinketItemId;
                break;
        }
    }

    private void ApplySnapshot(WorldSnapshotPacket snapshot)
    {
        var seenPlayers = new HashSet<int>();
        foreach (var p in snapshot.Players)
        {
            seenPlayers.Add(p.Id);
            var pos = new Vector3(p.X, EntityY, p.Y);
            GetOrCreatePlayerView(p.Id, pos);

            if (p.Id == _localPlayerId)
            {
                _localHealth = p.Health; // authoritative, never predicted
                _prediction?.Reconcile(new SysVec2(p.X, p.Y), p.LastProcessedInput);
            }
            else
            {
                _remoteTargets[p.Id] = pos;
            }
        }
        Prune(_playerViews, seenPlayers, _remoteTargets);

        var seenEnemies = new HashSet<int>();
        foreach (var e in snapshot.Enemies)
        {
            seenEnemies.Add(e.Id);
            var pos = new Vector3(e.X, EntityY, e.Y);
            var view = GetOrCreateEnemyView(e.Id, pos);
            _enemyTargets[e.Id] = pos;
            UpdateHealthBar(view, Mathf.Clamp(e.Health / SimConstants.EnemyMaxHealth, 0f, 1f));
        }
        Prune(_enemyViews, seenEnemies, _enemyTargets);

        var seenLoot = new HashSet<int>();
        foreach (var g in snapshot.GroundItems)
        {
            seenLoot.Add(g.Id);
            var view = GetOrCreateLootView(g.Id, g.Rarity);
            view.Position = new Vector3(g.X, LootY, g.Y);
        }
        Prune(_lootViews, seenLoot);
    }

    private void UpdateViews(double delta)
    {
        var factor = Mathf.Clamp((float)delta * RemoteSmoothing, 0f, 1f);

        foreach (var (id, view) in _playerViews)
        {
            if (id == _localPlayerId && _prediction is not null)
            {
                // Interpolate between the last two fixed ticks so 30 Hz motion renders smoothly.
                var alpha = Mathf.Clamp((float)(_tickAccumulator / SimConstants.TickDelta), 0f, 1f);
                _localRenderPos = SysVec2.Lerp(_prevTickPos, _prediction.Position, alpha);
                view.Position = new Vector3(_localRenderPos.X, EntityY, _localRenderPos.Y);
            }
            else if (_remoteTargets.TryGetValue(id, out var target))
            {
                view.Position = view.Position.Lerp(target, factor);
            }
        }

        foreach (var (id, view) in _enemyViews)
            if (_enemyTargets.TryGetValue(id, out var target))
                view.Position = view.Position.Lerp(target, factor);

        // Loot gems spin and bob so drops catch the eye.
        var bob = Mathf.Sin((float)_elapsed * 3f) * 4f;
        foreach (var view in _lootViews.Values)
        {
            view.Position = new Vector3(view.Position.X, LootY + bob, view.Position.Z);
            view.RotateY((float)delta * 2f);
        }
    }

    private void UpdateCamera(double delta)
    {
        var target = _prediction is not null
            ? new Vector3(_localRenderPos.X, 0f, _localRenderPos.Y) // follow the smoothed render position
            : Vector3.Zero;

        // Smooth the LOOK-TARGET (not the camera position) and keep the camera at a fixed
        // offset from it. That keeps the camera-to-target vector constant, so the camera
        // translates to follow the player but never rotates/tilts.
        if (!_cameraInitialised)
        {
            _cameraTarget = target; // snap on the first frame so we don't pan in from the origin
            _cameraInitialised = true;
        }
        else
        {
            _cameraTarget = _cameraTarget.Lerp(target, Mathf.Clamp((float)delta * CameraSmoothing, 0f, 1f));
        }

        _camera.Position = _cameraTarget + CameraOffset;
        _camera.LookAt(_cameraTarget, Vector3.Up);
    }

    private void UpdateHud()
    {
        var attack = SimConstants.PlayerAttackDamage + PowerOf(_equippedWeaponId) + PowerOf(_equippedTrinketId);
        var reduction = PowerOf(_equippedArmorId) * SimConstants.ArmorDamageReductionPerPower;

        _hud.Text = $"HP {Mathf.RoundToInt(_localHealth)}/{Mathf.RoundToInt(SimConstants.PlayerMaxHealth)}   " +
                    $"Items {_inventory.Count}   Atk {attack:0}   Armor {reduction:0.0}   " +
                    "[I] inventory   [Space] attack";

        if (_inventoryOpen)
            _invPanel.Text = BuildInventoryText(attack, reduction);
    }

    private string BuildInventoryText(float attack, float reduction)
    {
        var lines = new List<string> { "INVENTORY   (I to close · 1-9 to equip)" };
        for (var i = 0; i < _inventory.Count; i++)
        {
            var it = _inventory[i];
            var equipped = it.Id == _equippedWeaponId || it.Id == _equippedArmorId || it.Id == _equippedTrinketId;
            var num = i < 9 ? $"{i + 1})" : "  ";
            var mark = equipped ? "[E]" : "   ";
            lines.Add($"{num} {mark} {it.Name} — {it.Type} · Power {it.Power}");
        }
        lines.Add($"— total: Attack {attack:0} · Armor {reduction:0.0} —");
        return string.Join('\n', lines);
    }

    private int PowerOf(int itemId)
    {
        foreach (var it in _inventory)
            if (it.Id == itemId)
                return it.Power;
        return 0;
    }

    private void BuildDungeonMesh()
    {
        if (_map is null)
            return;

        var floorPositions = new List<Vector3>();
        var wallPositions = new List<Vector3>();
        for (var ty = 0; ty < _map.Height; ty++)
        for (var tx = 0; tx < _map.Width; tx++)
        {
            var c = _map.TileCenter(tx, ty);
            if (_map.IsFloorTile(tx, ty))
                floorPositions.Add(new Vector3(c.X, -2f, c.Y));       // thin slab, top at y=0
            else
                wallPositions.Add(new Vector3(c.X, WallHeight / 2f, c.Y));
        }

        var floorMesh = new BoxMesh { Size = new Vector3(_map.TileSize, 4f, _map.TileSize) };
        AddChild(MakeTileField(floorMesh, floorPositions, FloorMaterial()));
        BuildWalls(wallPositions);
    }

    private void BuildWalls(List<Vector3> positions)
    {
        _wallPositions = positions.ToArray();

        var mesh = new BoxMesh { Size = new Vector3(_map!.TileSize, WallHeight, _map.TileSize) };
        _wallMulti = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, // must be set before InstanceCount
            Mesh = mesh,
            InstanceCount = positions.Count,
        };
        for (var i = 0; i < positions.Count; i++)
        {
            _wallMulti.SetInstanceTransform(i, new Transform3D(Basis.Identity, positions[i]));
            _wallMulti.SetInstanceColor(i, Colors.White); // white = full stone texture; alpha = fade
        }

        // WallMaterial is textured stone; VertexColorUseAsAlbedo lets each instance's alpha fade it
        // when it would occlude the player, and AlphaHash writes depth so solid walls never mis-sort.
        AddChild(new MultiMeshInstance3D { Multimesh = _wallMulti, MaterialOverride = WallMaterial() });
    }

    // Fade the walls between the camera and the local player so it is never hidden.
    private void UpdateWallFade()
    {
        if (_wallMulti is null || _prediction is null)
            return;

        var player = new Vector3(_localRenderPos.X, EntityY, _localRenderPos.Y);
        var camDir = CameraOffset.Normalized(); // player → camera (fixed iso direction)
        const float fadeRadius = 55f;
        const float fadedAlpha = 0.18f;

        for (var i = 0; i < _wallPositions.Length; i++)
        {
            var v = _wallPositions[i] - player;
            var along = v.Dot(camDir);

            var alpha = 1f;
            if (along > 0f) // wall is on the camera side of the player
            {
                var perp = (v - camDir * along).Length(); // screen-space closeness (ortho)
                if (perp < fadeRadius)
                    alpha = Mathf.Lerp(fadedAlpha, 1f, perp / fadeRadius);
            }

            _wallMulti.SetInstanceColor(i, new Color(1f, 1f, 1f, alpha));
        }
    }

    private static MultiMeshInstance3D MakeTileField(Mesh mesh, List<Vector3> positions, Material material)
    {
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = positions.Count,
        };
        for (var i = 0; i < positions.Count; i++)
            multi.SetInstanceTransform(i, new Transform3D(Basis.Identity, positions[i]));

        return new MultiMeshInstance3D { Multimesh = multi, MaterialOverride = material };
    }

    private StandardMaterial3D FloorMaterial() => StoneMaterial(
        albedoSeed: 1, normalSeed: 2,
        dark: new Color(0.14f, 0.14f, 0.17f), light: new Color(0.34f, 0.33f, 0.37f), fadeable: false);

    private StandardMaterial3D WallMaterial() => StoneMaterial(
        albedoSeed: 3, normalSeed: 4,
        dark: new Color(0.06f, 0.06f, 0.09f), light: new Color(0.20f, 0.19f, 0.25f), fadeable: true);

    // Procedural stone: color-ramped noise albedo + a noise normal map, projected with world-space
    // triplanar so it reads as one continuous rock surface across all tiles (no per-tile seams).
    private static StandardMaterial3D StoneMaterial(int albedoSeed, int normalSeed, Color dark, Color light, bool fadeable)
    {
        var ramp = new Gradient();
        ramp.SetColor(0, dark);
        ramp.SetColor(1, light);

        var albedoNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.05f, Seed = albedoSeed };
        var albedo = new NoiseTexture2D { Noise = albedoNoise, Width = 256, Height = 256, Seamless = true, ColorRamp = ramp };

        var normalNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.08f, Seed = normalSeed };
        var normal = new NoiseTexture2D
        {
            Noise = normalNoise, Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 3f,
        };

        var material = new StandardMaterial3D
        {
            AlbedoColor = Colors.White,
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            Roughness = 0.95f,
            Metallic = 0f,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.012f, 0.012f, 0.012f),
        };

        if (fadeable)
        {
            material.VertexColorUseAsAlbedo = true;
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaHash;
        }

        return material;
    }

    private MeshInstance3D GetOrCreatePlayerView(int id, Vector3 spawn)
    {
        if (_playerViews.TryGetValue(id, out var existing))
            return existing;

        var view = new MeshInstance3D
        {
            Mesh = _entityMesh,
            Position = spawn, // snap to the real spot so it doesn't lerp in from the origin
            MaterialOverride = new StandardMaterial3D { AlbedoColor = id == _localPlayerId ? LocalColor : RemoteColor },
        };
        AddChild(view);
        _playerViews[id] = view;
        return view;
    }

    private MeshInstance3D GetOrCreateEnemyView(int id, Vector3 spawn)
    {
        if (_enemyViews.TryGetValue(id, out var existing))
            return existing;

        var view = new MeshInstance3D
        {
            Mesh = _entityMesh,
            Position = spawn, // snap to the real spot so it doesn't lerp in from the origin
            MaterialOverride = new StandardMaterial3D { AlbedoColor = EnemyColor },
        };
        AddChild(view);
        AttachHealthBar(view);
        _enemyViews[id] = view;
        return view;
    }

    private void AttachHealthBar(MeshInstance3D enemy)
    {
        var bg = new MeshInstance3D
        {
            Mesh = _barMesh,
            Position = new Vector3(0, 34, 0),
            MaterialOverride = BarMaterial(new Color(0.05f, 0.05f, 0.05f)),
        };
        enemy.AddChild(bg);

        var fill = new MeshInstance3D
        {
            Name = "HealthFill",
            Mesh = _barMesh,
            Position = new Vector3(0, 34, 0.2f),
            MaterialOverride = BarMaterial(Colors.LimeGreen),
        };
        enemy.AddChild(fill);
    }

    private static void UpdateHealthBar(MeshInstance3D enemy, float frac)
    {
        var fill = enemy.GetNode<MeshInstance3D>("HealthFill");
        fill.Scale = new Vector3(frac, 1f, 1f);
        ((StandardMaterial3D)fill.MaterialOverride).AlbedoColor = new Color(0.9f, 0.2f, 0.2f).Lerp(Colors.LimeGreen, frac);
    }

    private static StandardMaterial3D BarMaterial(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        BillboardKeepScale = true,
    };

    private MeshInstance3D GetOrCreateLootView(int id, byte rarity)
    {
        if (_lootViews.TryGetValue(id, out var existing))
            return existing;

        var view = new MeshInstance3D
        {
            Mesh = _lootMesh,
            RotationDegrees = new Vector3(0, 0, 45), // sit like a gem
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = RarityColor(rarity),
                EmissionEnabled = true,
                Emission = RarityColor(rarity),
                EmissionEnergyMultiplier = 0.4f,
            },
        };
        AddChild(view);
        _lootViews[id] = view;
        return view;
    }

    private static void Prune(Dictionary<int, MeshInstance3D> views, HashSet<int> seen,
                              Dictionary<int, Vector3>? targets = null)
    {
        foreach (var id in new List<int>(views.Keys))
        {
            if (seen.Contains(id))
                continue;
            views[id].QueueFree();
            views.Remove(id);
            targets?.Remove(id);
        }
    }

    private static Color RarityColor(byte rarity) => rarity switch
    {
        0 => new Color(0.80f, 0.80f, 0.80f), // Common
        1 => new Color(0.30f, 0.55f, 1.00f), // Magic
        2 => new Color(1.00f, 0.85f, 0.20f), // Rare
        3 => new Color(0.70f, 0.30f, 1.00f), // Epic
        4 => new Color(1.00f, 0.50f, 0.10f), // Legendary
        _ => Colors.White,
    };
}

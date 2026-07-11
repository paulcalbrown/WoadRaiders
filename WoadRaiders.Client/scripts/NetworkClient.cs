using System.Collections.Generic;
using Godot;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;
using SysVec3 = System.Numerics.Vector3; // Core simulates in 3D (Y-up), same convention as Godot.
using CoreAabb = WoadRaiders.Core.Aabb;  // disambiguate from Godot.Aabb

/// <summary>
/// 3D isometric client with prediction, combat, loot, and equipment.
///
/// The simulation is fully 3D (System.Numerics, Y-up) — the same convention as
/// Godot, so sim positions map to the scene 1:1. An orthographic camera at a
/// fixed isometric angle eases after the local player. Characters are capsules,
/// the dungeon is MultiMesh floor/walls with shadows, loot gems spin, and
/// enemies carry billboard health bars.
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
    private DungeonGeometry? _geometry;
    private Vector3 _cameraTarget;
    private bool _cameraInitialised;
    private SysVec3 _prevTickPos;    // predicted position at the previous fixed tick
    private SysVec3 _localRenderPos; // interpolated position actually drawn this frame

    private Camera3D _camera = null!;
    private CapsuleMesh _entityMesh = null!;
    private BoxMesh _lootMesh = null!;
    private QuadMesh _barMesh = null!;

    private MultiMesh? _wallMulti;
    private Vector3[] _wallMins = System.Array.Empty<Vector3>();
    private Vector3[] _wallMaxs = System.Array.Empty<Vector3>();

    // Authored-scene visuals: tall meshes that participate in the occlusion fade.
    private const float FadeMinHeight = 35f; // meshes whose top is below this never fade (floors)
    private readonly List<(GeometryInstance3D Node, Vector3 Min, Vector3 Max)> _fadeMeshes = new();

    private readonly Dictionary<int, MeshInstance3D> _playerViews = new();
    private readonly Dictionary<int, Vector3> _remoteTargets = new();
    private readonly Dictionary<int, MeshInstance3D> _enemyViews = new();
    private readonly Dictionary<int, Vector3> _enemyTargets = new();
    private readonly Dictionary<int, MeshInstance3D> _lootViews = new();
    private readonly Dictionary<int, Vector3> _lootBase = new(); // ground point each gem bobs above

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

        // Screen up/down keys steer along world Z (the ground plane).
        var move = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        var attack = Input.IsActionPressed("ui_accept"); // Space / Enter
        var input = new PlayerInput { MoveX = move.X, MoveZ = move.Y, Attack = attack, Sequence = ++_inputSequence };

        // Movement is predicted; damage, loot, and equipment stay server-authoritative.
        _prediction.Predict(input);

        _server.Send(
            NetProtocol.Frame(MessageType.Input, new InputPacket
            {
                MoveX = move.X, MoveZ = move.Y, Attack = attack, Sequence = input.Sequence,
            }),
            Channel, DeliveryMethod.Sequenced);
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
    {
        var type = (MessageType)reader.GetByte();
        switch (type)
        {
            case MessageType.DungeonGeometry:
                var geoPacket = new DungeonGeometryPacket();
                geoPacket.Deserialize(reader);
                var solids = new List<CoreAabb>(geoPacket.Boxes.Length / 6);
                for (var i = 0; i + 5 < geoPacket.Boxes.Length; i += 6)
                    solids.Add(new CoreAabb(
                        new SysVec3(geoPacket.Boxes[i], geoPacket.Boxes[i + 1], geoPacket.Boxes[i + 2]),
                        new SysVec3(geoPacket.Boxes[i + 3], geoPacket.Boxes[i + 4], geoPacket.Boxes[i + 5])));
                _geometry = new DungeonGeometry(
                    new SysVec3(geoPacket.SpawnX, geoPacket.SpawnY, geoPacket.SpawnZ),
                    solids, System.Array.Empty<SysVec3>())
                {
                    ScenePath = string.IsNullOrEmpty(geoPacket.ScenePath) ? null : geoPacket.ScenePath,
                };
                BuildDungeonVisuals();
                break;

            case MessageType.Welcome:
                var welcome = new WelcomePacket();
                welcome.Deserialize(reader);
                _localPlayerId = welcome.PlayerId;
                _prediction = new ClientPrediction(_localPlayerId, _geometry?.SpawnPoint ?? SysVec3.Zero, _geometry);
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
            var pos = new Vector3(p.X, p.Y + EntityY, p.Z);
            GetOrCreatePlayerView(p.Id, pos);

            if (p.Id == _localPlayerId)
            {
                _localHealth = p.Health; // authoritative, never predicted
                _prediction?.Reconcile(new SysVec3(p.X, p.Y, p.Z), p.LastProcessedInput);
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
            var pos = new Vector3(e.X, e.Y + EntityY, e.Z);
            var view = GetOrCreateEnemyView(e.Id, pos);
            _enemyTargets[e.Id] = pos;
            UpdateHealthBar(view, Mathf.Clamp(e.Health / SimConstants.EnemyMaxHealth, 0f, 1f));
        }
        Prune(_enemyViews, seenEnemies, _enemyTargets);

        var seenLoot = new HashSet<int>();
        foreach (var g in snapshot.GroundItems)
        {
            seenLoot.Add(g.Id);
            var ground = new Vector3(g.X, g.Y, g.Z);
            _lootBase[g.Id] = ground;
            GetOrCreateLootView(g.Id, g.Rarity, ground + Vector3.Up * LootY);
        }
        Prune(_lootViews, seenLoot, _lootBase);
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
                _localRenderPos = SysVec3.Lerp(_prevTickPos, _prediction.Position, alpha);
                view.Position = ToGodot(_localRenderPos) + Vector3.Up * EntityY;
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
        foreach (var (id, view) in _lootViews)
        {
            if (_lootBase.TryGetValue(id, out var ground))
                view.Position = ground + Vector3.Up * (LootY + bob);
            view.RotateY((float)delta * 2f);
        }
    }

    private void UpdateCamera(double delta)
    {
        var target = _prediction is not null
            ? ToGodot(_localRenderPos) // follow the smoothed render position (feet)
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

    // Hand-crafted maps render their own scene; procedural (or missing scene) maps
    // fall back to placeholder boxes built from the collision solids.
    private void BuildDungeonVisuals()
    {
        if (TryLoadAuthoredScene())
            return;
        BuildDungeonMesh();
    }

    private bool TryLoadAuthoredScene()
    {
        var path = _geometry?.ScenePath;
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
            return false;
        if (ResourceLoader.Load<PackedScene>(path) is not { } packed)
            return false;

        var scene = packed.Instantiate<Node>();
        AddChild(scene); // must be in-tree before reading global transforms
        CollectFadeMeshes(scene);
        GD.Print($"Rendering authored map scene '{path}' ({_fadeMeshes.Count} fade-aware meshes)");
        return true;
    }

    // Tall meshes take part in the occlusion fade; floors (low tops) never do.
    // Opt any mesh out by adding it to the "no_fade" group in the editor.
    private void CollectFadeMeshes(Node node)
    {
        if (node is MeshInstance3D mesh && !mesh.IsInGroup("no_fade"))
        {
            var aabb = mesh.GlobalTransform * mesh.GetAabb();
            if (aabb.Position.Y + aabb.Size.Y > FadeMinHeight)
                _fadeMeshes.Add((mesh, aabb.Position, aabb.Position + aabb.Size));
        }

        foreach (var child in node.GetChildren())
            CollectFadeMeshes(child);
    }

    private void BuildDungeonMesh()
    {
        if (_geometry is null)
            return;

        // One floor slab spanning the world extent (authored scenes will bring their own floors).
        var bounds = _geometry.Bounds;
        var size = ToGodot(bounds.Size);
        var center = ToGodot(bounds.Center);
        var floorMesh = new BoxMesh { Size = new Vector3(size.X, 4f, size.Z) };
        var floorPositions = new List<Vector3> { new(center.X, -2f, center.Z) };
        AddChild(MakeTileField(floorMesh, floorPositions, FloorMaterial()));

        BuildSolids();
    }

    private void BuildSolids()
    {
        var solids = _geometry!.Solids;
        _wallMins = new Vector3[solids.Count];
        _wallMaxs = new Vector3[solids.Count];

        // One unit cube, scaled per instance to each solid's size.
        var mesh = new BoxMesh { Size = Vector3.One };
        _wallMulti = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, // must be set before InstanceCount
            Mesh = mesh,
            InstanceCount = solids.Count,
        };
        for (var i = 0; i < solids.Count; i++)
        {
            _wallMins[i] = ToGodot(solids[i].Min);
            _wallMaxs[i] = ToGodot(solids[i].Max);
            var basis = Basis.Identity.Scaled(ToGodot(solids[i].Size));
            _wallMulti.SetInstanceTransform(i, new Transform3D(basis, ToGodot(solids[i].Center)));
            _wallMulti.SetInstanceColor(i, Colors.White); // white = full stone texture; alpha = fade
        }

        // WallMaterial is textured stone; VertexColorUseAsAlbedo lets each instance's alpha fade it
        // when it would occlude the player, and AlphaHash writes depth so solid walls never mis-sort.
        AddChild(new MultiMeshInstance3D { Multimesh = _wallMulti, MaterialOverride = WallMaterial() });
    }

    // Fade whatever is between the camera and the local player so it is never
    // hidden — placeholder wall instances and authored scene meshes alike.
    private void UpdateWallFade()
    {
        if (_prediction is null)
            return;

        var player = ToGodot(_localRenderPos) + Vector3.Up * EntityY;

        if (_wallMulti is not null)
        {
            for (var i = 0; i < _wallMins.Length; i++)
            {
                var alpha = OcclusionAlpha(player, _wallMins[i], _wallMaxs[i]);
                _wallMulti.SetInstanceColor(i, new Color(1f, 1f, 1f, alpha));
            }
        }

        foreach (var (node, min, max) in _fadeMeshes)
            node.Transparency = 1f - OcclusionAlpha(player, min, max);
    }

    // 1 = fully visible; falls toward fadedAlpha when the box occludes the player.
    private static float OcclusionAlpha(Vector3 player, Vector3 boxMin, Vector3 boxMax)
    {
        var camDir = CameraOffset.Normalized(); // player → camera (fixed iso direction)
        const float fadeRadius = 55f;
        const float fadedAlpha = 0.18f;

        // Closest point on the box to the player, so long walls fade correctly.
        var closest = player.Clamp(boxMin, boxMax);
        var v = closest - player;
        var along = v.Dot(camDir);
        if (along <= 0f) // box is behind the player relative to the camera
            return 1f;

        var perp = (v - camDir * along).Length(); // screen-space closeness (ortho)
        return perp < fadeRadius ? Mathf.Lerp(fadedAlpha, 1f, perp / fadeRadius) : 1f;
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

    private MeshInstance3D GetOrCreateLootView(int id, byte rarity, Vector3 spawn)
    {
        if (_lootViews.TryGetValue(id, out var existing))
            return existing;

        var view = new MeshInstance3D
        {
            Mesh = _lootMesh,
            Position = spawn,
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

    private static Vector3 ToGodot(SysVec3 v) => new(v.X, v.Y, v.Z);
}

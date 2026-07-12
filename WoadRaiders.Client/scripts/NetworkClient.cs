using System;
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
/// fixed isometric angle eases after the local player. Players and enemies are
/// animated KayKit characters that face their movement and play idle/run/attack
/// clips; the dungeon renders its authored scene; loot gems spin.
///
/// Arrows move · Space attacks · walk over loot · I = inventory · 1-9 = equip.
/// </summary>
public partial class NetworkClient : Node3D
{
    private const byte Channel = 0;
    private const float RemoteSmoothing = 18f;
    private const float CameraSmoothing = 8f;

    // 3D layout (world units == sim units).
    private const float BodyHeight = 22f;  // approx body-centre height, for camera/fade reference
    private const float LootY = 14f;
    private static readonly Vector3 CameraOffset = new(600f, 700f, 600f); // 45° yaw, ~40° pitch
    private const float CameraOrthoSize = 600f; // ortho view height in world units at the 1080 base; lower = more zoomed in

    // Top-of-screen player health bar (pixels at the 1080 render base).
    private const float HudBarWidth = 460f;
    private const float HudBarHeight = 30f;
    private const float HudBarMargin = 18f; // gap from the top edge
    private const float HudBarPad = 3f;     // dark frame thickness around the fill

    // Damage-chip trail (shared by the HUD bar and the enemy billboard bars).
    private const float EnemyBarWidth = 40f;     // matches _barMesh width
    private const float ChipDrainRate = 0.8f;    // health-fraction per second the chip drains after a hit
    private const float ChipHoldTime = 0.35f;    // seconds the chip lingers at the pre-hit level first

    // World-space "screen right" for the fixed iso camera (= Camera3D basis.X = up × viewZ).
    // The billboarded bars scale along this axis, so we also shift along it to left-anchor them.
    private static readonly Vector3 BillboardRight = Vector3.Up.Cross(CameraOffset.Normalized()).Normalized();

    // Characters (KayKit models are ~2.47 units tall → ~20x to reach ~49 world units).
    private const float CharScale = 20f;
    private const float HealthBarHeight = 54f;      // above the character's head
    private const float MoveAnimSpeed = 25f;        // units/s at/above which the run clip plays
    private const float TurnSpeed = 14f;            // facing lerp rate
    private const float ErrorDecayRate = 10f;       // how fast reconciliation corrections ease out
    private const float MaxSnapError = 120f;        // above this, snap (respawn/teleport) not smooth
    private const float VelSmoothRate = 12f;        // facing-velocity smoothing (kills twitch)
    private const float ModelYawOffset = 0f;  // KayKit chars face +Z (glTF convention); flip to Mathf.Pi if they moonwalk
    private const string AnimIdle = "Idle";
    private const string AnimRun = "Running_A";
    private const string AnimAttack = "1H_Melee_Attack_Chop";

    /// <summary>How each enemy type looks: model, size, swing, and health-bar placement.</summary>
    private readonly record struct EnemyVisual(string SceneFile, float Scale, string AttackClip, float BarHeight, float BarScale);

    private static readonly Dictionary<EnemyType, EnemyVisual> EnemyVisuals = new()
    {
        [EnemyType.Minion] = new("Skeleton_Minion.glb", 20f, "1H_Melee_Attack_Chop", 54f, 1f),
        [EnemyType.Rogue] = new("Skeleton_Rogue.glb", 20f, "1H_Melee_Attack_Stab", 54f, 1f),
        [EnemyType.Mage] = new("Skeleton_Mage.glb", 20f, "Spellcast_Shoot", 54f, 1f),
        [EnemyType.Boss] = new("Skeleton_Warrior.glb", 44f, "2H_Melee_Attack_Chop", 122f, 2f),
    };

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
    private float _localAttackAnim;      // predicted attack-anim window, so the swing is instant
    private float _localAttackCooldown;  // predicted attack cooldown, mirrors the server's cadence
    private SysVec3 _renderError;         // reconciliation correction being smoothed out of the render

    private Camera3D _camera = null!;
    private BoxMesh _lootMesh = null!;
    private SphereMesh _boltMesh = null!;
    private StandardMaterial3D _boltMaterial = null!;
    private QuadMesh _barMesh = null!;
    private PackedScene _localCharScene = null!;
    private PackedScene _remoteCharScene = null!;
    private readonly Dictionary<EnemyType, PackedScene> _enemyCharScenes = new();

    private MultiMesh? _wallMulti;
    private Vector3[] _wallMins = System.Array.Empty<Vector3>();
    private Vector3[] _wallMaxs = System.Array.Empty<Vector3>();

    // Authored-scene visuals: tall meshes that participate in the occlusion fade.
    private const float FadeMinHeight = 35f; // meshes whose top is below this never fade (floors)
    private readonly List<(GeometryInstance3D Node, Vector3 Min, Vector3 Max)> _fadeMeshes = new();

    private readonly Dictionary<int, CharacterView> _playerViews = new();
    private readonly Dictionary<int, Vector3> _remoteTargets = new();
    private readonly Dictionary<int, CharacterView> _enemyViews = new();
    private readonly Dictionary<int, Vector3> _enemyTargets = new();
    private readonly Dictionary<int, MeshInstance3D> _lootViews = new();
    private readonly Dictionary<int, MeshInstance3D> _projectileViews = new();
    private readonly Dictionary<int, Vector3> _projectileTargets = new();
    private readonly Dictionary<int, Vector3> _lootBase = new(); // ground point each gem bobs above

    private readonly List<Item> _inventory = new();
    private int _equippedWeaponId;
    private int _equippedArmorId;
    private int _equippedTrinketId;
    private bool _inventoryOpen;

    private Label _hud = null!;
    private Label _invPanel = null!;
    private ColorRect _healthBarChip = null!;
    private ColorRect _healthBarFill = null!;
    private Label _healthBarLabel = null!;
    private float _hudChipFrac = 1f;
    private float _hudChipHold;

    /// <summary>An animated character: a positioned holder, a facing pivot, and its AnimationPlayer.</summary>
    private sealed class CharacterView
    {
        public Node3D Root = null!;        // world position (never rotated → billboard bars stay upright)
        public Node3D Pivot = null!;       // yaw rotation for facing
        public AnimationPlayer? Anim;
        public MeshInstance3D? HealthFill; // enemies only
        public MeshInstance3D? HealthChip; // enemies only — the lagging "recently lost" trail
        public float HealthFrac = 1f;      // current authoritative health (target for fill + chip)
        public float ChipFrac = 1f;        // lagging fraction the chip drains toward HealthFrac
        public float ChipHold;             // seconds to linger before the chip starts draining
        public string AttackClip = AnimAttack;
        public float BarHeight = HealthBarHeight;
        public float BarScale = 1f;
        public Vector3 LastPos;
        public Vector3 SmoothVel;          // low-passed velocity used for facing (kills reconcile twitch)
        public string Clip = "";
        public float Yaw;
        public bool Attacking;
    }

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
        _lootMesh = new BoxMesh { Size = new Vector3(16, 16, 16) };
        _boltMesh = new SphereMesh { Radius = 9f, Height = 18f, RadialSegments = 8, Rings = 4 };
        _boltMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.6f, 0.35f, 1f),  // arcane violet
            EmissionEnabled = true,
            Emission = new Color(0.7f, 0.45f, 1f),
            EmissionEnergyMultiplier = 3f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        _barMesh = new QuadMesh { Size = new Vector2(40, 6) };

        const string adv = "res://addons/kaykit_character_pack_adventures/Characters/gltf";
        const string skel = "res://addons/kaykit_character_pack_skeletons/Characters/gltf";
        _localCharScene = GD.Load<PackedScene>($"{adv}/Knight.glb");
        _remoteCharScene = GD.Load<PackedScene>($"{adv}/Barbarian.glb");
        foreach (var (type, visual) in EnemyVisuals)
            _enemyCharScenes[type] = GD.Load<PackedScene>($"{skel}/{visual.SceneFile}");

        _camera = new Camera3D
        {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = CameraOrthoSize,
            Current = true,
            Position = CameraOffset,
        };
        AddChild(_camera);
        // Lighting/atmosphere now belongs to the map scene (authored in the .tscn,
        // emitted by MapGen). The client only supplies a default for maps that
        // bring none — see AddDefaultLighting, applied at map-load time.
    }

    // Fallback lighting for placeholder rendering, or an authored scene that has no
    // WorldEnvironment of its own — the dim, cool "dark torch-lit dungeon" default.
    private void AddDefaultLighting()
    {
        var key = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -50, 0),
            LightEnergy = 0.28f,
            LightColor = new Color(0.70f, 0.78f, 1.0f), // cool moonlight → contrasts warm torches
            ShadowEnabled = true,
        };
        AddChild(key);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-25, 130, 0), LightEnergy = 0.08f });
        AddChild(new WorldEnvironment { Environment = DungeonEnvironment() });
    }

    private static bool SceneHasEnvironment(Node node)
    {
        if (node is WorldEnvironment)
            return true;
        foreach (var child in node.GetChildren())
            if (SceneHasEnvironment(child))
                return true;
        return false;
    }

    private static Godot.Environment DungeonEnvironment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Color,
        BackgroundColor = new Color(0.015f, 0.015f, 0.025f),
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color(0.28f, 0.30f, 0.48f),
        AmbientLightEnergy = 0.12f, // low, so torch pools stand out against the dark
        // Very light fog only — at the far ortho camera (~1100 units) even a small
        // density greatly flattens the scene and washes out the torch pools.
        FogEnabled = true,
        FogLightColor = new Color(0.03f, 0.03f, 0.05f),
        FogDensity = 0.0005f,
    };

    private void SetupHud()
    {
        var canvas = new CanvasLayer();
        AddChild(canvas);
        _hud = new Label { Position = new Vector2(16, 12) };
        canvas.AddChild(_hud);
        _invPanel = new Label { Position = new Vector2(16, 44), Visible = false };
        canvas.AddChild(_invPanel);

        // Player health bar, centred along the top edge. Anchored to the top-centre
        // so it holds its place as the window scales (stretch mode = viewport).
        var barBg = new ColorRect
        {
            Color = new Color(0.08f, 0.08f, 0.10f, 0.85f), // dark frame / empty track
            MouseFilter = Control.MouseFilterEnum.Ignore,   // never intercept input
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -HudBarWidth / 2f, OffsetRight = HudBarWidth / 2f,
            OffsetTop = HudBarMargin, OffsetBottom = HudBarMargin + HudBarHeight,
        };
        canvas.AddChild(barBg);

        // Chip first so it sits behind the fill: it shows in the gap the shrinking
        // fill leaves, marking health just lost, then drains down to meet the fill.
        _healthBarChip = new ColorRect
        {
            Color = new Color(1.0f, 0.7f, 0.7f, 0.95f), // very light red "recently lost" trail
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(HudBarPad, HudBarPad),
            Size = new Vector2(HudBarWidth - 2 * HudBarPad, HudBarHeight - 2 * HudBarPad),
        };
        barBg.AddChild(_healthBarChip);

        _healthBarFill = new ColorRect
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(HudBarPad, HudBarPad),
            Size = new Vector2(HudBarWidth - 2 * HudBarPad, HudBarHeight - 2 * HudBarPad),
        };
        barBg.AddChild(_healthBarFill);

        _healthBarLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(HudBarWidth, HudBarHeight),
        };
        barBg.AddChild(_healthBarLabel); // added after the fill → drawn on top
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
        UpdateHud(delta);
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

        // Predict our own attack animation so the swing is instant (everyone else plays
        // theirs from the authoritative snapshot flag). Mirror the server's cooldown so we
        // trigger at the same cadence it will.
        _localAttackCooldown = Mathf.Max(0f, _localAttackCooldown - SimConstants.TickDelta);
        _localAttackAnim = Mathf.Max(0f, _localAttackAnim - SimConstants.TickDelta);
        if (attack && _localAttackCooldown <= 0f)
        {
            _localAttackAnim = SimConstants.AttackAnimDuration;
            _localAttackCooldown = SimConstants.PlayerAttackCooldown;
        }

        // Movement is predicted; damage, loot, and equipment stay server-authoritative.
        _prediction.Predict(input);

        // ReliableOrdered so the server's per-player input buffer receives every input
        // exactly once, in order — that 1:1 replay is what keeps reconciliation drift-free.
        _server.Send(
            NetProtocol.Frame(MessageType.Input, new InputPacket
            {
                MoveX = move.X, MoveZ = move.Y, Attack = attack, Sequence = input.Sequence,
            }),
            Channel, DeliveryMethod.ReliableOrdered);
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
            var feet = new Vector3(p.X, p.Y, p.Z);
            var view = GetOrCreatePlayerView(p.Id, feet);
            view.Attacking = p.Attacking;

            if (p.Id == _localPlayerId)
            {
                if (p.Health < _localHealth) _hudChipHold = ChipHoldTime; // took a hit → linger the chip
                _localHealth = p.Health; // authoritative, never predicted
                if (_prediction is not null)
                {
                    // Absorb the reconciliation correction into a decaying render error so the
                    // authoritative snap eases in over a few frames instead of popping.
                    var before = _prediction.Position;
                    _prediction.Reconcile(new SysVec3(p.X, p.Y, p.Z), p.LastProcessedInput);
                    _renderError += before - _prediction.Position;
                }
            }
            else
            {
                _remoteTargets[p.Id] = feet;
            }
        }
        Prune(_playerViews, seenPlayers, _remoteTargets);

        var seenEnemies = new HashSet<int>();
        foreach (var e in snapshot.Enemies)
        {
            seenEnemies.Add(e.Id);
            var feet = new Vector3(e.X, e.Y, e.Z);
            // Tolerate an unknown Type byte (version-skewed server / corrupt stream)
            // by falling back to Minion — never crash the receive path over cosmetics.
            var type = e.Type <= (byte)EnemyType.Boss ? (EnemyType)e.Type : EnemyType.Minion;
            var view = GetOrCreateEnemyView(e.Id, feet, type);
            view.Attacking = e.Attacking;
            _enemyTargets[e.Id] = feet;

            var frac = Mathf.Clamp(e.Health / EnemyArchetypes.Of(type).MaxHealth, 0f, 1f);
            if (frac < view.HealthFrac) view.ChipHold = ChipHoldTime; // took a hit → linger the chip
            view.HealthFrac = frac; // fill + chip are rendered per-frame in UpdateEnemyBar
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
        PruneLoot(seenLoot);

        var seenBolts = new HashSet<int>();
        foreach (var p in snapshot.Projectiles)
        {
            seenBolts.Add(p.Id);
            var pos = new Vector3(p.X, p.Y, p.Z);
            _projectileTargets[p.Id] = pos;
            GetOrCreateProjectileView(p.Id, pos);
        }
        PruneProjectiles(seenBolts);
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
                // Ease reconciliation corrections out instead of popping — but snap a large jump
                // (respawn/teleport), which isn't a correction and shouldn't be smoothed.
                if (_renderError.LengthSquared() > MaxSnapError * MaxSnapError)
                    _renderError = SysVec3.Zero;
                _renderError *= Mathf.Exp(-ErrorDecayRate * (float)delta);
                _localRenderPos = SysVec3.Lerp(_prevTickPos, _prediction.Position, alpha) + _renderError;
                view.Root.Position = ToGodot(_localRenderPos);
                view.Attacking = _localAttackAnim > 0f; // predicted (instant), not the snapshot flag
            }
            else if (_remoteTargets.TryGetValue(id, out var target))
            {
                view.Root.Position = view.Root.Position.Lerp(target, factor);
            }
            AnimateCharacter(view, delta);
        }

        foreach (var (id, view) in _enemyViews)
        {
            if (_enemyTargets.TryGetValue(id, out var target))
                view.Root.Position = view.Root.Position.Lerp(target, factor);
            AnimateCharacter(view, delta);
            UpdateEnemyBar(view, (float)delta);
        }

        // Bolts move fast; ease hard toward the latest snapshot so 20 Hz steps read smoothly.
        var boltFactor = Mathf.Clamp((float)delta * 30f, 0f, 1f);
        foreach (var (id, view) in _projectileViews)
            if (_projectileTargets.TryGetValue(id, out var target))
                view.Position = view.Position.Lerp(target, boltFactor);

        // Loot gems spin and bob so drops catch the eye.
        var bob = Mathf.Sin((float)_elapsed * 3f) * 4f;
        foreach (var (lid, lview) in _lootViews)
        {
            if (_lootBase.TryGetValue(lid, out var ground))
                lview.Position = ground + Vector3.Up * (LootY + bob);
            lview.RotateY((float)delta * 2f);
        }
    }

    // Faces the character along its movement and plays idle/run/attack. Replaying a
    // clip once it finishes makes every clip loop without touching import settings.
    private void AnimateCharacter(CharacterView view, double delta)
    {
        var pos = view.Root.Position;
        var flat = new Vector3(pos.X - view.LastPos.X, 0f, pos.Z - view.LastPos.Z);
        view.LastPos = pos;

        // Low-pass the velocity so per-frame reconciliation micro-corrections don't make the
        // model twitch its facing or flicker between idle/run.
        var frameVel = flat / Mathf.Max((float)delta, 0.0001f);
        view.SmoothVel = view.SmoothVel.Lerp(frameVel, Mathf.Clamp((float)delta * VelSmoothRate, 0f, 1f));
        var speed = view.SmoothVel.Length();

        var moving = speed > MoveAnimSpeed;
        if (moving)
        {
            var targetYaw = Mathf.Atan2(view.SmoothVel.X, view.SmoothVel.Z) + ModelYawOffset;
            view.Yaw = Mathf.LerpAngle(view.Yaw, targetYaw, Mathf.Clamp((float)delta * TurnSpeed, 0f, 1f));
            view.Pivot.Rotation = new Vector3(0f, view.Yaw, 0f);
        }

        if (view.Anim is null)
            return;

        var desired = view.Attacking ? view.AttackClip : moving ? AnimRun : AnimIdle;
        if (desired != view.Clip || !view.Anim.IsPlaying())
        {
            view.Anim.SpeedScale = desired == view.AttackClip ? AttackSpeedScale(view.Anim, view.AttackClip) : 1f;
            view.Anim.Play(desired);
            view.Clip = desired;
        }
    }

    // Speed the attack clip so its full swing fits the authoritative attack window.
    private static float AttackSpeedScale(AnimationPlayer anim, string clip) =>
        anim.HasAnimation(clip)
            ? (float)anim.GetAnimation(clip).Length / SimConstants.AttackAnimDuration
            : 1f;

    private void UpdateCamera(double delta)
    {
        var target = _prediction is not null
            ? ToGodot(_localRenderPos) + Vector3.Up * BodyHeight // follow the smoothed render position
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

    private void UpdateHud(double delta)
    {
        var attack = SimConstants.PlayerAttackDamage + PowerOf(_equippedWeaponId) + PowerOf(_equippedTrinketId);
        var reduction = PowerOf(_equippedArmorId) * SimConstants.ArmorDamageReductionPerPower;

        // Top-of-screen health bar: the fill snaps to current health; the chip behind
        // it lingers then drains, so a hit leaves a brief pale trail of what was lost.
        var frac = Mathf.Clamp(_localHealth / SimConstants.PlayerMaxHealth, 0f, 1f);
        AdvanceChip(ref _hudChipFrac, ref _hudChipHold, frac, (float)delta);
        var inner = HudBarHeight - 2 * HudBarPad;
        _healthBarChip.Size = new Vector2((HudBarWidth - 2 * HudBarPad) * _hudChipFrac, inner);
        _healthBarFill.Size = new Vector2((HudBarWidth - 2 * HudBarPad) * frac, inner);
        _healthBarFill.Color = Color.FromHsv(0.33f * frac, 0.75f, 0.8f); // green when full → red when low
        _healthBarLabel.Text = $"{Mathf.RoundToInt(_localHealth)} / {Mathf.RoundToInt(SimConstants.PlayerMaxHealth)}";

        _hud.Text = $"Items {_inventory.Count}   Atk {attack:0}   Armor {reduction:0.0}   " +
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

    // Hand-crafted maps render their own scene; a missing scene falls back to
    // placeholder boxes built from the collision solids.
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
        var selfLit = SceneHasEnvironment(scene);
        if (!selfLit)
            AddDefaultLighting(); // map brings no WorldEnvironment → light it with the default
        GD.Print($"Rendering authored map scene '{path}' ({_fadeMeshes.Count} fade-aware meshes, " +
                 $"{(selfLit ? "self-lit" : "default lighting")})");
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

        AddDefaultLighting(); // placeholder rendering brings no scene, so light it here

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

        AddChild(new MultiMeshInstance3D { Multimesh = _wallMulti, MaterialOverride = WallMaterial() });
    }

    // Fade whatever is between the camera and the local player so it is never
    // hidden — placeholder wall instances and authored scene meshes alike.
    private void UpdateWallFade()
    {
        if (_prediction is null)
            return;

        var player = ToGodot(_localRenderPos) + Vector3.Up * BodyHeight;

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

    private CharacterView GetOrCreatePlayerView(int id, Vector3 feet)
    {
        if (_playerViews.TryGetValue(id, out var existing))
            return existing;

        var scene = id == _localPlayerId ? _localCharScene : _remoteCharScene;
        var view = CreateCharacter(scene, feet, CharScale);
        _playerViews[id] = view;
        return view;
    }

    private CharacterView GetOrCreateEnemyView(int id, Vector3 feet, EnemyType type)
    {
        if (_enemyViews.TryGetValue(id, out var existing))
            return existing;

        var visual = EnemyVisuals[type];
        var view = CreateCharacter(_enemyCharScenes[type], feet, visual.Scale);
        view.AttackClip = visual.AttackClip;
        view.BarHeight = visual.BarHeight;
        view.BarScale = visual.BarScale;
        AttachHealthBar(view.Root, view);
        _enemyViews[id] = view;
        return view;
    }

    private CharacterView CreateCharacter(PackedScene scene, Vector3 feet, float charScale)
    {
        var holder = new Node3D { Position = feet }; // snap to real spot (no lerp-in from origin)
        var pivot = new Node3D();
        var model = scene.Instantiate<Node3D>();
        model.Scale = Vector3.One * charScale;
        pivot.AddChild(model);
        holder.AddChild(pivot);
        AddChild(holder);

        var view = new CharacterView
        {
            Root = holder,
            Pivot = pivot,
            Anim = FindAnimPlayer(model),
            LastPos = feet,
        };
        if (view.Anim is not null)
        {
            view.Anim.Play(AnimIdle);
            view.Clip = AnimIdle;
        }
        return view;
    }

    private static AnimationPlayer? FindAnimPlayer(Node node)
    {
        if (node is AnimationPlayer ap)
            return ap;
        foreach (var child in node.GetChildren())
            if (FindAnimPlayer(child) is { } found)
                return found;
        return null;
    }

    private void AttachHealthBar(Node3D holder, CharacterView view)
    {
        var bg = new MeshInstance3D
        {
            Mesh = _barMesh,
            Position = new Vector3(0, view.BarHeight, 0),
            Scale = new Vector3(view.BarScale, view.BarScale, 1f),
            MaterialOverride = BarMaterial(new Color(0.05f, 0.05f, 0.05f)),
        };
        holder.AddChild(bg);

        // Chip sits between the track and the fill; it lingers where the fill was,
        // marking freshly-lost health, then drains down to meet it.
        var chip = new MeshInstance3D
        {
            Mesh = _barMesh,
            Position = new Vector3(0, view.BarHeight, 0.1f),
            MaterialOverride = BarMaterial(new Color(1.0f, 0.7f, 0.7f)), // very light red trail
        };
        holder.AddChild(chip);

        var fill = new MeshInstance3D
        {
            Mesh = _barMesh,
            Position = new Vector3(0, view.BarHeight, 0.2f),
            MaterialOverride = BarMaterial(new Color(0.85f, 0.15f, 0.15f)), // solid red (hostile)
        };
        holder.AddChild(fill);

        view.HealthChip = chip;
        view.HealthFill = fill;
    }

    // Per-frame: drain the chip toward current health, then place both bars.
    private static void UpdateEnemyBar(CharacterView view, float delta)
    {
        if (view.HealthFill is null)
            return;

        AdvanceChip(ref view.ChipFrac, ref view.ChipHold, view.HealthFrac, delta);

        PlaceBar(view, view.HealthChip, view.ChipFrac, 0.1f);
        PlaceBar(view, view.HealthFill, view.HealthFrac, 0.2f);
    }

    // Scale a billboard bar to its fraction and left-anchor it. The quad billboards
    // (keep-scale), so its width shrinks along the camera's right axis; shifting the
    // centre left along that SAME axis by half the lost width keeps the left edge fixed
    // and drains from the right, like the HUD bar. (A world-X shift skews diagonally and
    // reads as a detached second bar.) Bar height/size come from the view (boss bars
    // sit higher and are drawn larger).
    private static void PlaceBar(CharacterView view, MeshInstance3D? bar, float frac, float z)
    {
        if (bar is null)
            return;
        frac = Mathf.Clamp(frac, 0f, 1f);
        bar.Scale = new Vector3(Mathf.Max(frac, 0.001f) * view.BarScale, view.BarScale, 1f);
        bar.Position = new Vector3(0f, view.BarHeight, z)
                       - EnemyBarWidth * view.BarScale * (1f - frac) / 2f * BillboardRight;
    }

    // Chip easing shared by the HUD and enemy bars: snap up on heal, else linger
    // (ChipHold) at the pre-hit level and then drain down to the current health.
    private static void AdvanceChip(ref float chipFrac, ref float chipHold, float target, float delta)
    {
        if (target >= chipFrac)
        {
            chipFrac = target; // healed or steady — no trailing chip
            return;
        }
        if (chipHold > 0f)
        {
            chipHold -= delta;
            return;
        }
        chipFrac = Mathf.MoveToward(chipFrac, target, ChipDrainRate * delta);
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

    private static void Prune(Dictionary<int, CharacterView> views, HashSet<int> seen,
                              Dictionary<int, Vector3>? targets = null)
    {
        foreach (var id in new List<int>(views.Keys))
        {
            if (seen.Contains(id))
                continue;
            views[id].Root.QueueFree();
            views.Remove(id);
            targets?.Remove(id);
        }
    }

    private void PruneLoot(HashSet<int> seen)
    {
        foreach (var id in new List<int>(_lootViews.Keys))
        {
            if (seen.Contains(id))
                continue;
            _lootViews[id].QueueFree();
            _lootViews.Remove(id);
            _lootBase.Remove(id);
        }
    }

    private MeshInstance3D GetOrCreateProjectileView(int id, Vector3 spawn)
    {
        if (_projectileViews.TryGetValue(id, out var existing))
            return existing;

        var view = new MeshInstance3D
        {
            Mesh = _boltMesh,
            Position = spawn, // snap the first frame; eased thereafter
            MaterialOverride = _boltMaterial,
        };
        AddChild(view);
        _projectileViews[id] = view;
        return view;
    }

    private void PruneProjectiles(HashSet<int> seen)
    {
        foreach (var id in new List<int>(_projectileViews.Keys))
        {
            if (seen.Contains(id))
                continue;
            _projectileViews[id].QueueFree();
            _projectileViews.Remove(id);
            _projectileTargets.Remove(id);
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

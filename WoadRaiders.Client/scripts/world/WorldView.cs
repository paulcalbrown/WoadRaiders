using Godot;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Client;

/// <summary>
/// Renders the snapshot stream: players, enemies, ground loot, and projectiles,
/// each as a <see cref="ViewMap{TView}"/> keyed by entity id. Applying a snapshot
/// diffs it against the live views (create on first sight, prune on
/// disappearance); the per-frame update eases everything toward its latest
/// authoritative target and animates it. The local player's view is special: its
/// position comes from prediction, not from lerping snapshots.
/// </summary>
public sealed class WorldView
{
    private const float RemoteSmoothing = 18f; // snapshot-lerp rate for remote characters
    private const float BoltSmoothing = 30f;   // bolts move fast; ease hard so 20 Hz steps read smoothly
    private const float LootY = 14f;           // gem hover height above its ground point

    // Characters (KayKit models are ~2.47 units tall → ~20x to reach ~49 world units).
    private const float CharScale = 20f;

    /// <summary>How each enemy type looks: model, size, swing, and health-bar placement.</summary>
    private readonly record struct EnemyVisual(string SceneFile, float Scale, string AttackClip, float BarHeight, float BarScale);

    private static readonly Dictionary<EnemyType, EnemyVisual> EnemyVisuals = new()
    {
        [EnemyType.Minion] = new("Skeleton_Minion.glb", 20f, "1H_Melee_Attack_Chop", 54f, 1f),
        [EnemyType.Rogue] = new("Skeleton_Rogue.glb", 20f, "1H_Melee_Attack_Stab", 54f, 1f),
        [EnemyType.Mage] = new("Skeleton_Mage.glb", 20f, "Spellcast_Shoot", 54f, 1f),
        [EnemyType.Boss] = new("Skeleton_Warrior.glb", 44f, "2H_Melee_Attack_Chop", 122f, 2f),
    };

    private sealed class LootView
    {
        public MeshInstance3D Node = null!;
        public Vector3 Ground; // point the gem bobs above
    }

    private sealed class ProjectileView
    {
        public MeshInstance3D Node = null!;
        public Vector3 Target;
    }

    private readonly Node3D _parent;
    private readonly ViewMap<CharacterView> _players = new(v => v.QueueFree());
    private readonly ViewMap<CharacterView> _enemies = new(v => v.QueueFree());
    private readonly ViewMap<LootView> _loot = new(v => v.Node.QueueFree());
    private readonly ViewMap<ProjectileView> _projectiles = new(v => v.Node.QueueFree());

    private readonly BoxMesh _lootMesh = new() { Size = new Vector3(16, 16, 16) };
    private readonly SphereMesh _boltMesh = new() { Radius = 9f, Height = 18f, RadialSegments = 8, Rings = 4 };
    private readonly QuadMesh _barMesh = new() { Size = new Vector2(40, 6) };
    private readonly StandardMaterial3D _boltMaterial = new()
    {
        AlbedoColor = new Color(0.6f, 0.35f, 1f),  // arcane violet
        EmissionEnabled = true,
        Emission = new Color(0.7f, 0.45f, 1f),
        EmissionEnergyMultiplier = 3f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };

    private readonly PackedScene _localCharScene;
    private readonly PackedScene _remoteCharScene;
    private readonly Dictionary<EnemyType, PackedScene> _enemyCharScenes = new();

    private double _elapsed; // drives the loot bob

    public WorldView(Node3D parent)
    {
        _parent = parent;

        const string adv = "res://addons/kaykit_character_pack_adventures/Characters/gltf";
        const string skel = "res://addons/kaykit_character_pack_skeletons/Characters/gltf";
        _localCharScene = GD.Load<PackedScene>($"{adv}/Knight.glb");
        _remoteCharScene = GD.Load<PackedScene>($"{adv}/Barbarian.glb");
        foreach (var (type, visual) in EnemyVisuals)
            _enemyCharScenes[type] = GD.Load<PackedScene>($"{skel}/{visual.SceneFile}");
    }

    /// <summary>Diff one authoritative snapshot into the live views.</summary>
    public void Apply(WorldSnapshotPacket snapshot, int localPlayerId)
    {
        foreach (var p in snapshot.Players)
        {
            var feet = new Vector3(p.X, p.Y, p.Z);
            if (!_players.Touch(p.Id, out var view))
            {
                var scene = p.Id == localPlayerId ? _localCharScene : _remoteCharScene;
                view = CharacterView.Spawn(_parent, scene, feet, CharScale);
                _players.Add(p.Id, view);
            }
            view.Attacking = p.Attacking; // the local view's flag is re-derived from prediction each frame
            view.Target = feet;           // ignored for the local view — prediction drives it
        }
        _players.Prune();

        foreach (var e in snapshot.Enemies)
        {
            var feet = new Vector3(e.X, e.Y, e.Z);
            // Tolerate an unknown Type byte (version-skewed server / corrupt stream)
            // by falling back to Minion — never crash the receive path over cosmetics.
            var type = e.Type <= (byte)EnemyType.Boss ? (EnemyType)e.Type : EnemyType.Minion;
            if (!_enemies.Touch(e.Id, out var view))
            {
                var visual = EnemyVisuals[type];
                view = CharacterView.Spawn(_parent, _enemyCharScenes[type], feet, visual.Scale);
                view.AttackClip = visual.AttackClip;
                view.AttachHealthBar(_barMesh, visual.BarHeight, visual.BarScale);
                _enemies.Add(e.Id, view);
            }
            view.Attacking = e.Attacking;
            view.Target = feet;
            view.SetHealthFraction(e.Health / EnemyArchetypes.Of(type).MaxHealth);
        }
        _enemies.Prune();

        foreach (var g in snapshot.GroundItems)
        {
            var ground = new Vector3(g.X, g.Y, g.Z);
            if (!_loot.Touch(g.Id, out var view))
            {
                view = new LootView { Node = CreateLootNode(g.Rarity, ground + Vector3.Up * LootY) };
                _loot.Add(g.Id, view);
            }
            view.Ground = ground;
        }
        _loot.Prune();

        foreach (var p in snapshot.Projectiles)
        {
            var pos = new Vector3(p.X, p.Y, p.Z);
            if (!_projectiles.Touch(p.Id, out var view))
            {
                view = new ProjectileView { Node = CreateBoltNode(pos) }; // snap the first frame; eased thereafter
                _projectiles.Add(p.Id, view);
            }
            view.Target = pos;
        }
        _projectiles.Prune();
    }

    /// <summary>
    /// Per-frame: ease remote views toward their snapshot targets, drive the local
    /// view from the predicted render position, animate everyone, spin the loot.
    /// </summary>
    public void Update(double delta, int localPlayerId, Vector3 localRenderPos, bool localSwinging)
    {
        var factor = Mathf.Clamp((float)delta * RemoteSmoothing, 0f, 1f);

        foreach (var (id, view) in _players.Items)
        {
            if (id == localPlayerId)
            {
                view.Position = localRenderPos;
                view.Attacking = localSwinging; // predicted (instant), not the snapshot flag
            }
            else
            {
                view.Position = view.Position.Lerp(view.Target, factor);
            }
            view.Animate(delta);
        }

        foreach (var view in _enemies.Items.Values)
        {
            view.Position = view.Position.Lerp(view.Target, factor);
            view.Animate(delta);
            view.UpdateBar((float)delta);
        }

        var boltFactor = Mathf.Clamp((float)delta * BoltSmoothing, 0f, 1f);
        foreach (var view in _projectiles.Items.Values)
            view.Node.Position = view.Node.Position.Lerp(view.Target, boltFactor);

        // Loot gems spin and bob so drops catch the eye.
        _elapsed += delta;
        var bob = Mathf.Sin((float)_elapsed * 3f) * 4f;
        foreach (var view in _loot.Items.Values)
        {
            view.Node.Position = view.Ground + Vector3.Up * (LootY + bob);
            view.Node.RotateY((float)delta * 2f);
        }
    }

    private MeshInstance3D CreateLootNode(byte rarity, Vector3 spawn)
    {
        var node = new MeshInstance3D
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
        _parent.AddChild(node);
        return node;
    }

    private MeshInstance3D CreateBoltNode(Vector3 spawn)
    {
        var node = new MeshInstance3D
        {
            Mesh = _boltMesh,
            Position = spawn,
            MaterialOverride = _boltMaterial,
        };
        _parent.AddChild(node);
        return node;
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

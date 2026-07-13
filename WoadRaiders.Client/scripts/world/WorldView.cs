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

    /// <summary>How each player class looks: the KayKit adventurer model and its strike clip.
    /// The Ranger borrows the hooded rogue body — the pack ships no dedicated ranger.</summary>
    private static readonly Dictionary<CharacterClass, (string SceneFile, string AttackClip)> ClassVisuals = new()
    {
        [CharacterClass.Knight] = ("Knight.glb", "1H_Melee_Attack_Chop"),
        [CharacterClass.Rogue] = ("Rogue.glb", "1H_Melee_Attack_Stab"),
        [CharacterClass.Mage] = ("Mage.glb", "Spellcast_Shoot"),
        [CharacterClass.Ranger] = ("Rogue_Hooded.glb", "2H_Ranged_Shoot"),
    };

    // Every character carries a light. Players glow warm (torch-lit raiders);
    // the undead enemies give off a cold spectral light — a clear friend/foe read.
    private static readonly Color PlayerLight = new(1.0f, 0.62f, 0.32f);
    private static readonly Color EnemyLight = new(0.45f, 0.72f, 1.0f);

    private sealed class LootView
    {
        public Node3D Node = null!;    // a gem MeshInstance3D, or an instantiated KayKit prop scene
        public Vector3 Ground;         // point the loot bobs above
    }

    private sealed class ProjectileView
    {
        public Node3D Node = null!;
        public Vector3 Target;
        public bool Oriented; // arrows point along their flight; bolts are round and don't care
    }

    private readonly Node3D _parent;
    private readonly ViewMap<CharacterView> _players = new(v => v.QueueFree());
    private readonly ViewMap<CharacterView> _enemies = new(v => v.QueueFree());
    private readonly ViewMap<LootView> _loot = new(v => v.Node.QueueFree());
    private readonly ViewMap<ProjectileView> _projectiles = new(v => v.Node.QueueFree());

    private readonly BoxMesh _lootMesh = new() { Size = new Vector3(16, 16, 16) }; // equipment gem (rarity-colored)
    private readonly SphereMesh _boltMesh = new() { Radius = 9f, Height = 18f, RadialSegments = 8, Rings = 4 };
    private readonly QuadMesh _barMesh = new() { Size = new Vector2(40, 6) };
    private readonly StandardMaterial3D _enemyBoltMaterial = new()
    {
        AlbedoColor = new Color(0.6f, 0.35f, 1f),  // arcane violet — hostile
        EmissionEnabled = true,
        Emission = new Color(0.7f, 0.45f, 1f),
        EmissionEnergyMultiplier = 3f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };
    private readonly StandardMaterial3D _magicBoltMaterial = new()
    {
        AlbedoColor = new Color(0.35f, 0.85f, 1f), // cold woad-fire — the player mage's
        EmissionEnabled = true,
        Emission = new Color(0.45f, 0.9f, 1f),
        EmissionEnergyMultiplier = 3f,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
    };

    private const float ArrowLength = 34f;   // KayKit arrow prop, fitted to this world length

    private readonly Dictionary<CharacterClass, PackedScene> _classScenes = new();
    private readonly Dictionary<int, byte> _playerClassSeen = new(); // class each player view was built as
    private readonly List<int> _classLedgerScratch = new();          // reused; keeps Apply allocation-free
    private readonly PackedScene _arrowScene;
    private readonly Dictionary<EnemyType, PackedScene> _enemyCharScenes = new();

    // Authored KayKit props for the non-equipment loot, kept with their own
    // materials (gold coins, green glass) rather than a tinted primitive.
    private readonly PackedScene _goldScene;
    private readonly PackedScene _potionScene;

    // Equipment renders as the actual KayKit weapon mesh for its ItemType. Each
    // weapon .gltf shares the knight texture atlas that's already imported.
    private static readonly Dictionary<ItemType, string> WeaponMeshFiles = new()
    {
        [ItemType.Sword] = "sword_1handed.gltf",
        [ItemType.Greatsword] = "sword_2handed.gltf",
        [ItemType.Axe] = "axe_1handed.gltf",
        [ItemType.Battleaxe] = "axe_2handed.gltf",
        [ItemType.Dagger] = "dagger.gltf",
        [ItemType.Staff] = "staff.gltf",
        [ItemType.Crossbow] = "crossbow_1handed.gltf",
        [ItemType.Shield] = "shield_round.gltf",
    };

    private readonly Dictionary<ItemType, PackedScene> _weaponScenes = new();

    // KayKit props/weapons are authored on a ~1-unit grid, so they need scaling up
    // to this world (a character is ~49 units). Each is fitted to this size by its
    // own bounds, so every loot piece reads at a consistent, visible scale.
    private const float LootPropSize = 26f;

    // Every drop glows in a coloured pool so it reads as special in the dark dungeon:
    // gold golden, potions green, equipment by rarity. A cheap shadowless point light
    // sits inside the floating prop — it leaves the prop's own materials untouched.
    private static readonly Color GoldGlow = new(1.0f, 0.78f, 0.15f);
    private static readonly Color PotionGlow = new(0.30f, 1.0f, 0.40f);
    private const float GlowRange = 100f;   // pool reaches the floor under the floating loot
    private const float GlowEnergy = 4f;    // bright enough to read against the low dungeon ambient
    private const float GlowHeight = 12f;    // sits within the fitted prop

    private double _elapsed; // drives the loot bob

    public WorldView(Node3D parent)
    {
        _parent = parent;

        const string adv = "res://addons/kaykit_character_pack_adventures/Characters/gltf";
        const string weapons = "res://addons/kaykit_character_pack_adventures/Assets/gltf";
        const string skel = "res://addons/kaykit_character_pack_skeletons/Characters/gltf";
        const string dungeon = "res://addons/kaykit_dungeon_remastered/Assets/gltf";
        foreach (var (cls, visual) in ClassVisuals)
            _classScenes[cls] = GD.Load<PackedScene>($"{adv}/{visual.SceneFile}");
        _arrowScene = GD.Load<PackedScene>($"{weapons}/arrow.gltf");
        _goldScene = GD.Load<PackedScene>($"{dungeon}/coin_stack_large.gltf.glb");
        _potionScene = GD.Load<PackedScene>($"{dungeon}/bottle_A_green.gltf.glb");
        foreach (var (itemType, file) in WeaponMeshFiles)
            _weaponScenes[itemType] = GD.Load<PackedScene>($"{weapons}/{file}");
        foreach (var (type, visual) in EnemyVisuals)
            _enemyCharScenes[type] = GD.Load<PackedScene>($"{skel}/{visual.SceneFile}");
    }

    /// <summary>Diff one authoritative snapshot into the live views.</summary>
    public void Apply(WorldSnapshotPacket snapshot, int localPlayerId)
    {
        foreach (var p in snapshot.Players)
        {
            var feet = new Vector3(p.X, p.Y, p.Z);
            // Tolerate an unknown Class byte the same way as enemy types: fall back
            // to Knight — never crash the receive path over cosmetics.
            var cls = p.Class <= (byte)CharacterClass.Ranger ? (CharacterClass)p.Class : CharacterClass.Knight;

            // A player's class can change on the same id (the server honors the join
            // profile a tick after it creates them) — rebuild the view for the new body.
            if (_playerClassSeen.TryGetValue(p.Id, out var seen) && seen != (byte)cls)
                _players.Remove(p.Id);

            if (!_players.Touch(p.Id, out var view))
            {
                view = CharacterView.Spawn(_parent, _classScenes[cls], feet, CharScale, PlayerLight, cls);
                view.AttackClip = ClassVisuals[cls].AttackClip;
                _players.Add(p.Id, view);
                _playerClassSeen[p.Id] = (byte)cls;
            }
            view.Attacking = p.Attacking; // the local view's flag is re-derived from prediction each frame
            view.Target = feet;           // ignored for the local view — prediction drives it
        }
        _players.Prune();

        // Keep the class ledger in step with the pruned views (ids leave and recycle).
        _classLedgerScratch.Clear();
        foreach (var id in _playerClassSeen.Keys)
            if (!_players.Items.ContainsKey(id))
                _classLedgerScratch.Add(id);
        foreach (var id in _classLedgerScratch)
            _playerClassSeen.Remove(id);

        foreach (var e in snapshot.Enemies)
        {
            var feet = new Vector3(e.X, e.Y, e.Z);
            // Tolerate an unknown Type byte (version-skewed server / corrupt stream)
            // by falling back to Minion — never crash the receive path over cosmetics.
            var type = e.Type <= (byte)EnemyType.Boss ? (EnemyType)e.Type : EnemyType.Minion;
            if (!_enemies.Touch(e.Id, out var view))
            {
                var visual = EnemyVisuals[type];
                view = CharacterView.Spawn(_parent, _enemyCharScenes[type], feet, visual.Scale, EnemyLight);
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
                view = new LootView { Node = CreateLootNode(g.Kind, g.Type, g.Rarity, ground + Vector3.Up * LootY) };
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
                view = CreateProjectileView(p.Kind, pos); // snap the first frame; eased thereafter
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
    public void Update(double delta, int localPlayerId, Vector3 localRenderPos, bool localSwinging, Vector3 localAttackFacing)
    {
        var factor = Mathf.Clamp((float)delta * RemoteSmoothing, 0f, 1f);

        foreach (var (id, view) in _players.Items)
        {
            if (id == localPlayerId)
            {
                view.Position = localRenderPos;
                view.Attacking = localSwinging; // predicted (instant), not the snapshot flag
                if (localSwinging && localAttackFacing.LengthSquared() > 0.0001f)
                    view.FaceToward(localAttackFacing); // hold the click direction through the swing
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
        {
            // Arrows face where they're going; do it before the lerp eats the offset.
            if (view.Oriented && view.Node.Position.DistanceSquaredTo(view.Target) > 1f)
                view.Node.LookAt(view.Target);
            view.Node.Position = view.Node.Position.Lerp(view.Target, boltFactor);
        }

        // Loot hovers, spins, and bobs so drops catch the eye.
        _elapsed += delta;
        var bob = Mathf.Sin((float)_elapsed * 3f) * 4f;
        foreach (var view in _loot.Items.Values)
        {
            view.Node.Position = view.Ground + Vector3.Up * (LootY + bob);
            view.Node.RotateY((float)delta * 2f);
        }
    }

    private Node3D CreateLootNode(byte kind, byte type, byte rarity, Vector3 spawn)
    {
        var lootKind = (LootKind)kind;

        // Gold and potions are their own KayKit props; equipment is the weapon mesh
        // for its ItemType. Any unknown byte (version skew) falls through to the gem
        // — never-crash-over-cosmetics, like unknown enemy types.
        var scene = lootKind switch
        {
            LootKind.Gold => _goldScene,
            LootKind.HealthPotion => _potionScene,
            LootKind.Equipment => _weaponScenes.GetValueOrDefault((ItemType)type),
            _ => null,
        };
        var node = scene is not null ? CreatePropLoot(scene, spawn) : CreateGemLoot(rarity, spawn);

        // Colour the glow: gold golden, potions green, equipment (and the gem
        // fallback) by rarity.
        var glow = lootKind switch
        {
            LootKind.Gold => GoldGlow,
            LootKind.HealthPotion => PotionGlow,
            _ => RarityColor(rarity),
        };
        node.AddChild(new OmniLight3D
        {
            Position = new Vector3(0, GlowHeight, 0),
            LightColor = glow,
            LightEnergy = GlowEnergy,
            OmniRange = GlowRange,
            ShadowEnabled = false, // a pickup halo needs no shadows — keeps it cheap
        });
        return node;
    }

    // A KayKit prop scene, fitted to LootPropSize by its own bounds and spun in place.
    private Node3D CreatePropLoot(PackedScene scene, Vector3 spawn)
    {
        var holder = new Node3D { Position = spawn };
        var model = scene.Instantiate<Node3D>();
        holder.AddChild(model);
        _parent.AddChild(holder); // in-tree so the mesh bounds below are valid

        var bounds = LocalMeshBounds(model, holder);
        var maxDim = Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z));
        if (maxDim > 0.0001f)
            model.Scale = Vector3.One * (LootPropSize / maxDim);
        return holder;
    }

    private MeshInstance3D CreateGemLoot(byte rarity, Vector3 spawn)
    {
        var color = RarityColor(rarity);
        var node = new MeshInstance3D
        {
            Mesh = _lootMesh,
            Position = spawn,
            RotationDegrees = new Vector3(0, 0, 45), // sit like a gem
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = color,
                EmissionEnabled = true,
                Emission = color,
                EmissionEnergyMultiplier = 0.4f,
            },
        };
        _parent.AddChild(node);
        return node;
    }

    // Union of every mesh's AABB in <paramref name="root"/>'s local space. The
    // instance must be in-tree so global transforms are valid. (Godot.Aabb —
    // disambiguated from the sim's Core.Aabb, which this file also uses.)
    private static Godot.Aabb LocalMeshBounds(Node instance, Node3D root)
    {
        var toRoot = root.GlobalTransform.AffineInverse();
        Godot.Aabb? total = null;
        foreach (var node in instance.SelfAndDescendants())
        {
            if (node is not MeshInstance3D mesh || mesh.Mesh is null)
                continue;
            var box = toRoot * mesh.GlobalTransform * mesh.GetAabb();
            total = total is { } t ? t.Merge(box) : box;
        }
        return total ?? new Godot.Aabb();
    }

    private ProjectileView CreateProjectileView(byte kind, Vector3 spawn)
    {
        // Unknown kinds render as the classic enemy bolt — never-crash-over-cosmetics.
        if ((ProjectileKind)kind == ProjectileKind.Arrow)
            return new ProjectileView { Node = CreateArrowNode(spawn), Oriented = true };
        return new ProjectileView { Node = CreateBoltNode(spawn, kind) };
    }

    private MeshInstance3D CreateBoltNode(Vector3 spawn, byte kind)
    {
        var node = new MeshInstance3D
        {
            Mesh = _boltMesh,
            Position = spawn,
            MaterialOverride = (ProjectileKind)kind == ProjectileKind.MagicBolt
                ? _magicBoltMaterial
                : _enemyBoltMaterial,
        };
        _parent.AddChild(node);
        return node;
    }

    // The KayKit arrow prop is authored lying along +Y; pitching it -90° maps
    // that onto -Z, the axis LookAt points down the flight path.
    private Node3D CreateArrowNode(Vector3 spawn)
    {
        var holder = new Node3D { Position = spawn };
        var model = _arrowScene.Instantiate<Node3D>();
        model.RotationDegrees = new Vector3(-90f, 0f, 0f);
        holder.AddChild(model);
        _parent.AddChild(holder); // in-tree so the mesh bounds below are valid

        var bounds = LocalMeshBounds(model, holder);
        var maxDim = Mathf.Max(bounds.Size.X, Mathf.Max(bounds.Size.Y, bounds.Size.Z));
        if (maxDim > 0.0001f)
            model.Scale = Vector3.One * (ArrowLength / maxDim);
        return holder;
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

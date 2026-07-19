using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb; // the sim's world box, not Godot's

namespace WoadRaiders.Client;

/// <summary>
/// The scene a realm design composes — a thin facade over the root Node3D that
/// knows the BAKE CONVENTIONS (terrain meshes in the "terrain" group, BoxShape3D
/// collision and the marker names) so a design never has to.
///
/// Everything here is a convenience, not a requirement. A design may ignore the
/// helpers entirely and hang whatever it likes off <see cref="Root"/> — any
/// meshes, materials, particles, or imported asset kits — because the served
/// geometry is baked FROM the finished scene afterwards, and the bake only
/// looks for the conventions above. Nothing else in the tree constrains it.
///
/// A fresh scene is EMPTY — no light, no sky, no ground. A design states its
/// whole look with <see cref="Add{T}"/> — a WorldEnvironment for the sky, and
/// whatever lights it wants — building them itself, since a look is the realm's
/// own (<see cref="TerrainSurface"/> and <see cref="SceneryRecipes"/> offer
/// ready-made pieces, but nothing obliges a realm to use them).
/// Nothing is inherited behind its back. (A realm that ships
/// no WorldEnvironment still renders — DungeonVisualBuilder notices and lights
/// it with the dungeon default at runtime — but it will not look designed.)
///
/// The only thing a realm MUST have is a player spawn; that is
/// Core.DungeonSceneFile's rule, not this class's. The root's NAME is not this
/// class's business either — <see cref="RealmSceneBuilder"/> stamps it from the
/// design's name when the scene is saved.
/// </summary>
public sealed class RealmScene
{
    /// <summary>The cell size Core.DungeonSceneFile assumes when the scene says
    /// nothing — a realm on this pitch needs no metadata at all.</summary>
    private const float DefaultCellSize = 40f;

    private Node3D? _solidVisuals;
    private StaticBody3D? _solidBodies;

    /// <summary>The scene root. Add anything Godot can express.</summary>
    public Node3D Root { get; } = new();

    /// <summary>The heightfield behind <see cref="GroundAt"/>, if the design gave
    /// one. Null for realms that place their ground by other means.</summary>
    public HeightField? Terrain { get; private set; }

    public int SolidCount { get; private set; }
    public int EnemyCount { get; private set; }
    public bool HasPlayerSpawn { get; private set; }
    public bool HasBoss { get; private set; }

    // ------------------------------------------------------------- the land

    /// <summary>Lay a heightfield down as a real displaced mesh: the bake tool
    /// samples it back off the "terrain" group, and <see cref="GroundAt"/> can
    /// place things on it. Only realms on a non-default cell pitch write any
    /// metadata; the rest stay convention-clean.
    ///
    /// What the land LOOKS like is the realm's own business, so both halves are
    /// required: <paramref name="colour"/> is baked into the mesh's vertices
    /// (height and surface-tilt in, colour out) and <paramref name="material"/>
    /// is what reads them back — they are a pair, so a material that ignores
    /// vertex colour discards the palette. TerrainSurface.Material reads them,
    /// and a realm supplies the colours — CragDesign's highland bands are one
    /// such palette, not the only one.
    ///
    /// Because the colours ride in the saved scene, a realm's palette needs no
    /// support from the wire format at all.</summary>
    public MeshInstance3D AddTerrain(HeightField field, Func<float, float, Color> colour, Material material)
    {
        var mesh = HeightFieldMesh.Build(field.OriginX, field.OriginZ, field.CellSize,
                                         field.Width, field.Depth, field.Heights.ToArray(), colour);
        return AddTerrain(mesh, field, material);
    }

    /// <summary>The position-aware variant: <paramref name="colour"/> also sees
    /// each vertex's world (x, z), for palettes that vary by place — see
    /// HeightFieldMesh's matching overload.</summary>
    public MeshInstance3D AddTerrain(HeightField field, Func<float, float, float, float, Color> colour, Material material)
    {
        var mesh = HeightFieldMesh.Build(field.OriginX, field.OriginZ, field.CellSize,
                                         field.Width, field.Depth, field.Heights.ToArray(), colour);
        return AddTerrain(mesh, field, material);
    }

    /// <summary>Lay down ground the design built itself — a sculpted mesh, an
    /// imported kit piece, anything. Pass <paramref name="sampler"/> when a
    /// heightfield describes the same ground, so <see cref="GroundAt"/> works;
    /// without it the bake still samples the real triangles.</summary>
    public MeshInstance3D AddTerrain(Mesh mesh, HeightField? sampler = null, Material? material = null)
    {
        if (sampler is not null)
        {
            Terrain = sampler;
            if (!Mathf.IsEqualApprox(sampler.CellSize, DefaultCellSize))
                Root.SetMeta("terrain_cell_size", sampler.CellSize);
        }

        // "terrain" is what the bake tool samples; "no_fade" keeps the occlusion
        // fader off the ground everything stands on.
        var node = new MeshInstance3D { Mesh = mesh, MaterialOverride = material };
        node.AddToGroup("terrain", persistent: true);
        node.AddToGroup("no_fade", persistent: true);
        return Attach(node, "Terrain");
    }

    /// <summary>The ground height under a point, per the heightfield the design
    /// laid down — 0 for realms that never gave one.</summary>
    public float GroundAt(float x, float z) => Terrain?.Sample(x, z) ?? 0f;

    /// <summary>A point set on the ground. The usual way to place a spawn or a
    /// landmark from plan-view (x, z) coordinates.</summary>
    public Vector3 OnGround(float x, float z) => new(x, GroundAt(x, z), z);

    // ------------------------------------------------------------- solids

    /// <summary>A blocking box: a visual and the matching BoxShape3D the bake
    /// reads as a solid. What solids are MADE of is the realm's business, so the
    /// material is required — pass one instance for a whole run of them and the
    /// saved scene stores it once.
    ///
    /// The collision is AUTHORED, not derived from the visual, and the two stay
    /// independent on purpose. Terrain goes the other way — the bake samples a
    /// heightfield from whatever mesh is grouped "terrain" — because sampling
    /// straight down is a faithful projection. There is no such reduction from
    /// an arbitrary mesh to an axis-aligned box, and inferring one would save
    /// nothing anyway: a realm's meshes are overwhelmingly NOT solid (in The
    /// Crag, 104 of 120), so "which meshes block" would need a group, exactly as
    /// this shape does. Authoring it instead buys collision that need not match
    /// any mesh: an invisible blocker sealing an exploit route, or one box
    /// standing in for a detailed kit piece.</summary>
    public void AddSolid(Aabb box, Material material)
    {
        _solidVisuals ??= Attach(new Node3D(), "SolidVisuals");
        // A StaticBody3D holder: the bake matches any CollisionShape3D wherever it
        // sits, and the game never runs Godot physics — but a shape orphaned from
        // a CollisionObject3D raises a configuration warning in the editor, and
        // these scenes are meant to open as clean, natural Godot files.
        _solidBodies ??= Attach(new StaticBody3D(), "Static");

        var center = box.Center.ToGodot();
        var size = box.Size.ToGodot();
        var index = SolidCount++;
        _solidVisuals.AddChild(new MeshInstance3D
        {
            Name = $"Solid_{index}",
            Position = center,
            Mesh = new BoxMesh { Size = size, Material = material },
        });
        _solidBodies.AddChild(new CollisionShape3D
        {
            Name = $"Col_{index}",
            Position = center,
            Shape = new BoxShape3D { Size = size },
        });
    }

    public void AddSolids(IEnumerable<Aabb> boxes, Material material)
    {
        foreach (var box in boxes)
            AddSolid(box, material);
    }

    // ------------------------------------------------------------- the cast

    /// <summary>Where the raiders arrive. Required — every realm needs one.</summary>
    public Marker3D SetPlayerSpawn(Vector3 position)
    {
        HasPlayerSpawn = true;
        return Attach(new Marker3D { Position = position }, "PlayerSpawn");
    }

    public Marker3D AddEnemy(EnemyType type, Vector3 position)
    {
        var suffix = type switch { EnemyType.Rogue => "_Rogue", EnemyType.Mage => "_Mage", _ => "" };
        return Attach(new Marker3D { Position = position }, $"EnemySpawn{EnemyCount++}{suffix}");
    }

    public Marker3D SetBossSpawn(Vector3 position)
    {
        HasBoss = true;
        return Attach(new Marker3D { Position = position }, "BossSpawn");
    }

    // ------------------------------------------------------------- free-form

    /// <summary>Hang anything else off the root — scenery, sound, an asset kit.
    /// Nothing added this way ever reaches the simulation.</summary>
    public T Add<T>(T node, string? name = null) where T : Node => Attach(node, name);

    /// <summary>An empty node to gather scenery under, keeping the tree tidy.</summary>
    public Node3D Folder(string name) => Attach(new Node3D(), name);

    /// <summary>What the realm ended up containing — for the build log.</summary>
    public string Describe() =>
        (Terrain is { } t ? $"{t.Width}x{t.Depth} terrain mesh, " : "no heightfield, ") +
        $"{SolidCount} solids, {EnemyCount} enemy markers" +
        (HasBoss ? " + boss" : " (no boss)");

    // ------------------------------------------------------------- plumbing

    private T Attach<T>(T node, string? name) where T : Node
    {
        if (name is not null)
            node.Name = name;
        Root.AddChild(node);
        return node;
    }

}

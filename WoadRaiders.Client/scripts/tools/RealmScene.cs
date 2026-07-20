using System;
using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb; // the sim's world box, not Godot's

namespace WoadRaiders.Client;

/// <summary>
/// The scene a realm design composes — a thin facade over the root Node3D that
/// knows the BAKE CONVENTIONS (BoxMesh slabs in the "ground" and "structure"
/// groups, and the marker names) so a design never has to.
///
/// Realms are BUILT, not carved: floors, ramps, stairs, walls, and roofs are
/// all SLABS — great cut stones, each a BoxMesh under an arbitrary transform.
/// "ground" slabs are what raiders walk on; "structure" slabs block and
/// occlude. The served geometry is baked FROM the finished scene afterwards,
/// and a slab scene even parses engine-free (Core.RealmSceneFile reads
/// BoxMesh sizes and transforms straight from the .tscn text).
///
/// Everything here is a convenience, not a requirement. A design may hang
/// whatever it likes off <see cref="Root"/> — any meshes, materials,
/// particles, or imported asset kits; the bake samples real triangles from
/// anything it finds in the two groups and ignores the rest. A fresh scene is
/// EMPTY — no light, no sky. A design states its whole look itself; the only
/// thing a realm MUST have is a player spawn (Core.RealmSceneFile's rule).
/// </summary>
public sealed class RealmScene
{
    private Node3D? _ground;
    private Node3D? _structure;

    // A design-time mirror of every slab, so markers can be seated on floors
    // (OnFloor) with exactly the geometry the bake will serve.
    private readonly SoupBuilder _soup = new();
    private TriangleSoup? _built;

    /// <summary>The scene root. Add anything Godot can express.</summary>
    public Node3D Root { get; } = new();

    public int SlabCount { get; private set; }
    public int EnemyCount { get; private set; }
    public bool HasPlayerSpawn { get; private set; }
    public bool HasBoss { get; private set; }

    // ------------------------------------------------------------- the stones

    // The floor/structure distinction below is the DESIGN's own bookkeeping —
    // which branch of the scene tree a slab is filed under, what it is called,
    // and whether the occlusion fader may dissolve it. The bake reads none of
    // it: what holds a raider up is decided from the geometry itself.

    /// <summary>A floor slab raiders walk on, filed under "Ground" and never faded.</summary>
    public MeshInstance3D AddFloor(Aabb box, Material material, string? name = null) =>
        AddSlab(box, material, floor: true, name);

    /// <summary>A blocking slab — wall, roof, monument — filed under "Structure".</summary>
    public MeshInstance3D AddStructure(Aabb box, Material material, string? name = null) =>
        AddSlab(box, material, floor: false, name);

    public MeshInstance3D AddSlab(Aabb box, Material material, bool floor, string? name = null) =>
        AddSlabAt(new Transform3D(Basis.Identity, box.Center.ToGodot()), box.Size.ToGodot(), material, floor, name);

    /// <summary>
    /// A slab under any transform — the primitive behind ramps and stairs.
    /// The transform lands verbatim in the saved scene, where the bake (and
    /// the engine-free parser) turn it back into world triangles.
    /// </summary>
    public MeshInstance3D AddSlabAt(Transform3D xform, Vector3 size, Material material, bool floor, string? name = null)
    {
        var parent = floor
            ? _ground ??= Attach(new Node3D(), "Ground")
            : _structure ??= Attach(new Node3D(), "Structure");
        var node = new MeshInstance3D
        {
            Name = name ?? $"{(floor ? "Floor" : "Structure")}_{SlabCount}",
            Transform = xform,
            Mesh = new BoxMesh { Size = size, Material = material },
        };
        if (floor)
            node.AddToGroup("no_fade", persistent: true); // the fader never eats the floor underfoot
        parent.AddChild(node);
        SlabCount++;

        // Mirror the slab into the design-time soup for OnFloor.
        Span<System.Numerics.Vector3> corners = stackalloc System.Numerics.Vector3[8];
        for (var k = 0; k < 8; k++)
        {
            var local = SoupBuilder.LocalCorner(k, (size * 0.5f).ToSim());
            var world = xform * new Vector3(local.X, local.Y, local.Z);
            corners[k] = new System.Numerics.Vector3(world.X, world.Y, world.Z);
        }
        _soup.AddBoxCorners(corners);
        _built = null;
        return node;
    }

    /// <summary>
    /// A pitched floor slab whose top surface runs from one point to another —
    /// a ramp. Both ends are the CENTRE of their edge; the slab hangs its
    /// thickness below the surface.
    /// </summary>
    public MeshInstance3D AddRamp(Vector3 from, Vector3 to, float width, Material material,
                                  float thickness = 12f, string? name = null)
    {
        var dir = (to - from).Normalized();
        var side = new Vector3(dir.Z, 0, -dir.X);
        if (side.LengthSquared() < 1e-6f)
            throw new ArgumentException("a ramp must run somewhere on the ground plane, not straight up");
        side = side.Normalized();
        var normal = side.Cross(dir);
        if (normal.Y < 0)
            normal = -normal;
        var centre = (from + to) * 0.5f - normal * (thickness * 0.5f);
        return AddSlabAt(new Transform3D(new Basis(side, normal, dir), centre),
                         new Vector3(width, thickness, (to - from).Length()), material, floor: true, name);
    }

    /// <summary>
    /// A stair of tread slabs from one point up (or down) to another, each
    /// tread rising less than the sim's StepHeight so feet flow up it.
    /// </summary>
    public void AddStairs(Vector3 from, Vector3 to, float width, Material material)
    {
        var run = new Vector3(to.X - from.X, 0, to.Z - from.Z);
        if (run.LengthSquared() < 1e-6f)
            throw new ArgumentException("a stair must run somewhere on the ground plane, not straight up");
        var dir = run.Normalized();
        var side = new Vector3(dir.Z, 0, -dir.X);
        var steps = Math.Max(1, Mathf.CeilToInt(Mathf.Abs(to.Y - from.Y) / (SimConstants.StepHeight - 2f)));

        for (var i = 0; i < steps; i++)
        {
            var a = from.Lerp(to, i / (float)steps);
            var b = from.Lerp(to, (i + 1) / (float)steps);
            var top = MathF.Max(a.Y, b.Y);
            var depth = MathF.Max(MathF.Abs(b.Y - a.Y) + 10f, 14f); // overlap the tread below
            var centre = new Vector3((a.X + b.X) * 0.5f, top - depth * 0.5f, (a.Z + b.Z) * 0.5f);
            AddSlabAt(new Transform3D(new Basis(side, Vector3.Up, dir), centre),
                      new Vector3(width, depth, (b - a with { Y = a.Y }).Length()), material, floor: true);
        }
    }

    /// <summary>The floor height under a point, per the slabs laid so far — how a
    /// design seats markers and scenery on what it just built. 0 with no floors.</summary>
    public float FloorAt(float x, float z)
    {
        _built ??= SlabCount > 0 ? _soup.Build() : null;
        return _built?.TopSurfaceAt(x, z) ?? 0f;
    }

    /// <summary>A point set on the floor, from plan-view (x, z) coordinates.</summary>
    public Vector3 OnFloor(float x, float z) => new(x, FloorAt(x, z), z);

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
        $"{SlabCount} slabs, {EnemyCount} enemy markers" + (HasBoss ? " + boss" : " (no boss)");

    // ------------------------------------------------------------- plumbing

    private T Attach<T>(T node, string? name) where T : Node
    {
        if (name is not null)
            node.Name = name;
        Root.AddChild(node);
        return node;
    }
}

using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The scene a realm design composes — a thin facade over the root Node3D,
/// laying geometry and placing the markers so a design need not think about
/// the .tscn it is really writing.
///
/// A realm is modelled from whatever geometry states it best: sculpted meshes,
/// instanced kit assets, primitives. The served collision is baked FROM the
/// finished scene afterwards by sampling real triangles off every mesh in it,
/// so nothing here narrows what a design may build from.
///
/// The surface states ROLES, not shapes:
///   <see cref="AddFloor{T}"/>      — ground raiders walk on;
///   <see cref="AddStructure{T}"/>  — a wall, roof, or monument that blocks;
///   <see cref="AddGeometry{T}"/>   — either, chosen by a flag.
/// Each takes any Node3D and files it so <see cref="OnFloor"/> can seat the cast
/// on what you just built. What matters is that the ROLE is stated; the mesh's
/// own triangles decide the rest. (Convenience helpers that build the common
/// primitive shapes for you live outside this class; they call these same role
/// verbs, so this class need not know they exist.)
///
/// There is almost nothing here a design MUST use. Every mesh it hangs off
/// <see cref="Root"/> by any means becomes collision, so the helpers here buy
/// convenience, not compliance: the bake reads no group and no name (see
/// Core.RealmSceneFile for the conventions in full). What they add is the
/// design's OWN bookkeeping — which branch of the tree a mesh is filed under,
/// what it is called, whether the occlusion fader may dissolve it, and which
/// surfaces <see cref="OnFloor"/> should seat markers on. A fresh scene is
/// EMPTY — no light, no sky. A design states its whole look itself; the only
/// thing a realm MUST have is a player spawn (Core.RealmSceneFile's rule).
/// </summary>
public sealed class RealmScene
{
    private Node3D? _ground;
    private Node3D? _structure;

    // A monotonic ordinal that gives every unnamed role node a unique default
    // name ("Floor_0", "Structure_1", ...). Load-bearing: those names land
    // verbatim in the saved .tscn, so the order they are handed out is part of
    // the byte-deterministic output, not a metric anyone reads.
    private int _roleOrdinal;

    // A design-time mirror of what the design laid as FLOOR, so it can seat
    // markers and scenery on it (OnFloor). Deliberately not every mesh: a
    // design knows perfectly well which surfaces it meant as ground, and the
    // roofs it lays over a crypt are up-facing surfaces too — seat a boss by
    // "the topmost surface here" and he stands on the roof of his own court.
    // This is the design's own intent, not a convention the bake reads: what
    // is served as collision is still every mesh in the finished scene.
    private readonly SoupBuilder _floors = new();
    private TriangleSoup? _built;
    private int _floorCount;

    /// <summary>The scene root. Add anything Godot can express.</summary>
    public Node3D Root { get; } = new();

    public int EnemyCount { get; private set; }
    public bool HasPlayerSpawn { get; private set; }
    public bool HasBoss { get; private set; }

    // ----------------------------------------------------------- the geometry

    /// <summary>
    /// Declare a node — and everything hung beneath it — PASSABLE: present to
    /// the eye, absent to the body. The bake takes every other mesh in the
    /// scene, so this is the only thing a design may say about geometry that
    /// the pipeline will not work out for itself, and the only tag it reads.
    ///
    /// Use it where physics and FICTION disagree: a banner across a doorway, a
    /// cobweb, a curtain, mist. Those are thin sheets of triangles no different
    /// in shape from a wall panel, so nothing measurable can tell them apart —
    /// only the author knows a raider should walk through. What it is NOT for
    /// is quieting a route ValidateRealm complained about. That complaint is
    /// nearly always the level design talking, and excusing the prop hides it:
    /// a blocked route the validator can prove, but geometry that was never
    /// there it can never miss. The bake prints what this drops on every run,
    /// so a realm quietly excusing more and more of itself is visible.
    ///
    /// Returns the node, so a placement can be wrapped where it is made.
    /// </summary>
    public T DeclarePassable<T>(T node) where T : Node
    {
        node.AddToGroup(RealmSceneFile.NoCollideGroup, persistent: true);
        return node;
    }

    // The floor/structure distinction below is the DESIGN's own bookkeeping —
    // which branch of the scene tree a mesh is filed under, what it is called,
    // and whether the occlusion fader may dissolve it. The bake reads none of
    // it: what holds a raider up is decided from the geometry itself.

    /// <summary>A floor raiders walk on — a sculpted terrace, a carved stair, an
    /// instanced kit piece — filed under "Ground", never faded, and its real
    /// surface is what <see cref="OnFloor"/> seats on.</summary>
    public T AddFloor<T>(T node, string? name = null) where T : Node3D =>
        AddGeometry(node, floor: true, name);

    /// <summary>A blocking structure — a wall, an arch, a sculpted cliff, a kit
    /// sarcophagus meant to block — filed under "Structure".</summary>
    public T AddStructure<T>(T node, string? name = null) where T : Node3D =>
        AddGeometry(node, floor: false, name);

    /// <summary>
    /// Geometry the design contributes as first-class realm structure — a
    /// sculpted cliff, an arch, an instanced kit piece, a primitive. Filed under
    /// Ground or Structure per <paramref name="floor"/>, and given a default
    /// name (Floor_N / Structure_N) when the design does not supply one.
    ///
    /// This grants no collision: every mesh under <see cref="Root"/> is already
    /// collision, however it got there. What it adds is the design's own
    /// bookkeeping, and one thing that genuinely matters — saying
    /// <paramref name="floor"/> puts the mesh's REAL triangles into what
    /// <see cref="OnFloor"/> seats on, sampled by the same code and the same
    /// winding rule the bake will use. Without that a design whose ground is
    /// modelled gets 0 back from <see cref="FloorAt"/> and seats its whole cast
    /// at the origin's height.
    ///
    /// Prefer this (or <see cref="AddFloor{T}"/> / <see cref="AddStructure{T}"/>)
    /// over <see cref="Add{T}"/> for anything that is part of the built realm;
    /// <see cref="Add{T}"/> states no role and does none of this. A piece
    /// wrapped in <see cref="DeclarePassable{T}"/> contributes nothing here
    /// either — nobody stands on a banner. It reads like this:
    /// <code>
    /// var cliff = new MeshInstance3D { Mesh = carvedTerrace, Material = ... };
    /// scene.AddFloor(cliff, "WestTerrace");     // or AddGeometry(cliff, floor: true)
    /// scene.AddStructure(archKit.Instantiate&lt;Node3D&gt;(), "GateArch");
    /// // seats on the sculpted surface, at the level named by the Y
    /// scene.AddEnemy(EnemyType.Minion, scene.OnFloor(new Vector3(1200, terraceY, 800)));
    /// </code>
    /// </summary>
    public T AddGeometry<T>(T node, bool floor, string? name = null) where T : Node3D
    {
        // Every role node gets a unique default name unless the design named it;
        // the ordinal advances on all of them, named or not, so the names fall
        // in a stable, byte-deterministic order in the saved .tscn.
        node.Name = name ?? $"{(floor ? "Floor" : "Structure")}_{_roleOrdinal}";
        _roleOrdinal++;
        if (floor)
            node.AddToGroup("no_fade", persistent: true); // the fader never eats the floor underfoot
        RoleFolder(floor).AddChild(node);

        if (!floor)
            return node;

        // The Ground/Structure folders sit at identity under an identity Root,
        // so the node's own transform is already world — what _floors wants.
        var triangles = new List<float>();
        MeshTriangles.Collect(node, Transform3D.Identity, triangles);
        if (triangles.Count > 0)
        {
            _floors.AddTriangles(triangles.ToArray(), MeshTriangles.SequentialIndices(triangles.Count / 9));
            _floorCount++;
            _built = null;
        }
        return node;
    }

    /// <summary>
    /// The floor under a point, resolved against the height the caller means —
    /// how a design seats markers and scenery on what it just built.
    ///
    /// A height is REQUIRED, and that is the whole point. A realm may stack
    /// walkable levels (a bridge deck over a chasm), so "the floor at this XZ"
    /// has no single answer, and the convenient one — the topmost — is silently
    /// wrong underneath anything: ask for a spot in the pit without saying how
    /// high you meant and you are handed the bridge. Where a design has no
    /// vantage of its own, it still has a level in mind (the terrace it is
    /// dressing, the chamber's own floor) and says so.
    ///
    /// Answers with the highest floor at or below <paramref name="nearY"/>
    /// (within a step, so a surface you meant to stand exactly on still
    /// counts), or the lowest floor here when the point lies beneath them all.
    /// 0 before any floor exists. Same rule the simulation itself uses to
    /// decide what is underfoot (TriangleSoup.GroundBelow), so a design seats
    /// its cast where the server will agree they stand.
    /// </summary>
    public float FloorAt(float x, float z, float nearY)
    {
        _built ??= _floorCount > 0 ? _floors.Build() : null;
        return _built?.GroundBelow(x, z, nearY, SimConstants.StepHeight) ?? 0f;
    }

    /// <summary>A point dropped onto the floor at or below <paramref name="near"/>
    /// — <paramref name="near"/>'s own X and Z, with Y resolved by
    /// <see cref="FloorAt"/>.</summary>
    public Vector3 OnFloor(Vector3 near) => new(near.X, FloorAt(near.X, near.Z, near.Y), near.Z);

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

    /// <summary>Hang anything else off the root — a light, a sky, a sound, a
    /// particle system.
    ///
    /// This states no ROLE, so it does none of the design's bookkeeping. It
    /// does NOT exempt anything from the simulation: a mesh added this way is
    /// collision like every other mesh in the scene, because the bake takes the
    /// whole tree (Core.RealmSceneFile's rule). Only <see cref="DeclarePassable{T}"/>
    /// excuses geometry, and only <see cref="AddGeometry{T}"/> tells
    /// <see cref="OnFloor"/> about it — so for anything the realm is BUILT
    /// from, reach for those instead.</summary>
    public T Add<T>(T node, string? name = null) where T : Node => Attach(node, name);

    /// <summary>An empty node to gather children under, keeping the tree tidy.
    /// A folder confers nothing by itself — meshes beneath it are collision
    /// like any other. Dressing a raider walks through is a folder DECLARED
    /// so: <c>DeclarePassable(Folder("Relics"))</c> excuses the whole subtree at
    /// once.</summary>
    public Node3D Folder(string name) => Attach(new Node3D(), name);

    /// <summary>What the realm ended up containing — for the build log. Counted
    /// off the tree rather than hand-tallied: the Ground and Structure branches
    /// hold every floor and wall the design placed.</summary>
    public string Describe()
    {
        var pieces = (_ground?.GetChildCount() ?? 0) + (_structure?.GetChildCount() ?? 0);
        return $"{pieces} geometry pieces, {EnemyCount} enemy markers" +
               (HasBoss ? " + boss" : " (no boss)");
    }

    // ------------------------------------------------------------- plumbing

    private T Attach<T>(T node, string? name) where T : Node
    {
        if (name is not null)
            node.Name = name;
        Root.AddChild(node);
        return node;
    }

    /// <summary>The Ground or Structure branch, created on first use — where
    /// every role verb files what it is handed.</summary>
    private Node3D RoleFolder(bool floor) => floor
        ? _ground ??= Attach(new Node3D(), "Ground")
        : _structure ??= Attach(new Node3D(), "Structure");
}

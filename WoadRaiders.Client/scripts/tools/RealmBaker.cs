using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using FileAccess = Godot.FileAccess;
using SysVec3 = System.Numerics.Vector3;

namespace WoadRaiders.Client;

/// <summary>
/// Bakes a realm scene into the server geometry JSON it is played from —
/// how any .tscn (hand-made or generated) becomes a hostable map. Terrain
/// meshes in the "terrain" group are sampled from above (the one step that
/// needs the engine — extracting triangles); everything else (the sampling
/// math, the scene parsing, validation) is engine-free Core code, unit-tested
/// there. The realm generator also runs this over its own scene as a
/// round-trip proof that scene and JSON agree.
///
/// Driven headless by tools/bake_realm.gd (Godot cannot run a C# script from
/// the command line, so a two-line GDScript shim instantiates this class).
/// </summary>
public partial class RealmBaker : RefCounted
{
    private const float DefaultCellSize = 40f;

    /// <summary>Run the bake: args = [scene res:// path, output json path]. Returns an exit code.</summary>
    public int Run(string[] args)
    {
        if (args.Length < 2)
        {
            GD.PrintErr("usage: -s res://tools/bake_realm.gd -- <scene.tscn> <out.json>");
            return 2;
        }
        var scenePath = args[0];
        var outPath = args[1];

        var text = FileAccess.GetFileAsString(scenePath);
        if (string.IsNullOrEmpty(text))
        {
            GD.PrintErr($"could not read scene text: {scenePath}");
            return 1;
        }

        // The engine-only step: instantiate the scene and collect the world-space
        // triangles of every mesh the author grouped as "terrain".
        if (GD.Load<PackedScene>(scenePath) is not { } packed)
        {
            GD.PrintErr($"could not load scene: {scenePath}");
            return 1;
        }
        var root = packed.Instantiate();
        var triangles = new List<SysVec3>();
        var footprints = new List<MeshBox>();
        CollectTerrainTriangles(root, root.Name, Transform3D.Identity, triangles, footprints);
        WarnAboutUngroupedGround(footprints);
        var cellSize = root.HasMeta("terrain_cell_size")
            ? (float)root.GetMeta("terrain_cell_size").AsDouble()
            : DefaultCellSize;
        root.Free();

        HeightField? sampled = null;
        if (triangles.Count > 0)
        {
            sampled = TerrainSampler.Sample(triangles, cellSize, out var uncovered);
            GD.Print($"sampled {triangles.Count / 3} terrain triangles onto a " +
                     $"{sampled.Width}x{sampled.Depth} grid @ {cellSize}" +
                     (uncovered > 0
                         ? $" — {uncovered} uncovered cells became a deep pit; " +
                           "cover your ground or expect ValidateRealm complaints"
                         : ""));
        }

        // Everything else is the shared engine-free pipeline.
        var geometry = DungeonSceneFile.Parse(text, scenePath, sampled);
        var json = DungeonGeometryFile.ToJson(geometry);

        using var file = FileAccess.Open(outPath, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PrintErr($"could not open for writing: {outPath}");
            return 1;
        }
        file.StoreString(json);

        GD.Print($"baked {geometry.Solids.Count} solids, {geometry.EnemySpawns.Count} enemy spawns" +
                 $"{(geometry.BossSpawn is not null ? " + boss" : "")}, " +
                 $"{(geometry.Terrain is { } t ? $"{t.Width}x{t.Depth} terrain" : "no terrain")} " +
                 $"-> {outPath}");
        return 0;
    }

    /// <summary>One mesh's plan-view (XZ) extent, and whether the author declared
    /// it ground. Used only by <see cref="WarnAboutUngroupedGround"/>.</summary>
    private readonly record struct MeshBox(string Path, bool IsTerrain, Vector2 Min, Vector2 Max);

    private static void CollectTerrainTriangles(Node node, string path, Transform3D parentXf,
                                                List<SysVec3> triangles, List<MeshBox> footprints)
    {
        var xf = node is Node3D spatial ? parentXf * spatial.Transform : parentXf;

        if (node is MeshInstance3D { Mesh: { } mesh } instance)
        {
            var isTerrain = instance.IsInGroup("terrain");
            if (isTerrain)
            {
                foreach (var vertex in mesh.GetFaces())
                {
                    var world = xf * vertex;
                    triangles.Add(new SysVec3(world.X, world.Y, world.Z));
                }
            }

            // Plan-view extent from the mesh's own AABB — its eight corners through
            // the world transform, so rotation is respected without walking faces.
            var local = mesh.GetAabb();
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var c = 0; c < 8; c++)
            {
                var corner = xf * local.GetEndpoint(c);
                min = new Vector2(Mathf.Min(min.X, corner.X), Mathf.Min(min.Y, corner.Z));
                max = new Vector2(Mathf.Max(max.X, corner.X), Mathf.Max(max.Y, corner.Z));
            }
            footprints.Add(new MeshBox(path, isTerrain, min, max));
        }

        foreach (var child in node.GetChildren())
            CollectTerrainTriangles(child, $"{path}/{child.Name}", triangles: triangles, footprints: footprints, parentXf: xf);
    }

    /// <summary>How much of the realm's plan view a mesh must cover, on BOTH axes,
    /// before an ungrouped one looks like forgotten ground rather than scenery.
    /// The Crag's largest non-terrain mesh (a rampart wall) is 13% x 1%, and
    /// terrain split into 4x4 chunks would be 25% each — so this sits between.</summary>
    private const float GroundSuspicionFraction = 0.2f;

    /// <summary>Catch the realistic authoring slip: a ground mesh that never made
    /// it into the "terrain" group. Nothing can INFER which meshes are walkable —
    /// a hill and a boulder are the same triangles, and only the author knows
    /// which one raiders stand on — but a mesh spanning much of the realm in plan
    /// view is worth a second look. A warning, never an error: a wide decorative
    /// plane (water, a plaza) is perfectly legitimate.</summary>
    private static void WarnAboutUngroupedGround(List<MeshBox> footprints)
    {
        if (footprints.Count == 0)
            return;

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var box in footprints)
        {
            min = new Vector2(Mathf.Min(min.X, box.Min.X), Mathf.Min(min.Y, box.Min.Y));
            max = new Vector2(Mathf.Max(max.X, box.Max.X), Mathf.Max(max.Y, box.Max.Y));
        }
        var span = max - min;
        if (span.X <= 0f || span.Y <= 0f)
            return;

        foreach (var box in footprints)
        {
            if (box.IsTerrain)
                continue;
            var coverX = (box.Max.X - box.Min.X) / span.X;
            var coverZ = (box.Max.Y - box.Min.Y) / span.Y;
            if (coverX < GroundSuspicionFraction || coverZ < GroundSuspicionFraction)
                continue;
            GD.PrintErr($"warning: '{box.Path}' spans {coverX:P0} x {coverZ:P0} of the realm in plan view " +
                        "but is NOT in the \"terrain\" group, so the bake will not sample it. If raiders are " +
                        "meant to walk on it, add it to \"terrain\" (and to \"no_fade\"); if it is scenery, ignore this.");
        }
    }
}

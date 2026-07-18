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
        CollectTerrainTriangles(root, Transform3D.Identity, triangles);
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
                 $"{(geometry.Terrain is { } t ? $"{t.Width}x{t.Depth} terrain" : "no terrain")}, " +
                 $"{geometry.Props.Count} props -> {outPath}");
        return 0;
    }

    private static void CollectTerrainTriangles(Node node, Transform3D parentXf, List<SysVec3> triangles)
    {
        var xf = node is Node3D spatial ? parentXf * spatial.Transform : parentXf;

        if (node is MeshInstance3D { Mesh: { } mesh } instance && instance.IsInGroup("terrain"))
        {
            foreach (var vertex in mesh.GetFaces())
            {
                var world = xf * vertex;
                triangles.Add(new SysVec3(world.X, world.Y, world.Z));
            }
        }

        foreach (var child in node.GetChildren())
            CollectTerrainTriangles(child, xf, triangles);
    }
}

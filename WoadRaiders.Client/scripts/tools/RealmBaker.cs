using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using FileAccess = Godot.FileAccess;

namespace WoadRaiders.Client;

/// <summary>
/// Bakes a realm scene into the server geometry JSON it is played from — how
/// any .tscn (hand-made or generated) becomes a hostable map. Meshes in the
/// "ground" and "structure" groups yield their world-space triangles (the one
/// step that needs the engine); everything else (the scene parsing, the JSON,
/// validation) is engine-free Core code, unit-tested there. The realm
/// generator also runs this over its own scene as a round-trip proof that
/// scene and JSON agree.
///
/// Driven headless by tools/bake_realm.gd (Godot cannot run a C# script from
/// the command line, so a two-line GDScript shim instantiates this class).
/// </summary>
public partial class RealmBaker : RefCounted
{
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
        // triangles of every mesh in the "ground" and "structure" groups.
        if (GD.Load<PackedScene>(scenePath) is not { } packed)
        {
            GD.PrintErr($"could not load scene: {scenePath}");
            return 1;
        }
        var root = packed.Instantiate();
        var builder = new SoupBuilder();
        var floors = new List<float>();
        var structures = new List<float>();
        CollectGroupTriangles(root, Transform3D.Identity, floors, structures);
        root.Free();

        TriangleSoup? soup = null;
        if (floors.Count + structures.Count > 0)
        {
            builder.AddTriangles(floors.ToArray(), SequentialIndices(floors.Count / 9), floor: true);
            builder.AddTriangles(structures.ToArray(), SequentialIndices(structures.Count / 9), floor: false);
            soup = builder.Build();
            GD.Print($"sampled {soup.Triangles.Length / 3} triangles " +
                     $"({soup.FloorTriangleCount} floor, {soup.Triangles.Length / 3 - soup.FloorTriangleCount} structure)");
        }

        // Everything else is the shared engine-free pipeline.
        var geometry = DungeonSceneFile.Parse(text, scenePath, soup);
        var json = DungeonGeometryFile.ToJson(geometry);

        using var file = FileAccess.Open(outPath, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PrintErr($"could not open for writing: {outPath}");
            return 1;
        }
        file.StoreString(json);

        GD.Print($"baked {geometry.EnemySpawns.Count} enemy spawns" +
                 $"{(geometry.BossSpawn is not null ? " + boss" : "")}, " +
                 $"{(geometry.Soup is { } s ? $"{s.Triangles.Length / 3} triangles" : "no geometry (flat map)")} " +
                 $"-> {outPath}");
        return 0;
    }

    private static void CollectGroupTriangles(Node node, Transform3D parentXf,
                                              List<float> floors, List<float> structures)
    {
        var xf = node is Node3D spatial ? parentXf * spatial.Transform : parentXf;

        if (node is MeshInstance3D { Mesh: { } mesh } instance)
        {
            var target = instance.IsInGroup("ground") ? floors
                : instance.IsInGroup("structure") ? structures
                : null;
            if (target is not null)
            {
                // Godot winds front faces CLOCKWISE; the soup (and Recast's
                // slope filter) want counter-clockwise, or every floor's top
                // face reads as unwalkable ceiling. Swap two corners per tri.
                var faces = mesh.GetFaces();
                for (var i = 0; i + 2 < faces.Length; i += 3)
                {
                    foreach (var vertex in new[] { faces[i], faces[i + 2], faces[i + 1] })
                    {
                        var world = xf * vertex;
                        target.Add(world.X);
                        target.Add(world.Y);
                        target.Add(world.Z);
                    }
                }
            }
        }

        foreach (var child in node.GetChildren())
            CollectGroupTriangles(child, xf, floors, structures);
    }

    private static int[] SequentialIndices(int triangles)
    {
        var indices = new int[triangles * 3];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;
        return indices;
    }
}

using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using FileAccess = Godot.FileAccess;

namespace WoadRaiders.Client;

/// <summary>
/// Bakes a realm scene into the server geometry JSON it is played from — how
/// any .tscn (hand-made or generated) becomes a hostable map. Every mesh the
/// realm is MODELLED from yields its world-space triangles — no groups, no
/// naming, no privileged mesh type; instanced sub-scenes are dressing and are
/// skipped whole (see <see cref="CollectTriangles"/>, and
/// <c>Core.RealmSceneFile</c> for the conventions in full). Sampling those
/// triangles is the one step that needs the engine; everything else (the
/// scene parsing, the JSON, validation) is engine-free Core code, unit-tested
/// there. The realm generator also runs this over its own scene as a
/// round-trip proof that scene and JSON agree.
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

        // The engine-only step: instantiate the scene and collect the
        // world-space triangles of every mesh the realm is modelled from.
        if (GD.Load<PackedScene>(scenePath) is not { } packed)
        {
            GD.PrintErr($"could not load scene: {scenePath}");
            return 1;
        }
        var root = packed.Instantiate();
        var builder = new SoupBuilder();
        var triangles = new List<float>();
        CollectTriangles(root, Transform3D.Identity, triangles);
        root.Free();

        TriangleSoup? soup = null;
        if (triangles.Count > 0)
        {
            builder.AddTriangles(triangles.ToArray(), SequentialIndices(triangles.Count / 9));
            soup = builder.Build();
            GD.Print($"sampled {soup.Triangles.Length / 3} triangles");
        }

        // Everything else is the shared engine-free pipeline.
        var definition = RealmSceneFile.Parse(text, scenePath, soup);
        var json = RealmDefinitionFile.ToJson(definition);

        using var file = FileAccess.Open(outPath, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PrintErr($"could not open for writing: {outPath}");
            return 1;
        }
        file.StoreString(json);

        GD.Print($"baked {definition.EnemySpawns.Count} enemy spawns" +
                 $"{(definition.BossSpawn is not null ? " + boss" : "")}, " +
                 $"{(definition.Soup is { } s ? $"{s.Triangles.Length / 3} triangles" : "no geometry (flat map)")} " +
                 $"-> {outPath}");
        return 0;
    }

    /// <summary>
    /// Every mesh the realm itself is BUILT from, in world space — whatever
    /// the mesh is, wherever it sits, in no group and under no naming rule.
    /// What holds a raider up and what blocks them are decided afterwards
    /// from the geometry: by each triangle's normal, and by whether a surface
    /// survives Recast's voxels and agent-radius erosion.
    ///
    /// Instanced sub-scenes — a kit sarcophagus, a brazier, a bone pile —
    /// are dressing, and are skipped whole. That line is drawn where authors
    /// already draw it (you MODEL the architecture and you DROP IN the props)
    /// rather than by anything they must remember to tag.
    ///
    /// It is provisional, and closer to lifting than it looks. Sweeping the
    /// kits in costs far less than first supposed: the navmesh barely moves
    /// (+17%, since sub-agent detail cannot survive erosion), and once the
    /// payload is welded and compressed the join goes to ~0.5 MB rather than
    /// the 6.4 MB raw. What still stops it is ONE validation failure, and it
    /// is not the props' doing — the far corner of the Crypt's chasm floor
    /// has no way back out to the stair, and the clean bake only passes
    /// because nothing can reach that corner to discover it. Props open a way
    /// in and the pre-existing dead end surfaces. Fix the chasm and this skip
    /// can go.
    /// </summary>
    private static void CollectTriangles(Node node, Transform3D parentXf, List<float> triangles, bool isSceneRoot = true)
    {
        var xf = node is Node3D spatial ? parentXf * spatial.Transform : parentXf;
        if (!isSceneRoot && !string.IsNullOrEmpty(node.SceneFilePath))
            return; // an instanced asset: dressing, not fabric

        if (node is MeshInstance3D { Mesh: { } mesh })
        {
            // Godot winds front faces CLOCKWISE; the soup (and Recast's slope
            // filter) want counter-clockwise, or every upward face reads as an
            // overhang. Swap two corners per triangle.
            var faces = mesh.GetFaces();
            for (var i = 0; i + 2 < faces.Length; i += 3)
            {
                foreach (var vertex in new[] { faces[i], faces[i + 2], faces[i + 1] })
                {
                    var world = xf * vertex;
                    triangles.Add(world.X);
                    triangles.Add(world.Y);
                    triangles.Add(world.Z);
                }
            }
        }

        foreach (var child in node.GetChildren())
            CollectTriangles(child, xf, triangles, isSceneRoot: false);
    }

    private static int[] SequentialIndices(int triangles)
    {
        var indices = new int[triangles * 3];
        for (var i = 0; i < indices.Length; i++)
            indices[i] = i;
        return indices;
    }
}

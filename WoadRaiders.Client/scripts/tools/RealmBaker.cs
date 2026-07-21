using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using FileAccess = Godot.FileAccess;

namespace WoadRaiders.Client;

/// <summary>
/// Bakes a realm scene into the server geometry JSON it is played from — how
/// any .tscn (hand-made or generated) becomes a hostable map. Every mesh the
/// realm is MODELLED from yields its world-space triangles — no groups, no
/// naming, no privileged mesh type, and no exception for instanced kit props
/// (see <see cref="CollectTriangles"/>, and
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
        var passed = new MeshTriangles.Excluded();
        MeshTriangles.Collect(root, Transform3D.Identity, triangles, passed);
        root.Free();

        TriangleSoup? soup = null;
        if (triangles.Count > 0)
        {
            builder.AddTriangles(triangles.ToArray(), MeshTriangles.SequentialIndices(triangles.Count / 9));
            soup = builder.Build();
            GD.Print($"sampled {soup.Triangles.Length / 3} triangles");
        }
        // Always say what was waved through, even when it is nothing. A realm
        // excusing more and more of itself from collision is the failure this
        // convention invites, and it is invisible unless the bake counts aloud.
        GD.Print(passed.Meshes == 0
            ? $"no_collide: nothing excluded"
            : $"no_collide: {passed.Meshes} mesh(es) excluded, {passed.Triangles} triangles waved through");

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

    // The sampling itself lives in MeshTriangles, shared with RealmScene so a
    // design measures its own modelled ground with the same code (and the same
    // winding rule) that the bake will measure it with.
    //
    // EVERYTHING is taken — the kit sarcophagus and the brazier and the bone
    // pile along with the walls, because a sarcophagus you cannot walk
    // through is the honest answer and an author should not have to say so.
    // The costs turned out to be smaller than they looked: the navmesh
    // barely moves (+17%, since sub-agent detail cannot survive radius
    // erosion), and welding plus compression put a 131k-triangle Crypt on
    // the join wire at ~670 KB. The one real obstacle was never the props —
    // it was a dead corner in the Crypt's chasm that only became reachable
    // once they were included, and that is fixed in the realm, where it
    // belonged.
}

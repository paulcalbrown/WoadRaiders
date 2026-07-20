using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Builds a realm's Godot scene from its DESIGN (any <see cref="IRealmDesign"/>
/// in <see cref="RealmDesigns"/>) and saves it with <see cref="ResourceSaver"/> —
/// Godot's own serializer, the same code path a hand-author's Ctrl+S runs — so
/// the emitted .tscn is exactly what a naturally-authored scene looks like:
/// built-in nodes and resources only, no scripts, no metadata.
///
/// Nothing here knows any particular realm. The design builds and returns its
/// own <see cref="RealmScene"/>, stating everything about it; this class only
/// resolves which design to run, names the root after it, checks the one rule
/// the format requires (a player spawn), and packs and saves the result.
///
/// Because the scene is built FIRST (the served geometry JSON is baked FROM it
/// afterwards, by the same tools/bake_realm.gd every hand-made realm uses), a
/// design has the WHOLE engine to work with. There is only one naming rule in
/// the whole format — the spawn markers — because everything else the
/// simulation needs is read back off the geometry itself: every mesh modelled
/// into the scene is collision, and what holds a raider up, what blocks them,
/// and what is too small to matter follow from that (Core.RealmSceneFile
/// states the conventions in full).
///
/// Driven headless by tools/build_realm_scene.gd (Godot cannot run a C# script
/// from the command line). tools/GenerateRealm.cs orchestrates the chain.
/// </summary>
public partial class RealmSceneBuilder : RefCounted
{
    /// <summary>Run the build: args = [output res:// tscn path, realm name
    /// (defaults to the file's base name)]. Returns an exit code.</summary>
    public int Run(string[] args)
    {
        if (args.Length < 1)
        {
            GD.PrintErr("usage: -s res://tools/build_realm_scene.gd -- <out.tscn> [realm]\n" +
                        $"known realms: {string.Join(", ", RealmDesigns.Names)}");
            return 2;
        }

        var outPath = args[0];
        var name = args.Length > 1 ? args[1] : System.IO.Path.GetFileNameWithoutExtension(outPath);
        if (RealmDesigns.Find(name) is not { } design)
        {
            GD.PrintErr($"no realm design named '{name}' — known realms: {string.Join(", ", RealmDesigns.Names)}");
            return 2;
        }

        var scene = design.Build();
        scene.Root.Name = design.Name; // the registry name is the scene's name, always

        if (!scene.HasPlayerSpawn)
        {
            GD.PrintErr($"the '{design.Name}' design placed no player spawn — call scene.SetPlayerSpawn(...); " +
                        "every realm needs one (Core.RealmSceneFile requires it)");
            return 1;
        }

        // Pack and save: nodes only serialize when owned by the root.
        SetOwnerRecursive(scene.Root, scene.Root);
        var packed = new PackedScene();
        var packError = packed.Pack(scene.Root);
        if (packError != Error.Ok)
        {
            GD.PrintErr($"could not pack the scene: {packError}");
            return 1;
        }
        var saveError = ResourceSaver.Save(packed, outPath);
        if (saveError != Error.Ok)
        {
            GD.PrintErr($"could not save {outPath}: {saveError}");
            return 1;
        }

        GD.Print($"built {outPath}: {scene.Describe()}, saved by Godot's own serializer.");
        return 0;
    }

    private static void SetOwnerRecursive(Node node, Node root)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = root;
            // An instanced scene (a .glb kit piece, a reusable prop scene) is
            // one node from this scene's point of view: owning its INTERNALS
            // would inline the whole imported tree into the saved .tscn.
            // Leaving them alone keeps the instance an ExtResource reference —
            // exactly what hand-instancing in the editor produces.
            if (string.IsNullOrEmpty(child.SceneFilePath))
                SetOwnerRecursive(child, root);
        }
    }
}

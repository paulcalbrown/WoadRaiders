using System.Linq;
using Godot;
using WoadRaiders.Core;
using FileAccess = Godot.FileAccess;

namespace WoadRaiders.Client;

/// <summary>
/// Builds a realm's Godot scene from its geometry JSON and saves it with
/// <see cref="ResourceSaver"/> — Godot's own serializer, the same code path a
/// hand-author's Ctrl+S runs — so the emitted .tscn is EXACTLY what a
/// naturally-authored scene looks like: built-in nodes and resources only, a
/// real displaced terrain ArrayMesh (in the "terrain" group, so the standard
/// bake tool can sample it back), stone visuals + collision, braziers, the
/// dusk light rig, and the marker conventions. No scripts, no metadata,
/// nothing a vanilla Godot editor doesn't understand.
///
/// Driven headless by tools/build_realm_scene.gd (Godot cannot run a C# script
/// from the command line). tools/GenerateRealm.cs orchestrates it and then
/// round-trip-verifies the result through the bake tool.
/// </summary>
public partial class RealmSceneBuilder : RefCounted
{
    /// <summary>Run the build: args = [geometry res:// json path, output res:// tscn path]. Returns an exit code.</summary>
    public int Run(string[] args)
    {
        if (args.Length < 2)
        {
            GD.PrintErr("usage: -s res://tools/build_realm_scene.gd -- <geometry.json> <out.tscn>");
            return 2;
        }

        var json = FileAccess.GetFileAsString(args[0]);
        if (string.IsNullOrEmpty(json))
        {
            GD.PrintErr($"could not read geometry: {args[0]}");
            return 1;
        }
        var geometry = DungeonGeometryFile.Parse(json);
        if (geometry.Terrain is not { } terrain)
        {
            GD.PrintErr("the geometry has no terrain — RealmSceneBuilder builds open realms");
            return 1;
        }

        var root = new Node3D { Name = args[1].GetFile().GetBaseName() };

        root.AddChild(new WorldEnvironment { Name = "Environment", Environment = RealmDressing.RealmEnvironment() });
        var sun = RealmDressing.MakeSun();
        sun.Name = "Sun";
        root.AddChild(sun);
        var fill = RealmDressing.MakeFill();
        fill.Name = "Fill";
        root.AddChild(fill);

        // The land: a REAL displaced mesh — what a hand-sculptor would have. The
        // "terrain" group is what the bake tool samples; "no_fade" keeps the
        // occlusion fader off the ground.
        var terrainNode = new MeshInstance3D
        {
            Name = "Terrain",
            Mesh = RealmTerrain.BuildMesh(terrain.OriginX, terrain.OriginZ, terrain.CellSize,
                                          terrain.Width, terrain.Depth, terrain.Heights.ToArray()),
            MaterialOverride = RealmTerrain.TerrainMaterial(),
        };
        terrainNode.AddToGroup("terrain", persistent: true);
        terrainNode.AddToGroup("no_fade", persistent: true);
        root.AddChild(terrainNode);

        // Solids: a stone visual and a matching collision shape per box.
        var stone = RealmDressing.StoneMaterial();
        var visuals = new Node3D { Name = "SolidVisuals" };
        root.AddChild(visuals);
        var body = new StaticBody3D { Name = "Static" };
        root.AddChild(body);
        for (var i = 0; i < geometry.Solids.Count; i++)
        {
            var solid = geometry.Solids[i];
            var center = solid.Center.ToGodot();
            var size = solid.Size.ToGodot();
            visuals.AddChild(new MeshInstance3D
            {
                Name = $"Solid_{i}",
                Position = center,
                Mesh = new BoxMesh { Size = size, Material = stone },
            });
            body.AddChild(new CollisionShape3D
            {
                Name = $"Col_{i}",
                Position = center,
                Shape = new BoxShape3D { Size = size },
            });
        }

        // Braziers: real fire nodes, grouped so the bake tool exports them as props.
        var braziers = new Node3D { Name = "Braziers" };
        root.AddChild(braziers);
        for (var i = 0; i < geometry.Props.Count; i++)
        {
            var brazier = RealmDressing.MakeBrazier(geometry.Props[i].Position.ToGodot());
            brazier.Name = $"Brazier{i}";
            brazier.AddToGroup("brazier", persistent: true);
            braziers.AddChild(brazier);
        }

        // The cast, in the marker naming conventions the bake tool reads.
        root.AddChild(new Marker3D { Name = "PlayerSpawn", Position = geometry.SpawnPoint.ToGodot() });
        for (var i = 0; i < geometry.EnemySpawns.Count; i++)
        {
            var spawn = geometry.EnemySpawns[i];
            var suffix = spawn.Type switch { EnemyType.Rogue => "_Rogue", EnemyType.Mage => "_Mage", _ => "" };
            root.AddChild(new Marker3D { Name = $"EnemySpawn{i}{suffix}", Position = spawn.Position.ToGodot() });
        }
        if (geometry.BossSpawn is { } boss)
            root.AddChild(new Marker3D { Name = "BossSpawn", Position = boss.ToGodot() });

        // Pack and save: nodes only serialize when owned by the root.
        SetOwnerRecursive(root, root);
        var packed = new PackedScene();
        var packError = packed.Pack(root);
        if (packError != Error.Ok)
        {
            GD.PrintErr($"could not pack the scene: {packError}");
            return 1;
        }
        var saveError = ResourceSaver.Save(packed, args[1]);
        if (saveError != Error.Ok)
        {
            GD.PrintErr($"could not save {args[1]}: {saveError}");
            return 1;
        }

        GD.Print($"built {args[1]}: {terrain.Width}x{terrain.Depth} terrain mesh, {geometry.Solids.Count} solids, " +
                 $"{geometry.Props.Count} braziers, {geometry.EnemySpawns.Count} enemy markers" +
                 $"{(geometry.BossSpawn is not null ? " + boss" : "")}, saved by Godot's own serializer.");
        return 0;
    }

    private static void SetOwnerRecursive(Node node, Node root)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = root;
            SetOwnerRecursive(child, root);
        }
    }
}

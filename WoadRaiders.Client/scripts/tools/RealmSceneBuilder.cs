using System.Linq;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// Builds the generated realm's Godot scene from its DESIGN
/// (<see cref="CragDesign"/>) and saves it with <see cref="ResourceSaver"/> —
/// Godot's own serializer, the same code path a hand-author's Ctrl+S runs — so
/// the emitted .tscn is exactly what a naturally-authored scene looks like:
/// built-in nodes and resources only, no scripts, no metadata.
///
/// Because the scene is built FIRST (the served geometry JSON is baked FROM it
/// afterwards, by the same tools/bake_realm.gd every hand-made realm uses),
/// the design has the WHOLE engine to work with: any meshes, materials,
/// particles, or imported asset kits. Only the pieces the simulation cares
/// about follow the bake conventions — terrain meshes in the "terrain" group,
/// BoxShape3D collision, the marker names, the "brazier" group — and
/// everything else (the boulder scatter today; whatever dressing tomorrow) is
/// pure scenery the bake never needs to understand.
///
/// Driven headless by tools/build_realm_scene.gd (Godot cannot run a C# script
/// from the command line). tools/GenerateRealm.cs orchestrates the chain.
/// </summary>
public partial class RealmSceneBuilder : RefCounted
{
    /// <summary>Run the build: args = [output res:// tscn path]. Returns an exit code.</summary>
    public int Run(string[] args)
    {
        if (args.Length < 1)
        {
            GD.PrintErr("usage: -s res://tools/build_realm_scene.gd -- <out.tscn>");
            return 2;
        }

        var field = CragDesign.BakeField();
        var solids = CragDesign.Solids(field);

        var root = new Node3D { Name = CragDesign.Name };

        root.AddChild(new WorldEnvironment { Name = "Environment", Environment = RealmDressing.RealmEnvironment() });
        var sun = RealmDressing.MakeSun();
        sun.Name = "Sun";
        root.AddChild(sun);
        var fill = RealmDressing.MakeFill();
        fill.Name = "Fill";
        root.AddChild(fill);

        // The land: a REAL displaced mesh. The "terrain" group is what the bake
        // tool samples; "no_fade" keeps the occlusion fader off the ground.
        var terrainNode = new MeshInstance3D
        {
            Name = "Terrain",
            Mesh = RealmTerrain.BuildMesh(field.OriginX, field.OriginZ, field.CellSize,
                                          field.Width, field.Depth, field.Heights.ToArray()),
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
        for (var i = 0; i < solids.Count; i++)
        {
            var solid = solids[i];
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
        for (var i = 0; i < CragDesign.BrazierSpots.Length; i++)
        {
            var (x, z) = CragDesign.BrazierSpots[i];
            var brazier = RealmDressing.MakeBrazier(new Vector3(x, field.Sample(x, z), z));
            brazier.Name = $"Brazier{i}";
            brazier.AddToGroup("brazier", persistent: true);
            braziers.AddChild(brazier);
        }

        // The cast, in the marker naming conventions the bake tool reads.
        Vector3 OnGround((float X, float Z) at) => new(at.X, field.Sample(at.X, at.Z), at.Z);
        root.AddChild(new Marker3D { Name = "PlayerSpawn", Position = OnGround(CragDesign.PlayerSpawn) });
        for (var i = 0; i < CragDesign.Enemies.Length; i++)
        {
            var e = CragDesign.Enemies[i];
            var suffix = e.Type switch { EnemyType.Rogue => "_Rogue", EnemyType.Mage => "_Mage", _ => "" };
            root.AddChild(new Marker3D { Name = $"EnemySpawn{i}{suffix}", Position = OnGround((e.X, e.Z)) });
        }
        root.AddChild(new Marker3D { Name = "BossSpawn", Position = OnGround(CragDesign.BossSpawn) });

        // ---- pure scenery, past here: nothing the simulation ever sees. ----
        DressWithBoulders(root);

        // Pack and save: nodes only serialize when owned by the root.
        SetOwnerRecursive(root, root);
        var packed = new PackedScene();
        var packError = packed.Pack(root);
        if (packError != Error.Ok)
        {
            GD.PrintErr($"could not pack the scene: {packError}");
            return 1;
        }
        var saveError = ResourceSaver.Save(packed, args[0]);
        if (saveError != Error.Ok)
        {
            GD.PrintErr($"could not save {args[0]}: {saveError}");
            return 1;
        }

        GD.Print($"built {args[0]}: {field.Width}x{field.Depth} terrain mesh, {solids.Count} solids, " +
                 $"{CragDesign.BrazierSpots.Length} braziers, {CragDesign.Enemies.Length} enemy markers + boss, " +
                 $"and the scenery, saved by Godot's own serializer.");
        return 0;
    }

    /// <summary>Scatter weathered boulders over the crag faces — cosmetic proof
    /// that the scene carries whatever the design wants: these exist nowhere in
    /// the served geometry (not solids, not props, not terrain), they are simply
    /// part of the realm's look.</summary>
    private static void DressWithBoulders(Node3D root)
    {
        var rocks = new Node3D { Name = "Boulders" };
        root.AddChild(rocks);

        // Three rock variants: shared low-detail spheres in differing greys,
        // squashed and tilted per instance so no two read alike.
        var variants = new Mesh[]
        {
            new SphereMesh { Radius = 1f, Height = 1.6f, RadialSegments = 10, Rings = 6,
                             Material = Rock(new Color(0.32f, 0.31f, 0.34f)) },
            new SphereMesh { Radius = 1f, Height = 1.4f, RadialSegments = 8, Rings = 5,
                             Material = Rock(new Color(0.27f, 0.27f, 0.30f)) },
            new SphereMesh { Radius = 1f, Height = 1.8f, RadialSegments = 9, Rings = 6,
                             Material = Rock(new Color(0.36f, 0.34f, 0.33f)) },
        };

        var count = 0;
        foreach (var (position, size, yaw, variant) in CragDesign.Boulders())
        {
            var rock = new MeshInstance3D
            {
                Name = $"Rock_{count++}",
                Mesh = variants[variant],
                Position = position.ToGodot(),
                Rotation = new Vector3(0f, yaw, (variant - 1) * 0.16f), // a lean, so they sit into the slope
                Scale = new Vector3(size, size * 0.62f, size * 0.84f),
            };
            rock.AddToGroup("no_fade", persistent: true); // scenery on the slopes must never dissolve
            rocks.AddChild(rock);
        }
        GD.Print($"scattered {count} boulders over the crag faces.");
    }

    private static StandardMaterial3D Rock(Color color) => new() { AlbedoColor = color, Roughness = 1f };

    private static void SetOwnerRecursive(Node node, Node root)
    {
        foreach (var child in node.GetChildren())
        {
            child.Owner = root;
            SetOwnerRecursive(child, root);
        }
    }
}

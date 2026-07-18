using System;
using System.IO;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The scene-to-geometry pipeline, engine-free: Godot .tscn text goes in,
/// simulation geometry comes out — markers, box collision (with composed and
/// rotated transforms), brazier props, and the RealmTerrain heightfield. This
/// is what the server runs when it loads a realm scene, tested directly.
/// </summary>
public class DungeonSceneFileTests
{
    private const string Realm = """
        [gd_scene load_steps=3 format=3]

        [ext_resource type="Script" path="res://scripts/world/RealmTerrain.cs" id="1_terrain"]

        [sub_resource type="BoxShape3D" id="wall"]
        size = Vector3(100, 60, 20)

        [sub_resource type="BoxShape3D" id="unit"]

        [node name="Realm" type="Node3D"]

        [node name="Terrain" type="Node3D" parent="." groups=["no_fade", "realm_terrain"]]
        script = ExtResource("1_terrain")
        CellSize = 50.0
        TerrainWidth = 3
        TerrainDepth = 2
        Heights = PackedFloat32Array(1, 2, 3, 4, 5, 6)

        [node name="Static" type="StaticBody3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 20)

        [node name="Wall" type="CollisionShape3D" parent="Static"]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 30, 0)
        shape = SubResource("wall")

        [node name="Turned" type="CollisionShape3D" parent="Static"]
        transform = Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, 0, 0, 0)
        shape = SubResource("wall")

        [node name="Tiny" type="CollisionShape3D" parent="."]
        shape = SubResource("unit")

        [node name="PlayerSpawn" type="Marker3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 1, 200)

        [node name="EnemySpawn0_Rogue" type="Marker3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 30, 0, 40)

        [node name="EnemySpawn1_Mage" type="Marker3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 50, 0, 60)

        [node name="EnemySpawn2" type="Marker3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 70, 0, 80)

        [node name="BossSpawn" type="Marker3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 400, 9, 500)

        [node name="Braziers" type="Node3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1, 0, 2)

        [node name="Brazier0" type="Node3D" parent="Braziers" groups=["brazier"]]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 6, 8, 7)
        """;

    [Fact]
    public void A_realm_scene_parses_into_full_geometry()
    {
        var geometry = DungeonSceneFile.Parse(Realm, "res://maps/Realm.tscn");

        Assert.Equal(new Vector3(100, 1, 200), geometry.SpawnPoint);
        Assert.Equal(new Vector3(400, 9, 500), geometry.BossSpawn);
        Assert.Equal("res://maps/Realm.tscn", geometry.ScenePath);

        var terrain = Assert.IsType<HeightField>(geometry.Terrain);
        Assert.Equal(50f, terrain.CellSize);
        Assert.Equal(3, terrain.Width);
        Assert.Equal(2, terrain.Depth);
        Assert.Equal(0f, terrain.OriginX); // omitted in the file → the RealmTerrain default
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f }, terrain.Heights);

        Assert.Equal(
            new[] { EnemyType.Rogue, EnemyType.Mage, EnemyType.Minion },
            geometry.EnemySpawns.Select(s => s.Type).ToArray());
        Assert.Equal(new Vector3(30, 0, 40), geometry.EnemySpawns[0].Position);

        // One brazier prop, at its transform COMPOSED with the holder's; the
        // "Braziers" folder node itself is not a prop.
        var prop = Assert.Single(geometry.Props);
        Assert.Equal(PropType.Brazier, prop.Type);
        Assert.Equal(new Vector3(7, 8, 9), prop.Position);
    }

    [Fact]
    public void Collision_boxes_compose_transforms_and_rotate_into_world_aabbs()
    {
        var solids = DungeonSceneFile.Parse(Realm).Solids;
        Assert.Equal(3, solids.Count);

        // Wall: parent origin (10,0,20) + own (5,30,0), half-extents (50,30,10).
        Assert.Contains(new Aabb(new Vector3(-35, 0, 10), new Vector3(65, 60, 30)), solids);

        // Turned: the same shape yawed 90° under the parent — footprint swaps.
        Assert.Contains(new Aabb(new Vector3(0, -30, -30), new Vector3(20, 30, 70)), solids);

        // Tiny: no transform, no size property — Godot's 1x1x1 BoxShape3D default.
        Assert.Contains(new Aabb(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, 0.5f, 0.5f)), solids);
    }

    [Fact]
    public void Metadata_terrain_on_the_root_is_the_pure_built_in_form()
    {
        // No scripts anywhere: the heightfield rides as root metadata (how the
        // generated realms carry it) and beats any other terrain source.
        var geometry = DungeonSceneFile.Parse("""
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            metadata/terrain_cell_size = 50.0
            metadata/terrain_width = 3
            metadata/terrain_depth = 2
            metadata/terrain_heights = PackedFloat32Array(1, 2, 3, 4, 5, 6)
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """);
        var terrain = Assert.IsType<HeightField>(geometry.Terrain);
        Assert.Equal(50f, terrain.CellSize);
        Assert.Equal(0f, terrain.OriginX); // omitted → the documented default
        Assert.Equal(3, terrain.Width);
        Assert.Equal(new[] { 1f, 2f, 3f, 4f, 5f, 6f }, terrain.Heights);
    }

    [Fact]
    public void Inconsistent_metadata_terrain_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => DungeonSceneFile.Parse("""
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            metadata/terrain_width = 3
            metadata/terrain_depth = 3
            metadata/terrain_heights = PackedFloat32Array(1, 2)
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """));
    }

    [Fact]
    public void RealmTerrain_is_recognized_by_script_alone()
    {
        // No groups on the node — only the script reference marks it.
        var geometry = DungeonSceneFile.Parse("""
            [gd_scene format=3]
            [ext_resource type="Script" path="res://scripts/world/RealmTerrain.cs" id="1"]
            [node name="R" type="Node3D"]
            [node name="T" type="Node3D" parent="."]
            script = ExtResource("1")
            TerrainWidth = 2
            TerrainDepth = 2
            Heights = PackedFloat32Array(0, 0, 0, 0)
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """);
        Assert.NotNull(geometry.Terrain);
        Assert.Equal(40f, geometry.Terrain!.CellSize); // the RealmTerrain default
    }

    [Fact]
    public void A_transformed_RealmTerrain_is_refused()
    {
        var text = """
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            [node name="T" type="Node3D" parent="." groups=["realm_terrain"]]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 100, 0, 0)
            TerrainWidth = 2
            TerrainDepth = 2
            Heights = PackedFloat32Array(0, 0, 0, 0)
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """;
        var e = Assert.Throws<InvalidDataException>(() => DungeonSceneFile.Parse(text));
        Assert.Contains("transformed", e.Message);
    }

    [Fact]
    public void Inconsistent_terrain_dimensions_are_refused()
    {
        var text = """
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            [node name="T" type="Node3D" parent="." groups=["realm_terrain"]]
            TerrainWidth = 3
            TerrainDepth = 3
            Heights = PackedFloat32Array(1, 2, 3)
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """;
        Assert.Throws<InvalidDataException>(() => DungeonSceneFile.Parse(text));
    }

    [Fact]
    public void Mesh_terrain_needs_the_bake_tool_or_a_sampled_field()
    {
        var text = """
            [gd_scene format=3]
            [sub_resource type="PlaneMesh" id="p"]
            [node name="R" type="Node3D"]
            [node name="Ground" type="MeshInstance3D" parent="." groups=["terrain"]]
            mesh = SubResource("p")
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """;
        var e = Assert.Throws<InvalidDataException>(() => DungeonSceneFile.Parse(text));
        Assert.Contains("bake", e.Message);

        // The bake tool samples the meshes and hands the result in — then it parses.
        var sampled = new HeightField(0, 0, 40, 2, 2, new float[4]);
        var geometry = DungeonSceneFile.Parse(text, sampledTerrain: sampled);
        Assert.Same(sampled, geometry.Terrain);
    }

    [Fact]
    public void A_scene_without_a_player_spawn_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => DungeonSceneFile.Parse("""
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            """));
        Assert.Contains("PlayerSpawn", e.Message);
    }

    [Fact]
    public void Exotic_scene_content_is_tolerated()
    {
        // Multi-line dictionaries, curves, strings with '=' — the parser must
        // shrug at everything a real authored scene carries.
        var geometry = DungeonSceneFile.Parse("""
            [gd_scene format=3]
            [sub_resource type="Animation" id="a"]
            _data = [Vector2(0, 1), 0.0, 0.0, 0, 0, Vector2(1, 0), 0.0, 0.0, 0, 0]
            [sub_resource type="AnimationLibrary" id="lib"]
            _data = {
            "flicker": SubResource("a")
            }
            [node name="R" type="Node3D"]
            [node name="Anim" type="AnimationPlayer" parent="."]
            libraries = {
            "": SubResource("lib")
            }
            autoplay = "has = signs and \"quotes\""
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 3, 0, 4)
            """);
        Assert.Equal(new Vector3(3, 0, 4), geometry.SpawnPoint);
    }
}

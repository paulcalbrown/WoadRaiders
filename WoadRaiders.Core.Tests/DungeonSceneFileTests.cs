using System;
using System.IO;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The scene-to-geometry pipeline, engine-free: Godot .tscn text goes in,
/// realm data comes out — markers, and BoxMesh SLABS in the "ground" and
/// "structure" groups (with composed and rotated transforms) triangulated
/// straight from the scene text. This is what the tools run when they read a
/// realm scene, tested directly.
/// </summary>
public class DungeonSceneFileTests
{
    private const string Realm = """
        [gd_scene load_steps=3 format=3]

        [sub_resource type="BoxMesh" id="slab"]
        size = Vector3(100, 20, 60)

        [sub_resource type="BoxMesh" id="unit"]

        [node name="Realm" type="Node3D"]

        [node name="Terraces" type="Node3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 20)

        [node name="Floor" type="MeshInstance3D" parent="Terraces" groups=["ground"]]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 30, 0)
        mesh = SubResource("slab")

        [node name="Turned" type="MeshInstance3D" parent="Terraces" groups=["structure"]]
        transform = Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, 0, 0, 0)
        mesh = SubResource("slab")

        [node name="Tiny" type="MeshInstance3D" parent="." groups=["structure"]]
        mesh = SubResource("unit")

        [node name="Scenery" type="MeshInstance3D" parent="."]
        mesh = SubResource("slab")

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
        """;

    [Fact]
    public void A_realm_scene_parses_into_markers_and_a_slab_soup()
    {
        var geometry = DungeonSceneFile.Parse(Realm, "res://maps/Realm.tscn");

        Assert.Equal(new Vector3(100, 1, 200), geometry.SpawnPoint);
        Assert.Equal(new Vector3(400, 9, 500), geometry.BossSpawn);
        Assert.Equal("res://maps/Realm.tscn", geometry.ScenePath);

        Assert.Equal(
            new[] { EnemyType.Rogue, EnemyType.Mage, EnemyType.Minion },
            geometry.EnemySpawns.Select(s => s.Type).ToArray());
        Assert.Equal(new Vector3(30, 0, 40), geometry.EnemySpawns[0].Position);

        // Three slabs, 12 triangles each; the un-grouped "Scenery" mesh is
        // ignored — scenery needs no convention.
        var soup = geometry.Soup;
        Assert.NotNull(soup);
        Assert.Equal(36, soup!.Triangles.Length / 3);
        Assert.Equal(12, soup.FloorTriangleCount); // one ground slab, first in the soup
    }

    [Fact]
    public void Slabs_compose_transforms_and_ride_where_they_stand()
    {
        var soup = DungeonSceneFile.Parse(Realm).Soup!;

        // Floor: parent (10,0,20) + own (5,30,0), size (100,20,60) → top face
        // at y=40 over x∈[-35,65], z∈[-10,50].
        Assert.Equal(40f, soup.FloorHeightAt(15f, 20f) ?? float.NaN, 3);
        Assert.Null(soup.FloorHeightAt(200f, 200f)); // no floor out there

        // Turned: the same slab yawed 90° under the parent — its footprint
        // swaps, so its long side now runs along z. As structure it never
        // answers floor queries, but it blocks sight straight through it.
        Assert.True(soup.SegmentHits(new Vector3(-30, 5, 20), new Vector3(50, 5, 20)));
    }

    [Fact]
    public void A_non_slab_mesh_in_the_groups_needs_the_bake_tool()
    {
        var text = """
            [gd_scene format=3]
            [sub_resource type="PlaneMesh" id="p"]
            [node name="R" type="Node3D"]
            [node name="Ground" type="MeshInstance3D" parent="." groups=["ground"]]
            mesh = SubResource("p")
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """;
        var e = Assert.Throws<InvalidDataException>(() => DungeonSceneFile.Parse(text));
        Assert.Contains("bake", e.Message);

        // The bake tool samples the meshes and hands the whole soup in — then it parses.
        var sampled = new SoupBuilder()
            .AddBox(new Aabb(Vector3.Zero, new Vector3(10, 1, 10)), floor: true)
            .Build();
        var geometry = DungeonSceneFile.Parse(text, sampledSoup: sampled);
        Assert.Same(sampled, geometry.Soup);
    }

    [Fact]
    public void A_scene_of_markers_alone_is_a_flat_map()
    {
        var geometry = DungeonSceneFile.Parse("""
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """);
        Assert.Null(geometry.Soup);
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

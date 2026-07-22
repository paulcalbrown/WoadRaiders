using System;
using System.IO;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The scene-to-geometry pipeline, engine-free: Godot .tscn text goes in,
/// realm data comes out — markers, and every BoxMesh in the scene (with
/// composed and rotated transforms) triangulated straight from the scene
/// text. No groups, no naming, no privileged mesh: an author builds a scene
/// and the pipeline reads what is there. This is what the tools run when they
/// read a realm scene, tested directly.
/// </summary>
public class RealmSceneFileTests
{
    // The fixture keeps the "ground"/"structure" groups an older pipeline
    // required, precisely so their IRRELEVANCE is under test: tagged and
    // untagged meshes must bake alike, and scenes authored before the split
    // was retired must keep loading unchanged.
    private const string Realm = """
        [gd_scene load_steps=3 format=3]

        [sub_resource type="BoxMesh" id="box"]
        size = Vector3(100, 20, 60)

        [sub_resource type="BoxMesh" id="unit"]

        [node name="Realm" type="Node3D"]

        [node name="Terraces" type="Node3D" parent="."]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 20)

        [node name="Floor" type="MeshInstance3D" parent="Terraces" groups=["ground"]]
        transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 30, 0)
        mesh = SubResource("box")

        [node name="Turned" type="MeshInstance3D" parent="Terraces" groups=["structure"]]
        transform = Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, 0, 0, 0)
        mesh = SubResource("box")

        [node name="Tiny" type="MeshInstance3D" parent="." groups=["structure"]]
        mesh = SubResource("unit")

        [node name="Scenery" type="MeshInstance3D" parent="."]
        mesh = SubResource("box")

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
    public void A_realm_scene_parses_into_markers_and_a_geometry_soup()
    {
        var geometry = RealmSceneFile.Parse(Realm, "res://maps/Realm.tscn");

        Assert.Equal(new Vector3(100, 1, 200), geometry.SpawnPoint);
        Assert.Equal(new Vector3(400, 9, 500), geometry.BossSpawn);
        Assert.Equal("res://maps/Realm.tscn", geometry.ScenePath);
        // This realm authors no exit — the way out opens where the boss stood.
        Assert.Null(geometry.PortalSpawn);

        Assert.Equal(
            new[] { EnemyType.Rogue, EnemyType.Mage, EnemyType.Minion },
            geometry.EnemySpawns.Select(s => s.Type).ToArray());
        Assert.Equal(new Vector3(30, 0, 40), geometry.EnemySpawns[0].Position);

        // FOUR boxes, 12 triangles each — including "Scenery", which sits in
        // no group at all. Authors tag nothing: every mesh in the scene is
        // geometry, and what it MEANS is read back off its shape afterwards.
        var soup = geometry.Soup;
        Assert.NotNull(soup);
        Assert.Equal(48, soup!.Triangles.Length / 3);
    }

    [Fact]
    public void No_collide_excuses_a_mesh_and_everything_under_it()
    {
        // The one claim an author may make about geometry. The hall is solid;
        // the banner hanging in it is not, and neither is the tassel hung off
        // the banner — the claim runs down the subtree so one tag on a folder
        // of dressing covers all of it. The spawn marker inside that folder
        // still marks a spawn: the claim is about GEOMETRY, not about nodes.
        var realm = RealmSceneFile.Parse("""
            [gd_scene load_steps=2 format=3]

            [sub_resource type="BoxMesh" id="box"]
            size = Vector3(400, 20, 400)

            [node name="Realm" type="Node3D"]

            [node name="Hall" type="MeshInstance3D" parent="."]
            mesh = SubResource("box")

            [node name="Dressing" type="Node3D" parent="." groups=["no_collide"]]

            [node name="Banner" type="MeshInstance3D" parent="Dressing"]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 200, 0)
            mesh = SubResource("box")

            [node name="Tassel" type="MeshInstance3D" parent="Dressing/Banner"]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 100, 0)
            mesh = SubResource("box")

            [node name="PlayerSpawn" type="Marker3D" parent="Dressing"]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 5, 60, 7)
            """);

        // One box's worth of triangles: the hall alone.
        Assert.Equal(12, realm.Soup!.Triangles.Length / 3);
        Assert.Equal(10f, realm.Soup.TopSurfaceAt(0f, 0f) ?? float.NaN, 3);   // the hall's top face
        Assert.Equal(new Vector3(5, 60, 7), realm.SpawnPoint);                // the marker still counts
    }

    [Fact]
    public void A_mesh_in_no_group_is_geometry_like_any_other()
    {
        // The scene's only mesh is untagged and unnamed — the case that used
        // to bake to nothing at all and refuse to load.
        var soup = RealmSceneFile.Parse("""
            [gd_scene load_steps=2 format=3]

            [sub_resource type="BoxMesh" id="box"]
            size = Vector3(100, 20, 100)

            [node name="Realm" type="Node3D"]

            [node name="AnyOldMesh" type="MeshInstance3D" parent="."]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 30, 0)
            mesh = SubResource("box")

            [node name="PlayerSpawn" type="Marker3D" parent="."]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 40, 0)
            """).Soup;

        Assert.NotNull(soup);
        // Its top face holds a raider up, and its side blocks a body — both
        // read from the triangles' own normals, neither declared anywhere.
        Assert.Equal(40f, soup!.TopSurfaceAt(0f, 0f) ?? float.NaN, 3);
        Assert.True(soup.SegmentHits(new Vector3(-80, 30, 0), new Vector3(80, 30, 0), blockersOnly: true),
            "the box's sheer side should block a body's clearance probe");
        Assert.False(soup.SegmentHits(new Vector3(-20, 45, 0), new Vector3(20, 45, 0), blockersOnly: true),
            "nothing sheer stands above the box's top face");
    }

    [Fact]
    public void Boxes_compose_transforms_and_ride_where_they_stand()
    {
        var soup = RealmSceneFile.Parse(Realm).Soup!;

        // Floor: parent (10,0,20) + own (5,30,0), size (100,20,60) → top face
        // at y=40 over x∈[-35,65], z∈[-10,50].
        Assert.Equal(40f, soup.TopSurfaceAt(15f, 20f) ?? float.NaN, 3);
        Assert.Null(soup.TopSurfaceAt(200f, 200f)); // no floor out there

        // Turned: the same box yawed 90° under the parent — its footprint
        // swaps, so its long side now runs along z. As structure it never
        // answers floor queries, but it blocks sight straight through it.
        Assert.True(soup.SegmentHits(new Vector3(-30, 5, 20), new Vector3(50, 5, 20)));
    }

    [Fact]
    public void A_non_box_mesh_in_the_groups_needs_the_bake_tool()
    {
        var text = """
            [gd_scene format=3]
            [sub_resource type="PlaneMesh" id="p"]
            [node name="R" type="Node3D"]
            [node name="Ground" type="MeshInstance3D" parent="." groups=["ground"]]
            mesh = SubResource("p")
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """;
        var e = Assert.Throws<InvalidDataException>(() => RealmSceneFile.Parse(text));
        Assert.Contains("bake", e.Message);

        // The bake tool samples the meshes and hands the whole soup in — then it parses.
        var sampled = new SoupBuilder()
            .AddBox(new Aabb(Vector3.Zero, new Vector3(10, 1, 10)))
            .Build();
        var geometry = RealmSceneFile.Parse(text, sampledSoup: sampled);
        Assert.Same(sampled, geometry.Soup);
    }

    // An instanced kit asset is OPAQUE to scene text: the file says only
    // "instance=ExtResource(...)", so this reader cannot tell whether the
    // piece carries collision. Skipping it silently was a real fault — a
    // realm whose only doorway was plugged by an instanced prop parsed to an
    // OPEN doorway and passed every check, while the baked geometry it would
    // actually be served as had the boss walled off. Absent geometry is the
    // one fault a reachability proof can never see, so the reader refuses.
    [Fact]
    public void An_instanced_prop_that_may_block_is_refused_not_skipped()
    {
        var text = """
            [gd_scene load_steps=3 format=3]
            [ext_resource type="PackedScene" path="res://assets/coffin.gltf" id="1_kit"]
            [sub_resource type="BoxMesh" id="box"]
            size = Vector3(100, 20, 100)
            [node name="R" type="Node3D"]
            [node name="Floor" type="MeshInstance3D" parent="."]
            mesh = SubResource("box")
            [node name="Sarcophagus" type="Node3D" parent="." instance=ExtResource("1_kit")]
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """;

        var e = Assert.Throws<InvalidDataException>(() => RealmSceneFile.Parse(text));
        Assert.Contains("instanced", e.Message);
        Assert.Contains("bake", e.Message);

        // The bake tool sees inside the instance and hands the soup in — then it parses.
        var sampled = new SoupBuilder()
            .AddBox(new Aabb(Vector3.Zero, new Vector3(10, 1, 10)))
            .Build();
        Assert.Same(sampled, RealmSceneFile.Parse(text, sampledSoup: sampled).Soup);
    }

    // The escape the shipping Crypt rides on: its 237 kit pieces sit under one
    // no_collide folder, so they are dressing by declaration and this reader
    // owes them nothing. Without this the Crypt's own scene would not parse.
    [Fact]
    public void An_instanced_prop_declared_passable_costs_the_reader_nothing()
    {
        var geometry = RealmSceneFile.Parse("""
            [gd_scene load_steps=3 format=3]
            [ext_resource type="PackedScene" path="res://assets/coffin.gltf" id="1_kit"]
            [sub_resource type="BoxMesh" id="box"]
            size = Vector3(100, 20, 100)
            [node name="R" type="Node3D"]
            [node name="Floor" type="MeshInstance3D" parent="."]
            mesh = SubResource("box")
            [node name="Relics" type="Node3D" parent="." groups=["no_collide"]]
            [node name="Sarcophagus" type="Node3D" parent="Relics" instance=ExtResource("1_kit")]
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """);

        // The floor alone — and no refusal, because nothing opaque claims to block.
        Assert.Equal(12, geometry.Soup!.Triangles.Length / 3);
    }

    [Fact]
    public void A_scene_of_markers_alone_is_a_flat_map()
    {
        var geometry = RealmSceneFile.Parse("""
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            """);
        Assert.Null(geometry.Soup);
    }

    [Fact]
    public void A_scene_without_a_player_spawn_is_refused()
    {
        var e = Assert.Throws<InvalidDataException>(() => RealmSceneFile.Parse("""
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
        var geometry = RealmSceneFile.Parse("""
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

    // A realm whose ending is somewhere other than its last fight says so, and
    // the marker composes through its parent's transform like any other.
    [Fact]
    public void An_authored_portal_marker_moves_the_way_out_off_the_boss()
    {
        var geometry = RealmSceneFile.Parse("""
            [gd_scene format=3]
            [node name="R" type="Node3D"]
            [node name="PlayerSpawn" type="Marker3D" parent="."]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 0, 20)
            [node name="BossSpawn" type="Marker3D" parent="."]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 900, -800, 30)
            [node name="Markers" type="Node3D" parent="."]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, 1000, 0, 0)
            [node name="PortalSpawn" type="Marker3D" parent="Markers"]
            transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, -900, 5, 20)
            """);

        Assert.Equal(new Vector3(900, -800, 30), geometry.BossSpawn);
        Assert.Equal(new Vector3(100, 5, 20), geometry.PortalSpawn);
    }

    [Fact]
    public void The_portal_marker_survives_the_json_the_server_is_handed()
    {
        var realm = new RealmDefinition(new Vector3(1, 2, 3), null, Array.Empty<EnemySpawnPoint>())
        {
            BossSpawn = new Vector3(40, 50, 60),
            PortalSpawn = new Vector3(70, 80, 90),
        };

        var parsed = RealmDefinitionFile.Parse(RealmDefinitionFile.ToJson(realm));

        Assert.Equal(new Vector3(70, 80, 90), parsed.PortalSpawn);
        // Absent stays absent — the fallback to the boss must not be baked in
        // as data, or a realm could never go back to having no authored exit.
        Assert.Null(RealmDefinitionFile.Parse(RealmDefinitionFile.ToJson(
            new RealmDefinition(Vector3.Zero, null, Array.Empty<EnemySpawnPoint>()))).PortalSpawn);
    }
}

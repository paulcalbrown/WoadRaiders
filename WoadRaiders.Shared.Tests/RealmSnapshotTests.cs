using System;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class RealmSnapshotTests
{
    private static RealmDefinition SampleRealm() => new(
        new Vector3(1, 0, 3),
        new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-50, -20, -50), new Vector3(50, 0, 50)))
            .AddBox(new Aabb(new Vector3(-5, 0, -5), new Vector3(5, 8, 5)))
            .Build(),
        new[] { new EnemySpawnPoint(new Vector3(100, 0, 0), EnemyType.Minion) })
    {
        ScenePath = "res://maps/Barrow.tscn",
    };

    [Fact]
    public void From_carries_spawn_scene_and_the_soup()
    {
        var realm = SampleRealm();

        var packet = RealmSnapshot.From(realm);

        Assert.Equal(1f, packet.SpawnX);
        Assert.Equal(0f, packet.SpawnY);
        Assert.Equal(3f, packet.SpawnZ);
        Assert.Equal("res://maps/Barrow.tscn", packet.ScenePath);
        Assert.Equal(realm.Soup!.Vertices, packet.SoupVertices);
        Assert.Equal(realm.Soup.Triangles, packet.SoupTriangles);
    }

    [Fact]
    public void From_maps_a_null_scene_path_to_empty_string()
    {
        var realm = new RealmDefinition(Vector3.Zero, null, Array.Empty<EnemySpawnPoint>());
        Assert.Equal("", RealmSnapshot.From(realm).ScenePath);
    }

    [Fact]
    public void Geometry_survives_the_full_wire_round_trip()
    {
        // Server side: project + serialize. Client side: deserialize + rebuild.
        // This is the invariant prediction depends on: the client must move
        // over exactly the triangles the server authored.
        var realm = SampleRealm();

        var writer = new NetDataWriter();
        RealmSnapshot.From(realm).Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var packet = new RealmGeometryPacket();
        packet.Deserialize(reader);

        var back = RealmSnapshot.ToDefinition(packet);

        Assert.Equal(realm.SpawnPoint, back.SpawnPoint);
        Assert.Equal(realm.ScenePath, back.ScenePath);
        Assert.Equal(realm.Soup!.Vertices, back.Soup!.Vertices);
        Assert.Equal(realm.Soup.Triangles, back.Soup.Triangles);
        Assert.Empty(back.EnemySpawns);            // spawns are server-only, never on the wire
    }

    [Fact]
    public void ToDefinition_maps_an_empty_scene_path_to_null()
    {
        // "" on the wire means "no authored scene"; the client's fallback rendering
        // keys off a null ScenePath.
        var bare = new RealmDefinition(Vector3.Zero, null, Array.Empty<EnemySpawnPoint>());
        Assert.Null(RealmSnapshot.ToDefinition(RealmSnapshot.From(bare)).ScenePath);
    }

    [Fact]
    public void Fingerprint_is_stable_for_identical_maps()
    {
        // Two packets independently projected from the same realm must match —
        // this is what lets a reconnect recognize "same map, keep the visuals".
        Assert.Equal(
            RealmSnapshot.Fingerprint(RealmSnapshot.From(SampleRealm())),
            RealmSnapshot.Fingerprint(RealmSnapshot.From(SampleRealm())));
    }

    [Fact]
    public void Fingerprint_changes_when_the_map_changes()
    {
        var baseline = RealmSnapshot.Fingerprint(RealmSnapshot.From(SampleRealm()));

        var movedSpawn = RealmSnapshot.From(SampleRealm());
        movedSpawn.SpawnX += 1f;
        Assert.NotEqual(baseline, RealmSnapshot.Fingerprint(movedSpawn));

        var movedWall = RealmSnapshot.From(SampleRealm());
        movedWall.SoupVertices = (float[])movedWall.SoupVertices.Clone();
        movedWall.SoupVertices[3] += 1f;
        Assert.NotEqual(baseline, RealmSnapshot.Fingerprint(movedWall));

        var otherScene = RealmSnapshot.From(SampleRealm());
        otherScene.ScenePath = "res://maps/Other.tscn";
        Assert.NotEqual(baseline, RealmSnapshot.Fingerprint(otherScene));
    }

    [Fact]
    public void Packet_survives_a_serialize_round_trip()
    {
        var packet = RealmSnapshot.From(SampleRealm());

        var writer = new NetDataWriter();
        packet.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new RealmGeometryPacket();
        back.Deserialize(reader);

        Assert.Equal(packet.SpawnX, back.SpawnX);
        Assert.Equal(packet.SpawnZ, back.SpawnZ);
        Assert.Equal(packet.ScenePath, back.ScenePath);
        Assert.Equal(packet.SoupVertices, back.SoupVertices);
        Assert.Equal(packet.SoupTriangles, back.SoupTriangles);
    }
}

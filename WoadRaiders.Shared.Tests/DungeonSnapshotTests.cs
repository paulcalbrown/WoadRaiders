using System;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class DungeonSnapshotTests
{
    private static DungeonGeometry SampleDungeon() => new(
        new Vector3(1, 2, 3),
        new[]
        {
            new Aabb(new Vector3(0, 0, 0), new Vector3(10, 20, 30)),
            new Aabb(new Vector3(-5, 0, -5), new Vector3(5, 8, 5)),
        },
        new[] { new EnemySpawnPoint(new Vector3(100, 0, 0), EnemyType.Minion) })
    {
        ScenePath = "res://maps/Barrow.tscn",
    };

    [Fact]
    public void From_flattens_solids_and_carries_spawn_and_scene()
    {
        var dungeon = SampleDungeon();

        var packet = DungeonSnapshot.From(dungeon);

        Assert.Equal(1f, packet.SpawnX);
        Assert.Equal(2f, packet.SpawnY);
        Assert.Equal(3f, packet.SpawnZ);
        Assert.Equal("res://maps/Barrow.tscn", packet.ScenePath);

        Assert.Equal(dungeon.Solids.Count * 6, packet.Boxes.Length);
        // First solid: min (0,0,0) then max (10,20,30), flattened in order.
        Assert.Equal(new[] { 0f, 0f, 0f, 10f, 20f, 30f }, packet.Boxes[..6]);
    }

    [Fact]
    public void From_maps_a_null_scene_path_to_empty_string()
    {
        var dungeon = new DungeonGeometry(Vector3.Zero, Array.Empty<Aabb>(), Array.Empty<EnemySpawnPoint>());
        Assert.Equal("", DungeonSnapshot.From(dungeon).ScenePath);
    }

    [Fact]
    public void Geometry_survives_the_full_wire_round_trip()
    {
        // Server side: project + serialize. Client side: deserialize + rebuild.
        // This is the invariant prediction depends on: the client must collide
        // against exactly the solids the server authored.
        var dungeon = SampleDungeon();

        var writer = new NetDataWriter();
        DungeonSnapshot.From(dungeon).Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var packet = new DungeonGeometryPacket();
        packet.Deserialize(reader);

        var back = DungeonSnapshot.ToGeometry(packet);

        Assert.Equal(dungeon.SpawnPoint, back.SpawnPoint);
        Assert.Equal(dungeon.ScenePath, back.ScenePath);
        Assert.Equal(dungeon.Solids, back.Solids); // Aabb is a record struct — value equality
        Assert.Empty(back.EnemySpawns);            // spawns are server-only, never on the wire
    }

    [Fact]
    public void ToGeometry_maps_an_empty_scene_path_to_null()
    {
        // "" on the wire means "no authored scene"; the client's fallback rendering
        // keys off a null ScenePath.
        var bare = new DungeonGeometry(Vector3.Zero, Array.Empty<Aabb>(), Array.Empty<EnemySpawnPoint>());
        Assert.Null(DungeonSnapshot.ToGeometry(DungeonSnapshot.From(bare)).ScenePath);
    }

    [Fact]
    public void Fingerprint_is_stable_for_identical_maps()
    {
        // Two packets independently projected from the same dungeon must match —
        // this is what lets a reconnect recognize "same map, keep the visuals".
        Assert.Equal(
            DungeonSnapshot.Fingerprint(DungeonSnapshot.From(SampleDungeon())),
            DungeonSnapshot.Fingerprint(DungeonSnapshot.From(SampleDungeon())));
    }

    [Fact]
    public void Fingerprint_changes_when_the_map_changes()
    {
        var baseline = DungeonSnapshot.Fingerprint(DungeonSnapshot.From(SampleDungeon()));

        var movedSpawn = DungeonSnapshot.From(SampleDungeon());
        movedSpawn.SpawnX += 1f;
        Assert.NotEqual(baseline, DungeonSnapshot.Fingerprint(movedSpawn));

        var movedWall = DungeonSnapshot.From(SampleDungeon());
        movedWall.Boxes[3] += 1f;
        Assert.NotEqual(baseline, DungeonSnapshot.Fingerprint(movedWall));

        var otherScene = DungeonSnapshot.From(SampleDungeon());
        otherScene.ScenePath = "res://maps/Other.tscn";
        Assert.NotEqual(baseline, DungeonSnapshot.Fingerprint(otherScene));
    }

    [Fact]
    public void Packet_survives_a_serialize_round_trip()
    {
        var packet = DungeonSnapshot.From(SampleDungeon());

        var writer = new NetDataWriter();
        packet.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new DungeonGeometryPacket();
        back.Deserialize(reader);

        Assert.Equal(packet.SpawnX, back.SpawnX);
        Assert.Equal(packet.SpawnZ, back.SpawnZ);
        Assert.Equal(packet.ScenePath, back.ScenePath);
        Assert.Equal(packet.Boxes, back.Boxes);
    }
}

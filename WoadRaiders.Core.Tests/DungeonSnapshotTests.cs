using System;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Core.Tests;

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

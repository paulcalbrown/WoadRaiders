using System;
using System.Linq;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

/// <summary>
/// The open-realm additions to the geometry wire format (v14): the heightfield
/// terrain and the cosmetic props must survive world → packet → wire → world
/// bit-exact — prediction collides against the client's rebuild.
/// </summary>
public class TerrainSnapshotTests
{
    private static DungeonGeometry Realm()
    {
        var heights = Enumerable.Range(0, 12).Select(i => i * 7.25f - 20f).ToArray();
        return new DungeonGeometry(
            new Vector3(10, 5, 20),
            new[] { new Aabb(new Vector3(0, 0, 0), new Vector3(40, 40, 40)) },
            Array.Empty<EnemySpawnPoint>(),
            new HeightField(-80f, -120f, 40f, 4, 3, heights))
        {
            ScenePath = "realm:crag",
            Props = new[]
            {
                new DungeonProp(PropType.Brazier, new Vector3(1, 2, 3)),
                new DungeonProp(PropType.Brazier, new Vector3(-4, 5.5f, 6)),
            },
        };
    }

    private static DungeonGeometryPacket Roundtrip(DungeonGeometryPacket packet)
    {
        var writer = new NetDataWriter();
        packet.Serialize(writer);
        var back = new DungeonGeometryPacket();
        back.Deserialize(new NetDataReader(writer.Data, 0, writer.Length));
        return back;
    }

    [Fact]
    public void Terrain_and_props_survive_the_wire_bit_exact()
    {
        var packet = Roundtrip(DungeonSnapshot.From(Realm()));
        var geometry = DungeonSnapshot.ToGeometry(packet);

        var terrain = Assert.IsType<HeightField>(geometry.Terrain);
        Assert.Equal(-80f, terrain.OriginX);
        Assert.Equal(-120f, terrain.OriginZ);
        Assert.Equal(40f, terrain.CellSize);
        Assert.Equal(4, terrain.Width);
        Assert.Equal(3, terrain.Depth);
        Assert.Equal(Realm().Terrain!.Heights, terrain.Heights); // exact floats, sample for sample

        Assert.Equal(2, geometry.Props.Count);
        Assert.Equal(new Vector3(-4, 5.5f, 6), geometry.Props[1].Position);
        Assert.Equal("realm:crag", geometry.ScenePath);

        // The rebuilt geometry must SIMULATE identically: same ground, same walk.
        var original = Realm();
        Assert.Equal(original.GroundHeight(-50f, -100f), geometry.GroundHeight(-50f, -100f));
        var move = new Vector3(30, 0, 0);
        var from = new Vector3(-70, original.GroundHeight(-70, -110), -110);
        Assert.Equal(original.Move(from, move), geometry.Move(from, move));
    }

    [Fact]
    public void A_flat_map_still_travels_without_terrain()
    {
        var flat = new DungeonGeometry(Vector3.Zero,
            new[] { new Aabb(Vector3.Zero, new Vector3(10, 10, 10)) },
            Array.Empty<EnemySpawnPoint>());

        var packet = Roundtrip(DungeonSnapshot.From(flat));
        Assert.False(packet.HasTerrain);
        var geometry = DungeonSnapshot.ToGeometry(packet);
        Assert.Null(geometry.Terrain);
        Assert.Empty(geometry.Props);
        Assert.Equal(0f, geometry.GroundHeight(123f, 456f)); // the implicit flat plane
    }

    [Fact]
    public void Fingerprint_tells_realms_apart_by_their_terrain()
    {
        var a = DungeonSnapshot.From(Realm());
        var b = DungeonSnapshot.From(Realm());
        Assert.Equal(DungeonSnapshot.Fingerprint(a), DungeonSnapshot.Fingerprint(b));

        b.TerrainHeights[5] += 1f; // one sample differs → a different realm
        Assert.NotEqual(DungeonSnapshot.Fingerprint(a), DungeonSnapshot.Fingerprint(b));
    }

    [Fact]
    public void Hostile_terrain_dimensions_are_rejected_not_allocated()
    {
        var packet = DungeonSnapshot.From(Realm());
        var writer = new NetDataWriter();
        packet.Serialize(writer);

        // Corrupt the width field to a huge value: deserialization must throw
        // (the server's receive path disconnects the sender), never allocate.
        var evil = DungeonSnapshot.From(Realm());
        evil.TerrainWidth = int.MaxValue;
        evil.TerrainDepth = int.MaxValue;
        var evilWriter = new NetDataWriter();
        evil.Serialize(evilWriter);
        var back = new DungeonGeometryPacket();
        Assert.ThrowsAny<Exception>(() => back.Deserialize(new NetDataReader(evilWriter.Data, 0, evilWriter.Length)));
    }
}

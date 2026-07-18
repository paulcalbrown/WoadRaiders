using System;
using System.Linq;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class ClassProtocolTests
{
    [Fact]
    public void Join_request_round_trips_name_class_and_dungeon()
    {
        var join = new JoinRequest
        {
            Name = "Boudica",
            Class = (byte)CharacterClass.Ranger,
            Mode = (byte)JoinMode.Create,
            Dungeon = (byte)DungeonId.Crag,
            InstanceName = "Boudica's revenge",
        };

        var writer = new NetDataWriter();
        join.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new JoinRequest();
        back.Deserialize(reader);

        Assert.Equal("Boudica", back.Name);
        Assert.Equal((byte)CharacterClass.Ranger, back.Class);
        Assert.Equal((byte)JoinMode.Create, back.Mode);
        Assert.Equal((byte)DungeonId.Crag, back.Dungeon);
        Assert.Equal("Boudica's revenge", back.InstanceName);
    }

    [Fact]
    public void Dungeon_catalog_is_internally_consistent()
    {
        // The enum indexes the table; a reorder or gap would misroute every join.
        for (var i = 0; i < DungeonCatalog.All.Length; i++)
            Assert.Equal((DungeonId)i, DungeonCatalog.All[i].Id);

        Assert.Equal(DungeonId.Crag, DungeonCatalog.Sanitize((byte)DungeonId.Crag));
        Assert.Equal(DungeonId.Crag, DungeonCatalog.Sanitize(200)); // junk byte falls home

        foreach (var info in DungeonCatalog.All)
            Assert.Equal(info, DungeonCatalog.ForScene(info.ScenePath));
    }

    [Fact]
    public void Snapshot_carries_player_class_and_projectile_kind_through_a_round_trip()
    {
        // A mage player mid-cast: the world holds a classed player and their bolt.
        var world = new GameWorld
        {
            Geometry = new DungeonGeometry(Vector3.Zero, Array.Empty<Aabb>(), Array.Empty<EnemySpawnPoint>()),
        };
        world.AddPlayer(1, "M", CharacterClass.Mage);
        world.SetInput(1, new PlayerInput { Attack = true, AimX = 1f });
        world.Step();
        Assert.Single(world.Projectiles); // guard: the fixture really cast

        var snap = WorldSnapshot.From(world);
        var writer = new NetDataWriter();
        snap.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new WorldSnapshotPacket();
        back.Deserialize(reader);

        Assert.Equal((byte)CharacterClass.Mage, back.Players.Single().Class);
        Assert.Equal("M", back.Players.Single().Name); // drives fellow raiders' nameplates
        Assert.Equal((byte)ProjectileKind.MagicBolt, back.Projectiles.Single().Kind);
    }
}

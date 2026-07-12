using System;
using System.Linq;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class WorldSnapshotTests
{
    // A world with one of everything the snapshot carries: a player, two enemies
    // (a typed mage among them), a ground item, and — after one step — a mage bolt.
    private static GameWorld PopulatedWorld(out PlayerState player, out EnemyState mage, out GroundItem drop)
    {
        var world = new GameWorld
        {
            Geometry = new DungeonGeometry(Vector3.Zero, Array.Empty<Aabb>(), Array.Empty<EnemySpawnPoint>()),
        };
        player = world.AddPlayer(1, "Chief");
        player.Position = new Vector3(10, 0, 20);

        var range = EnemyArchetypes.Of(EnemyType.Mage).AttackRange - 20f;
        mage = world.SpawnEnemy(new Vector3(range, 0, 0), EnemyType.Mage);
        world.SpawnEnemy(new Vector3(-300, 0, 0), EnemyType.Minion);
        // Far from the player so it isn't auto-collected on the step below.
        drop = world.DropItem(new Item(7, "Woad Blade", ItemRarity.Rare, ItemType.Blade, 25), new Vector3(700, 0, 700));

        world.Step(); // the mage casts — a projectile now exists
        return world;
    }

    [Fact]
    public void From_projects_every_entity_with_matching_fields()
    {
        var world = PopulatedWorld(out var player, out var mage, out var drop);
        Assert.NotEmpty(world.Projectiles); // guard: the fixture really produced a bolt

        var snap = WorldSnapshot.From(world);

        // Counts line up with the live world.
        Assert.Equal(world.Tick, snap.ServerTick);
        Assert.Equal(world.Players.Count, snap.Players.Length);
        Assert.Equal(world.Enemies.Count, snap.Enemies.Length);
        Assert.Equal(world.GroundItems.Count, snap.GroundItems.Length);
        Assert.Equal(world.Projectiles.Count, snap.Projectiles.Length);

        // Player fields map 1:1.
        var ps = snap.Players.Single(p => p.Id == player.Id);
        Assert.Equal(player.Position.X, ps.X);
        Assert.Equal(player.Position.Y, ps.Y);
        Assert.Equal(player.Position.Z, ps.Z);
        Assert.Equal(player.Health, ps.Health);
        Assert.Equal(player.LastProcessedInput, ps.LastProcessedInput);
        Assert.Equal(player.IsAttacking, ps.Attacking);

        // Enemy type is carried as its byte (drives the client model).
        var es = snap.Enemies.Single(e => e.Id == mage.Id);
        Assert.Equal((byte)EnemyType.Mage, es.Type);
        Assert.Equal(mage.Position.X, es.X);
        Assert.Equal(mage.Health, es.Health);

        // Ground item keeps its (world-assigned) id and rarity.
        var gs = snap.GroundItems.Single();
        Assert.Equal(drop.Id, gs.Id);
        Assert.Equal((byte)ItemRarity.Rare, gs.Rarity);
        Assert.Equal(700f, gs.X);
        Assert.Equal(700f, gs.Z);

        // Projectile position matches the live bolt.
        var live = world.Projectiles.Values.Single();
        var prj = snap.Projectiles.Single();
        Assert.Equal(live.Id, prj.Id);
        Assert.Equal(live.Position.X, prj.X);
        Assert.Equal(live.Position.Z, prj.Z);
    }

    [Fact]
    public void Snapshot_survives_a_serialize_round_trip()
    {
        var world = PopulatedWorld(out _, out var mage, out _);
        var snap = WorldSnapshot.From(world);

        var writer = new NetDataWriter();
        snap.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new WorldSnapshotPacket();
        back.Deserialize(reader);

        Assert.Equal(snap.ServerTick, back.ServerTick);
        Assert.Equal(snap.Players.Length, back.Players.Length);
        Assert.Equal(snap.Enemies.Length, back.Enemies.Length);
        Assert.Equal(snap.GroundItems.Length, back.GroundItems.Length);
        Assert.Equal(snap.Projectiles.Length, back.Projectiles.Length);

        Assert.Equal((byte)EnemyType.Mage, back.Enemies.Single(e => e.Id == mage.Id).Type);
        Assert.Equal((byte)ItemRarity.Rare, back.GroundItems[0].Rarity);
        Assert.Equal(snap.Players[0].X, back.Players[0].X);
        Assert.Equal(snap.Projectiles[0].Id, back.Projectiles[0].Id);
    }
}

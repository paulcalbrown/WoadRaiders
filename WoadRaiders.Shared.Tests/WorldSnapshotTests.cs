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
        var world = new GameWorld(); // no geometry: the open arena, everything visible
        player = world.AddPlayer(1, "Chief");
        player.Position = new Vector3(10, 0, 20);

        var range = EnemyArchetypes.Of(EnemyType.Mage).AttackRange - 20f;
        mage = world.SpawnEnemy(new Vector3(range, 0, 0), EnemyType.Mage);
        world.SpawnEnemy(new Vector3(-300, 0, 0), EnemyType.Minion);
        // Far from the player so none of it is auto-collected on the step below.
        drop = world.DropItem(new Item(7, "Woad Sword", ItemRarity.Rare, ItemType.Sword, 25), new Vector3(700, 0, 700));
        world.DropGold(12, new Vector3(720, 0, 700));
        world.DropPotion(new Vector3(740, 0, 700));

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
        Assert.Equal(player.AttackAnimRemaining, ps.AttackAnim);   // reconcile inputs
        Assert.Equal(player.AttackCooldown, ps.AttackCooldown);

        // Enemy type is carried as its byte (drives the client model).
        var es = snap.Enemies.Single(e => e.Id == mage.Id);
        Assert.Equal((byte)EnemyType.Mage, es.Type);
        Assert.Equal(mage.Position.X, es.X);
        Assert.Equal(mage.Health, es.Health);

        // Ground loot keeps its (world-assigned) id and carries kind + rarity:
        // equipment colors its gem by rarity; gold and potions key off the kind.
        var gs = snap.GroundItems.Single(g => g.Id == drop.Id);
        Assert.Equal((byte)LootKind.Equipment, gs.Kind);
        Assert.Equal((byte)ItemRarity.Rare, gs.Rarity);
        Assert.Equal((byte)ItemType.Sword, gs.Type); // client picks the weapon mesh from this
        Assert.Equal(700f, gs.X);
        Assert.Equal(700f, gs.Z);
        Assert.Single(snap.GroundItems, g => g.Kind == (byte)LootKind.Gold);
        Assert.Single(snap.GroundItems, g => g.Kind == (byte)LootKind.HealthPotion);

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
        Assert.Equal(snap.GroundItems.Select(g => g.Kind), back.GroundItems.Select(g => g.Kind));
        Assert.Equal(snap.GroundItems.Select(g => g.Rarity), back.GroundItems.Select(g => g.Rarity));
        Assert.Equal(snap.GroundItems.Select(g => g.Type), back.GroundItems.Select(g => g.Type));
        Assert.Equal(snap.Players[0].X, back.Players[0].X);
        Assert.Equal(snap.Projectiles[0].Id, back.Projectiles[0].Id);
    }

    [Fact]
    public void Pickup_packet_survives_a_serialize_round_trip_for_every_kind()
    {
        foreach (var packet in new[]
        {
            new ItemPickedUpPacket { Kind = (byte)LootKind.Equipment, ItemId = 7, Name = "Woad Blade", Rarity = 2, Type = 1, Power = 25 },
            new ItemPickedUpPacket { Kind = (byte)LootKind.Gold, Amount = 17 },
            new ItemPickedUpPacket { Kind = (byte)LootKind.HealthPotion, Amount = 30 },
        })
        {
            var writer = new NetDataWriter();
            packet.Serialize(writer);
            var reader = new NetDataReader();
            reader.SetSource(writer);
            var back = new ItemPickedUpPacket();
            back.Deserialize(reader);

            Assert.Equal(packet.Kind, back.Kind);
            Assert.Equal(packet.Amount, back.Amount);
            Assert.Equal(packet.ItemId, back.ItemId);
            Assert.Equal(packet.Name, back.Name);
            Assert.Equal(packet.Power, back.Power);
        }
    }

    // Interest management: what bounds a snapshot is what a raider can SEE, not
    // what the realm holds. Without this a realm's population is paid for by
    // every player at 20 Hz, and a big realm's snapshot spans enough unreliable
    // chunks that losing one — and so all of it — stops being rare.
    public class Around
    {
        private static GameWorld ScatteredWorld()
        {
            var world = new GameWorld();
            world.AddPlayer(1, "Near").Position = Vector3.Zero;
            world.AddPlayer(2, "Far").Position = new Vector3(9000, 0, 0);
            world.SpawnEnemy(new Vector3(500, 0, 0), EnemyType.Minion);   // in sight
            world.SpawnEnemy(new Vector3(0, 0, 900), EnemyType.Rogue);    // in sight
            world.SpawnEnemy(new Vector3(5000, 0, 0), EnemyType.Minion);  // a realm away
            world.SpawnEnemy(new Vector3(0, -4000, 0), EnemyType.Mage);   // and far BELOW
            world.DropGold(9, new Vector3(300, 0, 0));
            world.DropGold(9, new Vector3(6000, 0, 0));
            return world;
        }

        [Fact]
        public void Sends_only_what_stands_within_sight()
        {
            var snap = WorldSnapshot.Around(ScatteredWorld(), Vector3.Zero, 2200f);

            Assert.Equal(2, snap.Enemies.Length);
            Assert.Single(snap.GroundItems);
            // Distance is measured in three dimensions, so a chamber far below
            // costs nothing to a raider who cannot see down into it.
            Assert.DoesNotContain(snap.Enemies, e => e.Y < -1000);
        }

        [Fact]
        public void Never_filters_the_warband()
        {
            // The far raider is 9000 away — four times the sight radius — and is
            // still sent. A warband that cannot see its own scattered members
            // cannot regroup, and there are only ever eight of them.
            var snap = WorldSnapshot.Around(ScatteredWorld(), Vector3.Zero, 2200f);

            Assert.Equal(2, snap.Players.Length);
            Assert.Contains(snap.Players, p => p.Name == "Far");
        }

        [Fact]
        public void A_radius_past_the_realm_filters_nothing()
        {
            // The Crag is smaller than its own sight radius on purpose: an open
            // highland under a sky must not pop, so it keeps the whole world.
            var world = ScatteredWorld();
            var whole = WorldSnapshot.From(world);
            var wide = WorldSnapshot.Around(world, Vector3.Zero, 100_000f);

            Assert.Equal(whole.Enemies.Length, wide.Enemies.Length);
            Assert.Equal(whole.GroundItems.Length, wide.GroundItems.Length);
        }

        [Fact]
        public void The_portal_is_told_wherever_it_stands()
        {
            // The way out is one point and the run ends on it, so it rides every
            // snapshot regardless of distance — a raider must always be able to
            // find it, including from the far side of the realm.
            var world = ScatteredWorld();
            world.OpenPortal(new Vector3(7000, 0, 0));

            var snap = WorldSnapshot.Around(world, Vector3.Zero, 2200f);

            Assert.True(snap.PortalOpen);
            Assert.Equal(7000f, snap.PortalX);
        }
    }
}

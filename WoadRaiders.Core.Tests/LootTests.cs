using System;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class LootTests
{
    [Fact]
    public void Loot_roll_is_deterministic_for_a_given_seed()
    {
        var a = LootGenerator.TryRollDrop(new Random(1234));
        var b = LootGenerator.TryRollDrop(new Random(1234));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Drop_rate_is_near_the_configured_chance()
    {
        var rng = new Random(7);
        var drops = 0;
        const int rolls = 20000;
        for (var i = 0; i < rolls; i++)
            if (LootGenerator.TryRollDrop(rng) is not null)
                drops++;

        var rate = drops / (double)rolls;
        Assert.InRange(rate, SimConstants.EnemyDropChance - 0.03, SimConstants.EnemyDropChance + 0.03);
    }

    [Fact]
    public void Common_drops_far_outnumber_legendary()
    {
        var rng = new Random(99);
        var common = 0;
        var legendary = 0;
        for (var i = 0; i < 20000; i++)
        {
            var item = LootGenerator.TryRollDrop(rng);
            if (item is null) continue;
            if (item.Rarity == ItemRarity.Common) common++;
            if (item.Rarity == ItemRarity.Legendary) legendary++;
        }

        Assert.True(common > legendary * 5, $"common={common} legendary={legendary}");
    }

    [Fact]
    public void Player_auto_collects_a_nearby_ground_item()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A"); // at origin
        var dropped = world.DropItem(new Item(0, "Test Blade", ItemRarity.Rare, ItemType.Blade, 25), new Vector2(10, 0));

        world.Step();

        Assert.Empty(world.GroundItems);
        Assert.Single(player.Inventory);
        Assert.Equal(dropped.Id, player.Inventory[0].Id);
    }

    [Fact]
    public void Distant_ground_item_is_not_collected()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A");
        world.DropItem(new Item(0, "Far Torc", ItemRarity.Common, ItemType.Torc, 5), new Vector2(500, 0));

        world.Step();

        Assert.Single(world.GroundItems);
        Assert.Empty(player.Inventory);
    }

    [Fact]
    public void Consume_pickups_reports_then_clears()
    {
        var world = new GameWorld(new Random(1));
        world.AddPlayer(1, "A");
        world.DropItem(new Item(0, "Test", ItemRarity.Magic, ItemType.Axe, 12), new Vector2(5, 0));

        world.Step();

        var first = world.ConsumePickups();
        Assert.Single(first);
        Assert.Equal(1, first[0].PlayerId);

        Assert.Empty(world.ConsumePickups()); // buffer drained
    }

    [Fact]
    public void Slain_enemies_eventually_drop_loot()
    {
        var world = new GameWorld(new Random(2024));
        // No player present, so nothing is auto-collected; drops stay on the ground.
        for (var i = 0; i < 30; i++)
            world.SpawnEnemy(new Vector2(500 + i, 500)).TakeDamage(SimConstants.EnemyMaxHealth);

        world.Step(); // removes dead enemies, rolling drops

        Assert.NotEmpty(world.GroundItems);
    }
}

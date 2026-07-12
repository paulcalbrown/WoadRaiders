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
    public void Equipment_drop_rate_is_near_the_configured_chance()
    {
        var rng = new Random(7);
        var drops = 0;
        const int rolls = 20000;
        for (var i = 0; i < rolls; i++)
            if (LootGenerator.TryRollDrop(rng) is not null)
                drops++;

        var rate = drops / (double)rolls;
        Assert.InRange(rate, SimConstants.EquipmentDropChance - 0.01, SimConstants.EquipmentDropChance + 0.01);
    }

    [Fact]
    public void Common_enemy_kills_drop_each_kind_at_the_configured_rates()
    {
        var world = new GameWorld(new Random(41));
        const int kills = 4000;
        for (var i = 0; i < kills; i++)
            world.SpawnEnemy(new Vector3(500 + i, 0, 500)).TakeDamage(SimConstants.EnemyMaxHealth);

        world.Step(); // removes the dead, rolling drops; no player → nothing collected

        var counts = world.GroundItems.Values
            .GroupBy(g => g.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.InRange(counts.GetValueOrDefault(LootKind.Gold) / (double)kills,
            SimConstants.GoldDropChance - 0.03, SimConstants.GoldDropChance + 0.03);
        Assert.InRange(counts.GetValueOrDefault(LootKind.HealthPotion) / (double)kills,
            SimConstants.PotionDropChance - 0.03, SimConstants.PotionDropChance + 0.03);
        Assert.InRange(counts.GetValueOrDefault(LootKind.Equipment) / (double)kills,
            SimConstants.EquipmentDropChance - 0.02, SimConstants.EquipmentDropChance + 0.02);

        // Every dropped pile pays within the configured coin range.
        Assert.All(world.GroundItems.Values.Where(g => g.Kind == LootKind.Gold),
            g => Assert.InRange(g.Amount, SimConstants.GoldDropMin, SimConstants.GoldDropMax));
    }

    [Fact]
    public void A_boss_always_drops_its_guaranteed_equipment()
    {
        var expected = EnemyArchetypes.Of(EnemyType.Boss).GuaranteedDrops;
        for (var seed = 0; seed < 20; seed++)
        {
            var world = new GameWorld(new Random(seed));
            world.SpawnEnemy(new Vector3(500, 0, 500), EnemyType.Boss)
                 .TakeDamage(EnemyArchetypes.Of(EnemyType.Boss).MaxHealth);

            world.Step();

            var equipment = world.GroundItems.Values.Count(g => g.Kind == LootKind.Equipment);
            Assert.Equal(expected, equipment);
        }
    }

    [Fact]
    public void Gold_pickup_fills_the_purse_and_leaves_the_inventory_alone()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A"); // at origin
        world.DropGold(17, new Vector3(10, 0, 0));

        world.Step();

        Assert.Equal(17, player.Gold);
        Assert.Empty(player.Inventory);
        Assert.Empty(world.GroundItems);
        var pickup = Assert.Single(world.ConsumePickups());
        Assert.Equal(LootKind.Gold, pickup.Kind);
        Assert.Equal(17, pickup.Amount);
        Assert.Null(pickup.Item);
    }

    [Fact]
    public void Potion_heals_the_injured_and_reports_the_amount()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A");
        player.TakeDamage(50f);
        world.DropPotion(new Vector3(10, 0, 0));

        world.Step();

        Assert.Equal(SimConstants.PlayerMaxHealth - 50f + SimConstants.PotionHealAmount, player.Health, 3);
        Assert.Empty(world.GroundItems);
        var pickup = Assert.Single(world.ConsumePickups());
        Assert.Equal(LootKind.HealthPotion, pickup.Kind);
        Assert.Equal((int)SimConstants.PotionHealAmount, pickup.Amount);
    }

    [Fact]
    public void Potion_healing_caps_at_max_health()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A");
        player.TakeDamage(10f); // barely hurt — the potion mostly overheals
        world.DropPotion(new Vector3(10, 0, 0));

        world.Step();

        Assert.Equal(SimConstants.PlayerMaxHealth, player.Health, 3);
        Assert.Equal(10, Assert.Single(world.ConsumePickups()).Amount); // only what was missing
    }

    [Fact]
    public void A_full_health_player_leaves_potions_on_the_ground()
    {
        var world = new GameWorld(new Random(1));
        world.AddPlayer(1, "A");
        world.DropPotion(new Vector3(10, 0, 0));

        world.Step();

        Assert.Single(world.GroundItems); // still lying there for whoever needs it
        Assert.Empty(world.ConsumePickups());
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
        var dropped = world.DropItem(new Item(0, "Test Sword", ItemRarity.Rare, ItemType.Sword, 25), new Vector3(10, 0, 0));

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
        world.DropItem(new Item(0, "Far Dagger", ItemRarity.Common, ItemType.Dagger, 5), new Vector3(500, 0, 0));

        world.Step();

        Assert.Single(world.GroundItems);
        Assert.Empty(player.Inventory);
    }

    [Fact]
    public void Consume_pickups_reports_then_clears()
    {
        var world = new GameWorld(new Random(1));
        world.AddPlayer(1, "A");
        world.DropItem(new Item(0, "Test", ItemRarity.Magic, ItemType.Axe, 12), new Vector3(5, 0, 0));

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
            world.SpawnEnemy(new Vector3(500 + i, 0, 500)).TakeDamage(SimConstants.EnemyMaxHealth);

        world.Step(); // removes dead enemies, rolling drops

        Assert.NotEmpty(world.GroundItems);
    }
}

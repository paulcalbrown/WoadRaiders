using System;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class EquipmentTests
{
    private static Item Make(int id, ItemType type, int power)
        => new(id, $"Test {type}", ItemRarity.Common, type, power);

    [Fact]
    public void Equipping_a_weapon_raises_attack_damage()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A");
        var axe = Make(10, ItemType.Axe, 25);
        player.Inventory.Add(axe);

        Assert.True(player.TryEquip(axe.Id));
        Assert.Equal(SimConstants.PlayerAttackDamage + 25, player.AttackDamage, 3);
    }

    [Fact]
    public void Equipped_weapon_deals_more_cleave_damage()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A");
        var enemy = world.SpawnEnemy(new Vector2(40, 0));

        var blade = Make(10, ItemType.Blade, 20);
        player.Inventory.Add(blade);
        player.TryEquip(blade.Id);

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        var expected = SimConstants.EnemyMaxHealth - (SimConstants.PlayerAttackDamage + 20);
        Assert.Equal(expected, enemy.Health, 3);
    }

    [Fact]
    public void Equipping_armor_reduces_incoming_damage()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A"); // origin
        world.SpawnEnemy(new Vector2(SimConstants.EnemyAttackRange - 5, 0)); // already in strike range

        var shield = Make(10, ItemType.Shield, 30);
        player.Inventory.Add(shield);
        player.TryEquip(shield.Id);

        world.Step(); // enemy strikes on the first in-range tick

        var reduction = 30 * SimConstants.ArmorDamageReductionPerPower;
        var expectedDamage = Math.Max(1f, SimConstants.EnemyAttackDamage - reduction);
        Assert.Equal(SimConstants.PlayerMaxHealth - expectedDamage, player.Health, 3);
    }

    [Fact]
    public void Items_route_to_the_correct_slot()
    {
        Assert.Equal(EquipSlot.Weapon, Equipment.SlotFor(ItemType.Blade));
        Assert.Equal(EquipSlot.Weapon, Equipment.SlotFor(ItemType.Spear));
        Assert.Equal(EquipSlot.Armor, Equipment.SlotFor(ItemType.Helm));
        Assert.Equal(EquipSlot.Trinket, Equipment.SlotFor(ItemType.Torc));
    }

    [Fact]
    public void Equipping_a_second_weapon_replaces_the_first()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A");
        var weak = Make(10, ItemType.Axe, 10);
        var strong = Make(11, ItemType.Blade, 40);
        player.Inventory.Add(weak);
        player.Inventory.Add(strong);

        player.TryEquip(weak.Id);
        player.TryEquip(strong.Id);

        Assert.Equal(SimConstants.PlayerAttackDamage + 40, player.AttackDamage, 3);
        Assert.Single(player.Equipped); // still one weapon slot occupied
    }

    [Fact]
    public void TryEquip_unknown_item_returns_false()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "A");
        Assert.False(player.TryEquip(999));
    }
}

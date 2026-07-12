using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class ClientStateTests
{
    private static Item Make(int id, ItemType type, int power)
        => new(id, $"Test {type}", ItemRarity.Common, type, power);

    [Fact]
    public void Inventory_preserves_pickup_order()
    {
        var state = new ClientState();
        state.AddItem(Make(5, ItemType.Sword, 10));
        state.AddItem(Make(3, ItemType.Shield, 20));
        state.AddItem(Make(9, ItemType.Dagger, 30));

        Assert.Equal(new[] { 5, 3, 9 }, state.Inventory.Select(i => i.Id));
    }

    [Fact]
    public void Attack_damage_mirrors_the_server_formula()
    {
        // The HUD must show what PlayerState.AttackDamage computes: base + weapon + trinket.
        var state = new ClientState();
        state.AddItem(Make(1, ItemType.Axe, 25));
        state.AddItem(Make(2, ItemType.Dagger, 7));
        state.SetEquipment(weaponId: 1, armorId: 0, trinketId: 2);

        Assert.Equal(SimConstants.PlayerAttackDamage + 25 + 7, state.AttackDamage, 3);
    }

    [Fact]
    public void Damage_reduction_mirrors_the_server_formula()
    {
        var state = new ClientState();
        state.AddItem(Make(1, ItemType.Shield, 30));
        state.SetEquipment(weaponId: 0, armorId: 1, trinketId: 0);

        Assert.Equal(30 * SimConstants.ArmorDamageReductionPerPower, state.DamageReduction, 3);
    }

    [Fact]
    public void Unowned_equipped_id_contributes_no_power()
    {
        // A malformed/laggy EquipmentUpdate naming an item we never received must not crash or add stats.
        var state = new ClientState();
        state.SetEquipment(weaponId: 42, armorId: 0, trinketId: 0);

        Assert.Equal(SimConstants.PlayerAttackDamage, state.AttackDamage, 3);
    }

    [Fact]
    public void IsEquipped_matches_slots_and_never_matches_empty()
    {
        var state = new ClientState();
        state.AddItem(Make(1, ItemType.Sword, 10));
        state.SetEquipment(weaponId: 1, armorId: 0, trinketId: 0);

        Assert.True(state.IsEquipped(1));
        Assert.False(state.IsEquipped(2));
        Assert.False(state.IsEquipped(0)); // 0 means "slot empty", not an item
    }

    [Fact]
    public void SetHealth_reports_only_drops_as_damage()
    {
        var state = new ClientState();

        Assert.True(state.SetHealth(SimConstants.PlayerMaxHealth - 10));  // hit
        Assert.False(state.SetHealth(SimConstants.PlayerMaxHealth - 10)); // steady
        Assert.False(state.SetHealth(SimConstants.PlayerMaxHealth));      // heal / respawn
    }

    [Fact]
    public void Gold_accumulates_across_pickups()
    {
        var state = new ClientState();
        state.AddGold(12);
        state.AddGold(8);
        Assert.Equal(20, state.Gold);
    }

    [Fact]
    public void Reset_returns_to_a_fresh_join()
    {
        var state = new ClientState();
        state.AddItem(Make(1, ItemType.Sword, 10));
        state.SetEquipment(1, 0, 0);
        state.SetHealth(12f);
        state.AddGold(50);

        state.Reset();

        Assert.Empty(state.Inventory);
        Assert.False(state.IsEquipped(1));
        Assert.Equal(SimConstants.PlayerMaxHealth, state.Health);
        Assert.Equal(SimConstants.PlayerAttackDamage, state.AttackDamage, 3);
        Assert.Equal(0, state.Gold);
    }
}

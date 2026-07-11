using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>Authoritative state for a single connected player.</summary>
public sealed class PlayerState : Combatant
{
    public string Name { get; set; }

    /// <summary>
    /// Unit direction the player faces on the ground plane (XZ). Updated from the
    /// last non-zero movement intent and held while idle, so a standing player
    /// keeps facing where they last moved. Melee strikes only land in front of it.
    /// </summary>
    public Vector3 Facing = Vector3.UnitX;

    /// <summary>
    /// Sequence number of the last input folded into this state. Broadcast in
    /// snapshots so a client knows how much of its predicted input the server has
    /// acknowledged (the basis for reconciliation).
    /// </summary>
    public uint LastProcessedInput;

    /// <summary>Items this player has collected. Authoritative, server-side.</summary>
    public List<Item> Inventory { get; } = new();

    /// <summary>Currently equipped item per slot.</summary>
    public Dictionary<EquipSlot, Item> Equipped { get; } = new();

    public PlayerState(int id, string name) : base(id, SimConstants.PlayerMaxHealth)
    {
        Name = name;
    }

    /// <summary>Effective attack damage: base plus equipped weapon and trinket power.</summary>
    public float AttackDamage =>
        SimConstants.PlayerAttackDamage + EquippedPower(EquipSlot.Weapon) + EquippedPower(EquipSlot.Trinket);

    /// <summary>Flat damage soaked from each incoming hit, from equipped armor.</summary>
    public float DamageReduction => EquippedPower(EquipSlot.Armor) * SimConstants.ArmorDamageReductionPerPower;

    /// <summary>Equip an owned item into its slot. Returns false if it isn't in the inventory.</summary>
    public bool TryEquip(int itemId)
    {
        foreach (var item in Inventory)
        {
            if (item.Id != itemId)
                continue;
            Equipped[Equipment.SlotFor(item.Type)] = item;
            return true;
        }
        return false;
    }

    private float EquippedPower(EquipSlot slot) =>
        Equipped.TryGetValue(slot, out var item) ? item.Power : 0f;
}

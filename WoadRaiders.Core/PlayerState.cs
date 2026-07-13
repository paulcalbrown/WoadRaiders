using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>Authoritative state for a single connected player.</summary>
public sealed class PlayerState : Combatant
{
    public string Name { get; set; }

    /// <summary>The class this player raids as. Fixed at creation — max health
    /// derives from it, so a class change means a fresh <see cref="PlayerState"/>.</summary>
    public CharacterClass Class { get; }

    /// <summary>This player's class stats (speed, damage, cooldown, reach).</summary>
    public ClassArchetype Archetype => ClassArchetypes.Of(Class);

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

    /// <summary>Coins collected this session. No spend sink yet — shops will come.</summary>
    public int Gold;

    /// <summary>The world tick this player joined on — the run-summary clock starts here.</summary>
    public int JoinTick;

    /// <summary>Currently equipped item per slot.</summary>
    public Dictionary<EquipSlot, Item> Equipped { get; } = new();

    public PlayerState(int id, string name, CharacterClass cls = CharacterClass.Knight)
        : base(id, ClassArchetypes.Of(cls).MaxHealth)
    {
        Name = name;
        Class = cls;
    }

    /// <summary>Effective attack damage: the class base plus equipped weapon and trinket power.</summary>
    public float AttackDamage =>
        Archetype.AttackDamage + EquippedPower(EquipSlot.Weapon) + EquippedPower(EquipSlot.Trinket);

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

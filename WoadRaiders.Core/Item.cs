namespace WoadRaiders.Core;

public enum ItemRarity : byte
{
    Common,
    Magic,
    Rare,
    Epic,
    Legendary,
}

/// <summary>
/// The kinds of equipment that drop, one per mesh in the KayKit weapon set the
/// client renders (see WorldView). Serialized as a byte in item and ground-loot
/// packets, so keep the numbering stable. Every weapon equips to the Weapon slot;
/// the Shield equips to Armor (see <see cref="Equipment.SlotFor"/>).
/// </summary>
public enum ItemType : byte
{
    Sword = 0,      // sword_1handed
    Greatsword = 1, // sword_2handed
    Axe = 2,        // axe_1handed
    Battleaxe = 3,  // axe_2handed
    Dagger = 4,     // dagger
    Staff = 5,      // staff
    Crossbow = 6,   // crossbow_1handed
    Shield = 7,     // shield_round
}

/// <summary>
/// A generated item. Immutable value — the world stamps a unique <see cref="Id"/>
/// when it drops. <see cref="Power"/> is the single primary stat for now.
/// </summary>
public sealed record Item(int Id, string Name, ItemRarity Rarity, ItemType Type, int Power);

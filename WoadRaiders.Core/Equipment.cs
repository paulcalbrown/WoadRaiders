namespace WoadRaiders.Core;

/// <summary>The three gear slots. An item's <see cref="ItemType"/> decides its slot.</summary>
public enum EquipSlot : byte
{
    Weapon,
    Armor,
    Trinket,
}

public static class Equipment
{
    /// <summary>
    /// Which slot an item type occupies. Every weapon in the kit equips to the
    /// Weapon slot; the shield is the only Armor piece. The Trinket slot has no
    /// items yet (the kit ships no amulet mesh) — it stays in the enum for when it does.
    /// </summary>
    public static EquipSlot SlotFor(ItemType type) => type switch
    {
        ItemType.Shield => EquipSlot.Armor,
        _ => EquipSlot.Weapon,
    };
}

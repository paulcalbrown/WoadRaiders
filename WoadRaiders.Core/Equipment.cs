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
    /// <summary>Which slot an item type occupies.</summary>
    public static EquipSlot SlotFor(ItemType type) => type switch
    {
        ItemType.Blade or ItemType.Axe or ItemType.Spear => EquipSlot.Weapon,
        ItemType.Shield or ItemType.Helm => EquipSlot.Armor,
        ItemType.Torc => EquipSlot.Trinket,
        _ => EquipSlot.Trinket,
    };
}

namespace WoadRaiders.Core;

public enum ItemRarity : byte
{
    Common,
    Magic,
    Rare,
    Epic,
    Legendary,
}

public enum ItemType : byte
{
    Blade,
    Axe,
    Spear,
    Shield,
    Helm,
    Torc,
}

/// <summary>
/// A generated item. Immutable value — the world stamps a unique <see cref="Id"/>
/// when it drops. <see cref="Power"/> is the single primary stat for now.
/// </summary>
public sealed record Item(int Id, string Name, ItemRarity Rarity, ItemType Type, int Power);

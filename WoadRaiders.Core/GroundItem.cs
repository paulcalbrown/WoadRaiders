using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// What a piece of ground loot is. Serialized as a byte in ground-item
/// snapshots, so keep the numbering stable.
/// </summary>
public enum LootKind : byte
{
    Equipment = 0,    // an Item that goes to the inventory
    Gold = 1,         // coins; added to the collector's purse
    HealthPotion = 2, // consumed on pickup; heals the collector
}

/// <summary>Loot lying in the world, waiting to be walked over.</summary>
public sealed class GroundItem
{
    public int Id { get; }
    public LootKind Kind { get; }

    /// <summary>The equipment payload; null for gold and potions.</summary>
    public Item? Item { get; }

    /// <summary>Gold: coins in the pile. Potion: nominal heal. Equipment: unused.</summary>
    public int Amount { get; }

    public Vector3 Position;

    /// <summary>Equipment on the ground. The ground id is the item's id.</summary>
    public GroundItem(Item item, Vector3 position)
    {
        Id = item.Id;
        Kind = LootKind.Equipment;
        Item = item;
        Position = position;
    }

    /// <summary>Gold or a potion on the ground.</summary>
    public GroundItem(int id, LootKind kind, int amount, Vector3 position)
    {
        Id = id;
        Kind = kind;
        Amount = amount;
        Position = position;
    }
}

/// <summary>
/// A record that a player collected loot this tick (server → client).
/// <see cref="Item"/> is set for equipment; <see cref="Amount"/> carries the
/// coins for gold and the health actually restored for a potion.
/// </summary>
public readonly record struct LootPickup(int PlayerId, LootKind Kind, Item? Item, int Amount);

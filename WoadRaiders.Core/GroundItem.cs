using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>An item lying in the world, waiting to be picked up.</summary>
public sealed class GroundItem
{
    public int Id { get; }
    public Item Item { get; }
    public Vector3 Position;

    public GroundItem(Item item, Vector3 position)
    {
        Id = item.Id;
        Item = item;
        Position = position;
    }
}

/// <summary>A record that a player collected an item this tick (server → client).</summary>
public readonly record struct LootPickup(int PlayerId, Item Item);

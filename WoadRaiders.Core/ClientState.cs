namespace WoadRaiders.Core;

/// <summary>
/// The client's replica of its own server-owned state: collected items, equipped
/// item ids, and health. All of it is authoritative-from-the-server — the client
/// only records what it is told (ItemPickedUp / EquipmentUpdate / snapshots) and
/// derives display stats from it. Engine-free so the stat math is unit-testable,
/// and kept in lockstep with <see cref="PlayerState"/>'s formulas so what the HUD
/// shows is what the server computes.
/// </summary>
public sealed class ClientState
{
    private readonly List<Item> _items = new();        // display order = pickup order (drives the 1-9 hotkeys)
    private readonly Dictionary<int, Item> _byId = new(); // id lookup for equipment stat math

    /// <summary>Items in pickup order — the inventory panel and 1-9 equip hotkeys index into this.</summary>
    public IReadOnlyList<Item> Inventory => _items;

    public int WeaponId { get; private set; }
    public int ArmorId { get; private set; }
    public int TrinketId { get; private set; }

    /// <summary>Authoritative health from the latest snapshot; never predicted.</summary>
    public float Health { get; private set; } = SimConstants.PlayerMaxHealth;

    /// <summary>Record an item the server says we picked up.</summary>
    public void AddItem(Item item)
    {
        _items.Add(item);
        _byId[item.Id] = item;
    }

    /// <summary>Record the equipped item id per slot (0 = empty), from an EquipmentUpdate.</summary>
    public void SetEquipment(int weaponId, int armorId, int trinketId)
    {
        WeaponId = weaponId;
        ArmorId = armorId;
        TrinketId = trinketId;
    }

    /// <summary>Record the authoritative health. Returns true when it dropped (a hit landed).</summary>
    public bool SetHealth(float health)
    {
        var damaged = health < Health;
        Health = health;
        return damaged;
    }

    /// <summary>True if this item id sits in any gear slot (0 never matches — it means "empty").</summary>
    public bool IsEquipped(int itemId) =>
        itemId != 0 && (itemId == WeaponId || itemId == ArmorId || itemId == TrinketId);

    /// <summary>Displayed attack: base plus weapon and trinket power (mirrors <see cref="PlayerState.AttackDamage"/>).</summary>
    public float AttackDamage => SimConstants.PlayerAttackDamage + PowerOf(WeaponId) + PowerOf(TrinketId);

    /// <summary>Displayed per-hit armor soak (mirrors <see cref="PlayerState.DamageReduction"/>).</summary>
    public float DamageReduction => PowerOf(ArmorId) * SimConstants.ArmorDamageReductionPerPower;

    /// <summary>Forget everything — a reconnect joins the server as a brand-new player.</summary>
    public void Reset()
    {
        _items.Clear();
        _byId.Clear();
        WeaponId = ArmorId = TrinketId = 0;
        Health = SimConstants.PlayerMaxHealth;
    }

    private float PowerOf(int itemId) => _byId.TryGetValue(itemId, out var item) ? item.Power : 0f;
}

using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The client's custom input actions, registered in code at startup (the rest of
/// the client is code-first too). Movement and attack reuse Godot's built-in
/// ui_* actions; these cover the inventory. Routing every binding through the
/// InputMap — instead of raw keycodes in _Input — keeps them in one place and
/// makes a future rebinding UI a matter of editing the map.
/// </summary>
public static class ClientActions
{
    public const string InventoryToggle = "inventory_toggle";

    /// <summary>equip_slot_1 .. equip_slot_9, indexed by inventory row.</summary>
    public static readonly string[] EquipSlots =
        Enumerable.Range(1, 9).Select(i => $"equip_slot_{i}").ToArray();

    public static void EnsureRegistered()
    {
        if (InputMap.HasAction(InventoryToggle))
            return; // already registered (e.g. a scene reload)

        Register(InventoryToggle, Key.I);
        for (var i = 0; i < EquipSlots.Length; i++)
            Register(EquipSlots[i], Key.Key1 + i);
    }

    private static void Register(string action, Key key)
    {
        InputMap.AddAction(action);
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key });
    }
}

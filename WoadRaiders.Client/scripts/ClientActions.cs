using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The client's custom input actions, registered in code at startup (the rest of
/// the client is code-first too). Movement and attack reuse Godot's built-in
/// ui_* actions; these cover the inventory and add WASD alongside the arrow keys.
/// Routing every binding through the InputMap — instead of raw keycodes in
/// _Input — keeps them in one place and makes a future rebinding UI a matter of
/// editing the map.
/// </summary>
public static class ClientActions
{
    public const string InventoryToggle = "inventory_toggle";

    /// <summary>Melee swing aimed at the cursor — left mouse button (ARPG-style).</summary>
    public const string Attack = "attack";

    /// <summary>Melee swing in the character's current facing — the Space key.</summary>
    public const string AttackForward = "attack_forward";

    /// <summary>Click/hold to path toward the cursor — right mouse button (ARPG-style).</summary>
    public const string MoveTo = "move_to";

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

        // Left mouse button swings toward the cursor; Space swings in the direction
        // the character is already facing.
        InputMap.AddAction(Attack);
        InputMap.ActionAddEvent(Attack, new InputEventMouseButton { ButtonIndex = MouseButton.Left });
        Register(AttackForward, Key.Space);

        // Right mouse button paths toward the cursor.
        InputMap.AddAction(MoveTo);
        InputMap.ActionAddEvent(MoveTo, new InputEventMouseButton { ButtonIndex = MouseButton.Right });

        // WASD as a second binding on the built-in movement actions (the arrow keys
        // stay). LocalPlayer reads ui_up/down/left/right, so it needs no changes.
        AddEvent("ui_up", Key.W);
        AddEvent("ui_left", Key.A);
        AddEvent("ui_down", Key.S);
        AddEvent("ui_right", Key.D);
    }

    private static void Register(string action, Key key)
    {
        InputMap.AddAction(action);
        AddEvent(action, key);
    }

    /// <summary>Bind a key to an existing action (the ui_* movement actions are built in).</summary>
    private static void AddEvent(string action, Key key) =>
        InputMap.ActionAddEvent(action, new InputEventKey { Keycode = key });
}

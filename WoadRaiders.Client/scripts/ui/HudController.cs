using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The 2D overlay: stats line, top-of-screen health bar with its damage-chip
/// trail, the inventory panel, and the connection-status banner. Pure
/// presentation — it renders whatever <see cref="ClientState"/> and the
/// connection report each frame and owns no game state of its own.
/// </summary>
public partial class HudController : CanvasLayer
{
    // Top-of-screen player health bar (pixels at the 1080 render base).
    private const float BarWidth = 460f;
    private const float BarHeight = 30f;
    private const float BarMargin = 18f; // gap from the top edge
    private const float BarPad = 3f;     // dark frame thickness around the fill

    private Label _stats = null!;
    private Label _invPanel = null!;
    private Label _status = null!;
    private ColorRect _healthChipRect = null!;
    private ColorRect _healthFillRect = null!;
    private Label _healthLabel = null!;
    private DamageChip _chip = DamageChip.Full;
    private CharacterClass _class = CharacterClass.Knight;
    private float _maxHealth = SimConstants.PlayerMaxHealth;

    public bool InventoryOpen { get; private set; }

    /// <summary>Tell the HUD which class it is drawing: the health bar's scale and the stats line.</summary>
    public void SetPlayerClass(CharacterClass cls)
    {
        _class = cls;
        _maxHealth = ClassArchetypes.Of(cls).MaxHealth;
    }

    public override void _Ready()
    {
        _stats = new Label { Position = new Vector2(16, 12) };
        AddChild(_stats);
        _invPanel = new Label { Position = new Vector2(16, 44), Visible = false };
        AddChild(_invPanel);

        // Player health bar, centred along the top edge. Anchored to the top-centre
        // so it holds its place as the window scales (stretch mode = viewport).
        var barBg = new ColorRect
        {
            Color = new Color(0.08f, 0.08f, 0.10f, 0.85f), // dark frame / empty track
            MouseFilter = Control.MouseFilterEnum.Ignore,   // never intercept input
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -BarWidth / 2f, OffsetRight = BarWidth / 2f,
            OffsetTop = BarMargin, OffsetBottom = BarMargin + BarHeight,
        };
        AddChild(barBg);

        // Chip first so it sits behind the fill: it shows in the gap the shrinking
        // fill leaves, marking health just lost, then drains down to meet the fill.
        _healthChipRect = new ColorRect
        {
            Color = new Color(1.0f, 0.7f, 0.7f, 0.95f), // very light red "recently lost" trail
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(BarPad, BarPad),
            Size = new Vector2(BarWidth - 2 * BarPad, BarHeight - 2 * BarPad),
        };
        barBg.AddChild(_healthChipRect);

        _healthFillRect = new ColorRect
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(BarPad, BarPad),
            Size = new Vector2(BarWidth - 2 * BarPad, BarHeight - 2 * BarPad),
        };
        barBg.AddChild(_healthFillRect);

        _healthLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Size = new Vector2(BarWidth, BarHeight),
        };
        barBg.AddChild(_healthLabel); // added after the fill → drawn on top

        // Connection banner just under the health bar; empty while Playing.
        _status = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0f, AnchorBottom = 0f,
            OffsetLeft = -BarWidth / 2f, OffsetRight = BarWidth / 2f,
            OffsetTop = BarMargin + BarHeight + 8f, OffsetBottom = BarMargin + BarHeight + 34f,
            Modulate = new Color(1f, 0.8f, 0.4f), // amber — visible but not alarming
        };
        AddChild(_status);
    }

    public void ToggleInventory()
    {
        InventoryOpen = !InventoryOpen;
        _invPanel.Visible = InventoryOpen;
    }

    /// <summary>Call when a snapshot shows the local player just took a hit.</summary>
    public void OnDamage() => _chip.OnDamage();

    /// <summary>Redraw everything from the current state; runs once per frame.</summary>
    public void Refresh(ClientState state, ConnectionState connection, double delta)
    {
        // The fill snaps to current health; the chip behind it lingers then drains,
        // so a hit leaves a brief pale trail of what was lost.
        var frac = Mathf.Clamp(state.Health / _maxHealth, 0f, 1f);
        _chip.Advance(frac, (float)delta);
        var inner = BarHeight - 2 * BarPad;
        _healthChipRect.Size = new Vector2((BarWidth - 2 * BarPad) * _chip.Fraction, inner);
        _healthFillRect.Size = new Vector2((BarWidth - 2 * BarPad) * frac, inner);
        _healthFillRect.Color = Color.FromHsv(0.33f * frac, 0.75f, 0.8f); // green when full → red when low
        _healthLabel.Text = $"{Mathf.RoundToInt(state.Health)} / {Mathf.RoundToInt(_maxHealth)}";

        _stats.Text = $"{_class}   Gold {state.Gold}   Items {state.Inventory.Count}   Atk {state.AttackDamage:0}   " +
                      $"Armor {state.DamageReduction:0.0}   " +
                      "[LMB] attack   [RMB] move   [I] inventory   [Esc] menu";

        _status.Text = connection switch
        {
            ConnectionState.Connecting => "Connecting to server ...",
            ConnectionState.Joining => "Joining ...",
            ConnectionState.Disconnected => "Connection lost — retrying ...",
            _ => "",
        };

        if (InventoryOpen)
            _invPanel.Text = BuildInventoryText(state);
    }

    private static string BuildInventoryText(ClientState state)
    {
        var lines = new List<string> { "INVENTORY   (I to close · 1-9 to equip)" };
        for (var i = 0; i < state.Inventory.Count; i++)
        {
            var it = state.Inventory[i];
            var num = i < 9 ? $"{i + 1})" : "  ";
            var mark = state.IsEquipped(it.Id) ? "[E]" : "   ";
            lines.Add($"{num} {mark} {it.Name} — {it.Type} · Power {it.Power}");
        }
        lines.Add($"— total: Attack {state.AttackDamage:0} · Armor {state.DamageReduction:0.0} —");
        return string.Join('\n', lines);
    }
}

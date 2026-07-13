using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// A menu button in the game's Celtic/gothic dress: heavy small-caps
/// serif in a faint frame that ignites radioactive green when highlighted —
/// glow border, knot spearheads sliding in from the sides, a woven underline
/// growing from the centre, the text itself turning green, and a slight
/// scale-up. Mouse hover and keyboard focus share one highlight state, so
/// pointing and arrow-key selection look identical.
/// </summary>
public partial class GothicButton : Button
{
    private static readonly string[] FontColorStates =
        ["font_color", "font_hover_color", "font_focus_color", "font_pressed_color", "font_hover_pressed_color"];

    private float _highlight;
    private Tween? _tween;
    private bool _hovered;
    private bool _focused;

    public override void _Ready()
    {
        Text = Text.ToUpperInvariant();
        CustomMinimumSize = new Vector2(0, 54);
        MouseDefaultCursorShape = CursorShape.PointingHand;

        AddThemeFontOverride("font", UiTheme.DisplayFont());
        AddThemeFontSizeOverride("font_size", 27);

        var idle = new StyleBoxFlat
        {
            BgColor = new Color(1f, 1f, 1f, 0.02f),
            BorderColor = new Color(UiTheme.WoadDim, 0.25f),
        };
        idle.SetBorderWidthAll(1);
        idle.SetContentMarginAll(8);

        var hot = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.10f, 0.05f, 0.55f),
            BorderColor = new Color(UiTheme.OozeGreen, 0.85f),
            ShadowColor = new Color(UiTheme.OozeGreen, 0.22f), // the radioactive glow
            ShadowSize = 16,
        };
        hot.SetBorderWidthAll(1);
        hot.SetContentMarginAll(8);

        var pressed = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.20f, 0.08f, 0.70f),
            BorderColor = UiTheme.OozeGreen,
            ShadowColor = new Color(UiTheme.OozeGreen, 0.30f),
            ShadowSize = 18,
        };
        pressed.SetBorderWidthAll(1);
        pressed.SetContentMarginAll(8);

        AddThemeStyleboxOverride("normal", idle);
        AddThemeStyleboxOverride("hover", hot);
        AddThemeStyleboxOverride("focus", hot);
        AddThemeStyleboxOverride("pressed", pressed);
        SetHighlight(0f);

        // Hover takes keyboard focus (like DungeonCard): hover and focus ignite
        // identically, so if they could point at different buttons, one would
        // stay lit "selected" while another glowed under the mouse — and Enter
        // would fire the stale one. Never steal focus from a text field though;
        // brushing past a button mid-typing must not interrupt the typing.
        MouseEntered += () =>
        {
            _hovered = true;
            if (!Disabled && GetViewport().GuiGetFocusOwner() is null or GothicButton)
                GrabFocus();
            Retarget();
        };
        MouseExited += () => { _hovered = false; Retarget(); };
        FocusEntered += () => { _focused = true; Retarget(); };
        FocusExited += () => { _focused = false; Retarget(); };
        Resized += () => PivotOffset = Size / 2f; // keep the scale pulse centred
    }

    private void Retarget()
    {
        float target = _hovered || _focused ? 1f : 0f;
        _tween?.Kill();
        _tween = CreateTween();
        _tween.TweenMethod(Callable.From<float>(SetHighlight), _highlight, target, 0.18)
            .SetTrans(Tween.TransitionType.Cubic)
            .SetEase(Tween.EaseType.Out);
    }

    private void SetHighlight(float value)
    {
        _highlight = value;
        Scale = Vector2.One * (1f + 0.035f * value);
        // The Button picks a different theme color per draw state; pin them all
        // to the tweened value so the text never snaps between states.
        var color = UiTheme.BoneSilver.Lerp(UiTheme.OozeGreen, value);
        foreach (var state in FontColorStates)
            AddThemeColorOverride(state, color);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_highlight <= 0.001f)
            return;
        var green = new Color(UiTheme.OozeGreen, _highlight);
        var woad = new Color(UiTheme.WoadBlue, 0.7f * _highlight);

        // Woven underline growing out from the centre: two thin strands
        // crossing each other, a miniature of the knotwork divider.
        float half = _highlight * (Size.X * 0.5f - 26f);
        if (half > 6f)
        {
            float cx = Size.X * 0.5f;
            float baseY = Size.Y - 8f;
            int steps = Mathf.Max(2, (int)(half / 4f));
            var over = new Vector2[2 * steps + 1];
            var under = new Vector2[2 * steps + 1];
            for (int i = 0; i <= 2 * steps; i++)
            {
                float x = cx - half + half * i / steps;
                float wave = 2.4f * Mathf.Sin((x - cx) * 0.18f);
                over[i] = new Vector2(x, baseY + wave);
                under[i] = new Vector2(x, baseY - wave);
            }
            DrawPolyline(under, woad, 1.3f, true);
            DrawPolyline(over, green, 1.6f, true);
        }

        // Knot spearheads sliding in from the sides — but only when there is
        // clear gutter beside the label. On a narrow button (or a long label)
        // they would stab straight through the text; the art spans ~41px from
        // the edge plus the 14px slide-in, so it needs ~56px of clear side.
        var font = GetThemeFont("font");
        var textWidth = font.GetStringSize(Text, HorizontalAlignment.Center, -1, GetThemeFontSize("font_size")).X;
        if ((Size.X - textWidth) / 2f < 56f)
            return;

        float slide = (1f - _highlight) * 14f;
        DrawSpearhead(new Vector2(20f - slide, Size.Y / 2f), 1, green, woad);
        DrawSpearhead(new Vector2(Size.X - 20f + slide, Size.Y / 2f), -1, green, woad);
    }

    /// <summary>A small filled diamond pointing inward with two curled horns behind it.</summary>
    private void DrawSpearhead(Vector2 at, int dir, Color green, Color woad)
    {
        DrawColoredPolygon(
            [at + new Vector2(-7f * dir, 0), at + new Vector2(0, -5f), at + new Vector2(7f * dir, 0), at + new Vector2(0, 5f)],
            green);
        var hornCenter = at + new Vector2(-12f * dir, 0);
        DrawArc(hornCenter + new Vector2(0, -4f), 4.5f, 0, Mathf.Tau * 0.75f, 16, woad, 1.4f, true);
        DrawArc(hornCenter + new Vector2(0, 4f), 4.5f, Mathf.Tau * 0.25f, Mathf.Tau, 16, woad, 1.4f, true);
    }
}

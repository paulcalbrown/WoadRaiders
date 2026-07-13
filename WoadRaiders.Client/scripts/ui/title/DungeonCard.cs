using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// One dungeon on the selection screen: its floorplan drawn from the real
/// server geometry, the name in blackletter, a tagline, and a censusline
/// (foes, boss). The whole card is a Button sharing the menu kit's
/// green-ignite highlight, identical for mouse hover and keyboard focus.
/// </summary>
public partial class DungeonCard : Button
{
    private const int MapWidth = 620;
    private const int MapHeight = 340;

    /// <summary>Which dungeon this card offers. Set before adding to the tree.</summary>
    public DungeonInfo Info { get; set; }

    private float _highlight;
    private Tween? _tween;
    private bool _hovered;
    private bool _focused;

    public override void _Ready()
    {
        // A Button takes no height from child controls, so the card sizes itself:
        // margins + floorplan + name + tagline + census.
        CustomMinimumSize = new Vector2(MapWidth + 24, 496);
        MouseDefaultCursorShape = CursorShape.PointingHand;

        var idle = new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.04f, 0.06f, 0.75f),
            BorderColor = new Color(UiTheme.WoadDim, 0.35f),
        };
        idle.SetBorderWidthAll(1);
        idle.SetContentMarginAll(12);

        var hot = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.09f, 0.05f, 0.85f),
            BorderColor = new Color(UiTheme.OozeGreen, 0.9f),
            ShadowColor = new Color(UiTheme.OozeGreen, 0.25f), // the radioactive glow
            ShadowSize = 18,
        };
        hot.SetBorderWidthAll(2);
        hot.SetContentMarginAll(12);

        AddThemeStyleboxOverride("normal", idle);
        AddThemeStyleboxOverride("hover", hot);
        AddThemeStyleboxOverride("focus", hot);
        AddThemeStyleboxOverride("pressed", hot);

        BuildContent();

        // Hover takes keyboard focus too: hover and focus ignite identically, so
        // if they could point at different cards, Enter would fire the one that
        // merely LOOKS unselected. With this, Enter always enters the lit card.
        MouseEntered += () => { _hovered = true; GrabFocus(); Retarget(); };
        MouseExited += () => { _hovered = false; Retarget(); };
        FocusEntered += () => { _focused = true; Retarget(); };
        FocusExited += () => { _focused = false; Retarget(); };
        Resized += () => PivotOffset = Size / 2f; // keep the scale pulse centred
    }

    private void BuildContent()
    {
        // The card composes its face from mouse-transparent children, so every
        // click lands on the card itself.
        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);
        column.AddThemeConstantOverride("separation", 8);
        AddChild(column);

        var map = new DungeonMapView
        {
            CustomMinimumSize = new Vector2(MapWidth, MapHeight),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        map.ShowDungeon(Info);
        column.AddChild(map);

        var name = new Label
        {
            Text = Info.Name.ToUpperInvariant(),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        name.AddThemeFontOverride("font", UiTheme.DisplayFont());
        name.AddThemeFontSizeOverride("font_size", 44);
        name.AddThemeColorOverride("font_color", UiTheme.BoneSilver);
        column.AddChild(name);

        var tagline = new Label
        {
            Text = Info.Tagline,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        tagline.AddThemeFontOverride("font", UiTheme.BodyFont());
        tagline.AddThemeFontSizeOverride("font_size", 18);
        tagline.AddThemeColorOverride("font_color", new Color(UiTheme.WoadDim, 0.95f));
        column.AddChild(tagline);

        var (enemies, hasBoss) = map.Census;
        var census = new Label
        {
            Text = $"{enemies} FOES{(hasBoss ? "  ·  A KING BELOW" : "")}",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        census.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 4 });
        census.AddThemeFontSizeOverride("font_size", 15);
        census.AddThemeColorOverride("font_color", new Color(UiTheme.BoneSilver, 0.6f));
        column.AddChild(census);
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
        Scale = Vector2.One * (1f + 0.02f * value);
    }
}

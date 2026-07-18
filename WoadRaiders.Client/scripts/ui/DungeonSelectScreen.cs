using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The dungeon-picking screen between class selection and the raid: one
/// <see cref="DungeonCard"/> per catalog dungeon under the shared Celtic/gothic
/// dress. Choosing a card stores the dungeon in <see cref="ClientConfig"/> and
/// moves on to the raid browser (forge or join an instance); Esc returns to the
/// class picker. Code-first like every screen — the .tscn is just this script
/// on an empty Control.
/// </summary>
public partial class DungeonSelectScreen : Control
{
    public const string ScenePath = "res://screens/DungeonSelect.tscn";

    private readonly Dictionary<DungeonId, DungeonCard> _cards = new();

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();
        BuildUi();

        if (ClientConfig.ScreenshotPath is { } path)
            CaptureScreenshots(path);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel")) // Esc — back to the class picker
            GetTree().ChangeSceneToFile(CharacterSelectScreen.ScenePath);
    }

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        AddChild(new FogBackground());
        AddChild(new CelticFrame());
        MusicJukebox.Instance.PlayTitleTheme(); // carries on seamlessly from the previous screen

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 16);
        center.AddChild(column);

        var heading = new Label
        {
            Text = "CHOOSE YOUR HUNT",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        heading.AddThemeFontOverride("font", UiTheme.DisplayFont());
        heading.AddThemeFontSizeOverride("font_size", 72);
        heading.AddThemeColorOverride("font_color", UiTheme.BoneSilver);
        heading.AddThemeColorOverride("font_outline_color", new Color(0.02f, 0.04f, 0.03f));
        heading.AddThemeConstantOverride("outline_size", 8);
        column.AddChild(heading);

        var divider = new CelticKnotDivider
        {
            CustomMinimumSize = new Vector2(640, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        column.AddChild(divider);

        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 8) }); // spacer

        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        row.AddThemeConstantOverride("separation", 28);
        column.AddChild(row);

        foreach (var info in DungeonCatalog.All)
        {
            var card = new DungeonCard { Info = info };
            card.Pressed += () => Choose(info.Id);
            row.AddChild(card);
            _cards[info.Id] = card;
        }

        var hint = new Label
        {
            Text = "·  ENTER  ·  ESC TURNS BACK  ·",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 6 });
        hint.AddThemeFontSizeOverride("font_size", 18);
        hint.AddThemeColorOverride("font_color", new Color(UiTheme.BoneSilver, 0.5f));
        column.AddChild(hint);

        // Keyboard flow starts on the last dungeon raided (or the Barrow).
        _cards[ClientConfig.Dungeon].GrabFocus();
    }

    private void Choose(DungeonId id)
    {
        ClientConfig.Dungeon = id;
        GetTree().ChangeSceneToFile(RaidSelectScreen.ScenePath);
    }

    /// <summary>Dev helper behind --dungeon-select --screenshot: save a still on the
    /// default focus, another courting the last realm, and exit.</summary>
    private void CaptureScreenshots(string path)
    {
        GetTree().CreateTimer(1.4).Timeout += () =>
        {
            SaveStill(path);
            _cards[DungeonCatalog.All[^1].Id].GrabFocus();
            GetTree().CreateTimer(1.2).Timeout += () =>
            {
                SaveStill(System.IO.Path.ChangeExtension(path, null) + "-2.png");
                GetTree().Quit();
            };
        };
    }

    private void SaveStill(string path) => GetViewport().GetTexture().GetImage().SavePng(path);
}

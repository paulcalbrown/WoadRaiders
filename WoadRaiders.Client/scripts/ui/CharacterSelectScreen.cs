using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The class-picking screen between the title and the dungeon: four
/// <see cref="ClassCard"/>s under the shared Celtic/gothic dress. Choosing a
/// card stores the class in <see cref="ClientConfig"/> and enters the game;
/// Esc returns to the title. Code-first like every screen — the .tscn is just
/// this script on an empty Control.
/// </summary>
public partial class CharacterSelectScreen : Control
{
    public const string ScenePath = "res://screens/CharacterSelect.tscn";

    private readonly Dictionary<CharacterClass, ClassCard> _cards = new();

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();
        BuildUi();

        if (ClientConfig.ScreenshotPath is { } path)
            CaptureScreenshots(path);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel")) // Esc — back to the title
            GetTree().ChangeSceneToFile(TitleScreen.ScenePath);
    }

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        AddChild(new FogBackground());
        AddChild(new CelticFrame());
        MusicJukebox.Instance.PlayTitleTheme(); // carries on seamlessly from the title screen

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 16);
        center.AddChild(column);

        var heading = new Label
        {
            Text = "CHOOSE YOUR RAIDER",
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
        row.AddThemeConstantOverride("separation", 24);
        column.AddChild(row);

        foreach (var cls in new[]
                 { CharacterClass.Knight, CharacterClass.Rogue, CharacterClass.Mage, CharacterClass.Ranger })
        {
            var card = new ClassCard { Class = cls };
            card.Pressed += () => Choose(cls);
            row.AddChild(card);
            _cards[cls] = card;
        }

        var hint = new Label
        {
            Text = "·  ONWARD  ·  ESC TURNS BACK  ·",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 6 });
        hint.AddThemeFontSizeOverride("font_size", 18);
        hint.AddThemeColorOverride("font_color", new Color(UiTheme.BoneSilver, 0.5f));
        column.AddChild(hint);

        // Keyboard flow starts on the last class raided (or the knight).
        _cards[ClientConfig.PlayerClass].GrabFocus();
    }

    private void Choose(CharacterClass cls)
    {
        ClientConfig.PlayerClass = cls;
        GetTree().ChangeSceneToFile(DungeonSelectScreen.ScenePath); // next: pick the hunt
    }

    /// <summary>Dev helper behind --select --screenshot: save a still on the default
    /// focus, another courting the mage, and exit.</summary>
    private void CaptureScreenshots(string path)
    {
        GetTree().CreateTimer(1.4).Timeout += () =>
        {
            SaveStill(path);
            _cards[CharacterClass.Mage].GrabFocus();
            GetTree().CreateTimer(1.2).Timeout += () =>
            {
                SaveStill(System.IO.Path.ChangeExtension(path, null) + "-2.png");
                GetTree().Quit();
            };
        };
    }

    private void SaveStill(string path) => GetViewport().GetTexture().GetImage().SavePng(path);
}

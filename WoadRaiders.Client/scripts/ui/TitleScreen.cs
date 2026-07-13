using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The entry screen: title, player name, server endpoint, Play/Quit. Play stores
/// the choices in <see cref="ClientConfig"/> and switches to the game screen;
/// the game screen returns here on Esc. Launching with <c>--play</c> skips
/// straight into the game (dev/headless convenience); <c>--screenshot</c> saves
/// stills of this screen and exits. Code-first like the rest of the client —
/// the .tscn is just this script on an empty Control; the shared Celtic/gothic
/// dress (theme, fog backdrop, knotwork, glowing menu widgets) lives in
/// scripts/ui/common, and the oozing wordmark in scripts/ui/title.
/// </summary>
public partial class TitleScreen : Control
{
    public const string ScenePath = "res://screens/TitleScreen.tscn";

    private LineEdit _nameEdit = null!;
    private LineEdit _serverEdit = null!;
    private GothicButton _playButton = null!;

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();

        if (ClientConfig.ScreenshotPath is { } screenshotPath)
        {
            BuildUi();
            CaptureScreenshots(screenshotPath);
            return;
        }

        if (ClientConfig.AutoPlay)
        {
            // A scene change can't run while the tree is still mid-add of this
            // scene (_Ready), so defer it to the end of the frame.
            Callable.From(() => GetTree().ChangeSceneToFile(GameScreen.ScenePath)).CallDeferred();
            return;
        }

        BuildUi();
    }

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        AddChild(new FogBackground());
        AddChild(new CelticFrame());
        PlayMusic();

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        center.AddChild(column);

        // First child so the ooze dripping past its box falls behind everything below.
        column.AddChild(new OozeTitle());

        // The drip zone hangs ~125px below the title's layout box; everything
        // else starts under it so no text ever sits behind falling ooze.
        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 130) });

        // Tracked-out small caps in light woad, dark-edged with a faint green
        // halo — legible even against a stray drop.
        var taglineFont = new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 8 };
        var tagline = new Label
        {
            Text = "CO-OP DUNGEON RAIDING",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        tagline.AddThemeFontOverride("font", taglineFont);
        tagline.AddThemeFontSizeOverride("font_size", 24);
        tagline.AddThemeColorOverride("font_color", new Color(0.74f, 0.85f, 1f));
        tagline.AddThemeColorOverride("font_outline_color", new Color(UiTheme.Night, 0.95f));
        tagline.AddThemeConstantOverride("outline_size", 6);
        tagline.AddThemeColorOverride("font_shadow_color", new Color(UiTheme.OozeGreen, 0.30f));
        tagline.AddThemeConstantOverride("shadow_outline_size", 8);
        tagline.AddThemeConstantOverride("shadow_offset_x", 0);
        tagline.AddThemeConstantOverride("shadow_offset_y", 0);
        column.AddChild(tagline);

        var divider = new CelticKnotDivider
        {
            CustomMinimumSize = new Vector2(640, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        column.AddChild(divider);

        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) }); // spacer

        // The menu is narrower than the title, so it gets its own centring.
        var menuCenter = new CenterContainer();
        column.AddChild(menuCenter);
        var menu = new VBoxContainer { CustomMinimumSize = new Vector2(470, 0) };
        menu.AddThemeConstantOverride("separation", 12);
        menuCenter.AddChild(menu);

        _nameEdit = AddField(menu, "Name", ClientConfig.PlayerName);
        _serverEdit = AddField(menu, "Server", ClientConfig.ServerText);
        _serverEdit.TextSubmitted += _ => Play(); // Enter in the server box starts the game

        menu.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) }); // spacer

        _playButton = new GothicButton { Text = "Play" };
        _playButton.Pressed += Play;
        menu.AddChild(_playButton);

        var quit = new GothicButton { Text = "Quit" };
        quit.Pressed += () => GetTree().Quit();
        menu.AddChild(quit);

        _nameEdit.GrabFocus();
    }

    private static LineEdit AddField(VBoxContainer column, string label, string initial)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var caption = new Label
        {
            Text = label.ToUpperInvariant(),
            CustomMinimumSize = new Vector2(92, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        caption.AddThemeFontOverride("font", UiTheme.BodyFont());
        caption.AddThemeFontSizeOverride("font_size", 19);
        caption.AddThemeColorOverride("font_color", new Color(UiTheme.WoadDim, 0.9f));
        row.AddChild(caption);

        var edit = new LineEdit { Text = initial, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        UiTheme.StyleInput(edit);
        row.AddChild(edit);

        column.AddChild(row);
        return edit;
    }

    private void Play()
    {
        // The server sanitizes names (an empty one becomes Raider-<id>), so raw
        // user text is fine to send as-is.
        ClientConfig.PlayerName = _nameEdit.Text.Trim();
        ClientConfig.SetServer(_serverEdit.Text);
        GetTree().ChangeSceneToFile(GameScreen.ScenePath);
    }

    /// <summary>Dev helper behind --screenshot: let the animations run, save a
    /// still, focus Play so the menu highlight shows, save a second, and exit.</summary>
    private void CaptureScreenshots(string path)
    {
        GetTree().CreateTimer(1.3).Timeout += () =>
        {
            SaveStill(path);
            _playButton.GrabFocus();
            GetTree().CreateTimer(2.2).Timeout += () =>
            {
                SaveStill(System.IO.Path.ChangeExtension(path, null) + "-2.png");
                GetTree().Quit();
            };
        };
    }

    private void SaveStill(string path) => GetViewport().GetTexture().GetImage().SavePng(path);

    /// <summary>Loop the title theme, rendered by tools/GenerateTitleMusic.cs.</summary>
    private void PlayMusic() => MusicPlayer.Loop(this, "res://assets/audio/title_theme.wav", -6f);
}

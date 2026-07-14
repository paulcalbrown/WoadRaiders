using Godot;
using WoadRaiders.Shared;

namespace WoadRaiders.Client;

/// <summary>
/// The entry screen: title, player name, server endpoint, Play/Quit. Play stores
/// the choices in <see cref="ClientConfig"/> and moves on to the character-select
/// screen; the game screen returns here on Esc. Launching with <c>--play</c> skips
/// straight into the game and <c>--select</c> straight to the class picker
/// (dev/headless conveniences); <c>--screenshot</c> saves stills of this screen
/// and exits. Code-first like the rest of the client —
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
    private VBoxContainer _menu = null!;

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();

        if (ClientConfig.AutoSelect || ClientConfig.AutoDungeonSelect || ClientConfig.AutoRaidSelect)
        {
            // A scene change can't run while the tree is still mid-add of this
            // scene (_Ready), so defer it to the end of the frame.
            var next = ClientConfig.AutoRaidSelect ? RaidSelectScreen.ScenePath
                : ClientConfig.AutoDungeonSelect ? DungeonSelectScreen.ScenePath
                : CharacterSelectScreen.ScenePath;
            Callable.From(() => GetTree().ChangeSceneToFile(next)).CallDeferred();
            return;
        }

        if (ClientConfig.AutoPlay)
        {
            Callable.From(() => GetTree().ChangeSceneToFile(GameScreen.ScenePath)).CallDeferred();
            return;
        }

        if (ClientConfig.ScreenshotPath is { } screenshotPath)
        {
            BuildUi();
            CaptureScreenshots(screenshotPath);
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
        _menu = new VBoxContainer { CustomMinimumSize = new Vector2(470, 0) };
        _menu.AddThemeConstantOverride("separation", 12);
        menuCenter.AddChild(_menu);

        _nameEdit = AddField(_menu, "Name", ClientConfig.PlayerName);
        _serverEdit = AddField(_menu, "Server", ClientConfig.ServerText);
        _serverEdit.TextSubmitted += _ => Play(); // Enter in the server box starts the game

        _menu.AddChild(new Control { CustomMinimumSize = new Vector2(0, 10) }); // spacer

        _playButton = new GothicButton { Text = "Play" };
        _playButton.Pressed += Play;
        _menu.AddChild(_playButton);

        var quit = new GothicButton { Text = "Quit" };
        quit.Pressed += () => GetTree().Quit();
        _menu.AddChild(quit);

        _nameEdit.GrabFocus();
        CheckForUpdates();
    }

    /// <summary>Ask GitHub whether a newer build exists — fire-and-forget, so the
    /// title screen never waits on the network. Godot's synchronization context
    /// resumes the await on the main thread, where touching the tree is safe.</summary>
    private async void CheckForUpdates()
    {
        var manifest = await UpdateManifest.FetchAsync(ClientConfig.ManifestUrl);
        // The player may have hit Play (or quit) while we were fetching.
        if (manifest is null || !manifest.IsNewerThan(NetConfig.ConnectionKey) || !IsInsideTree())
            return;

        NetConfig.TryParseVersion(manifest.Key, out var released);
        NetConfig.TryParseVersion(NetConfig.ConnectionKey, out var current);

        var notice = new Label
        {
            Text = $"A NEWER BUILD IS OUT — V{released} (THIS IS V{current})",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        notice.AddThemeFontOverride("font", UiTheme.BodyFont());
        notice.AddThemeFontSizeOverride("font_size", 17);
        notice.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.4f)); // amber, like the HUD banner
        _menu.AddChild(notice);

        var get = new GothicButton { Text = "Get the update" };
        get.Pressed += () => OS.ShellOpen(manifest.Page.Length > 0 ? manifest.Page : NetConfig.DownloadUrl);
        _menu.AddChild(get);
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
        GetTree().ChangeSceneToFile(CharacterSelectScreen.ScenePath);
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

    /// <summary>Carry the title theme (rendered by tools/GenerateTitleMusic.cs)
    /// through the jukebox, so it keeps playing unbroken into the next screen.</summary>
    private void PlayMusic() => MusicJukebox.Instance.PlayTitleTheme();
}

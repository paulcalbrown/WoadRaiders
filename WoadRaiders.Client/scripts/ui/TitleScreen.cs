using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The entry screen: title, player name, server endpoint, Play/Quit. Play stores
/// the choices in <see cref="ClientConfig"/> and switches to the game screen;
/// the game screen returns here on Esc. Launching with <c>--play</c> skips
/// straight into the game (dev/headless convenience). Code-first like the rest
/// of the client — the .tscn is just this script on an empty Control.
/// </summary>
public partial class TitleScreen : Control
{
    public const string ScenePath = "res://screens/TitleScreen.tscn";

    private LineEdit _nameEdit = null!;
    private LineEdit _serverEdit = null!;

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();

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

        var background = new ColorRect { Color = new Color(0.015f, 0.015f, 0.025f) }; // matches the dungeon sky
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(background);

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(420, 0) };
        column.AddThemeConstantOverride("separation", 14);
        center.AddChild(column);

        var title = new Label { Text = "WOAD RAIDERS", HorizontalAlignment = HorizontalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 64);
        title.AddThemeColorOverride("font_color", new Color(0.55f, 0.75f, 1f)); // woad blue
        column.AddChild(title);

        var tagline = new Label
        {
            Text = "co-op dungeon raiding",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(1f, 1f, 1f, 0.6f),
        };
        column.AddChild(tagline);

        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) }); // spacer

        _nameEdit = AddField(column, "Name", ClientConfig.PlayerName);
        _serverEdit = AddField(column, "Server", ClientConfig.ServerText);
        _serverEdit.TextSubmitted += _ => Play(); // Enter in the server box starts the game

        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 12) }); // spacer

        var play = new Button { Text = "Play" };
        play.Pressed += Play;
        column.AddChild(play);

        var quit = new Button { Text = "Quit" };
        quit.Pressed += () => GetTree().Quit();
        column.AddChild(quit);

        _nameEdit.GrabFocus();
    }

    private static LineEdit AddField(VBoxContainer column, string label, string initial)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(70, 0) });
        var edit = new LineEdit { Text = initial, SizeFlagsHorizontal = SizeFlags.ExpandFill };
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
}

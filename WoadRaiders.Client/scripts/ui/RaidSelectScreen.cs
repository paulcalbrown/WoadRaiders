using Godot;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Client;

/// <summary>
/// The raid browser between the dungeon choice and the run: it connects to the
/// server, lists the live instances of the chosen dungeon (refreshed while the
/// screen is open), and offers to forge a new one. A player MUST pass through
/// here — every run happens inside a forged-or-joined instance. Choosing stores
/// the decision in <see cref="ClientConfig"/> (Create vs Join) and enters the
/// game; Esc returns to the dungeon picker. Code-first like every screen — the
/// .tscn is just this script on an empty Control.
/// </summary>
public partial class RaidSelectScreen : Control
{
    public const string ScenePath = "res://screens/RaidSelect.tscn";

    /// <summary>One-shot notice shown on arrival — a denied join sets it before
    /// sending the player back here, so they learn why they bounced.</summary>
    public static string? Notice;

    private const double RefreshSeconds = 2.0; // instance-list poll cadence while browsing

    private ClientConnection _connection = null!;
    private VBoxContainer _list = null!;
    private Label _status = null!;
    private LineEdit _nameEdit = null!;
    private GothicButton _forgeButton = null!;
    private string _notice = "";
    private string _listFingerprint = ""; // skip rebuilds (and focus loss) when nothing changed
    private double _refreshIn;

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();
        BuildUi();

        if (Notice is { } notice)
        {
            _notice = notice;
            Notice = null;
        }

        _connection = new ClientConnection(ClientConfig.Host, ClientConfig.Port);
        _connection.Connected += _connection.RequestInstances;
        _connection.InstanceListReceived += RebuildList;
        _connection.Start();

        if (ClientConfig.ScreenshotPath is { } path)
            CaptureScreenshots(path);
    }

    public override void _Process(double delta)
    {
        _connection.Poll(delta);

        // Keep the list fresh while browsing: another party may forge or fill
        // an instance at any moment.
        _refreshIn -= delta;
        if (_refreshIn <= 0 && _connection.State == ConnectionState.Lobby)
        {
            _connection.RequestInstances();
            _refreshIn = RefreshSeconds;
        }

        _status.Text = _connection.State switch
        {
            ConnectionState.Connecting => $"Seeking the warband-fires at {ClientConfig.ServerText} ...",
            ConnectionState.Incompatible => _connection.RefusalMessage ?? "This build is out of date.",
            ConnectionState.Disconnected => _connection.RefusalMessage is { } why
                ? $"{why} Retrying ..."
                : "The server is beyond reach — retrying ...",
            _ => _notice,
        };
    }

    public override void _ExitTree() => _connection.Stop();

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel")) // Esc — back to the dungeon picker
            GetTree().ChangeSceneToFile(DungeonSelectScreen.ScenePath);
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

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(680, 0) };
        column.AddThemeConstantOverride("separation", 14);
        center.AddChild(column);

        var info = DungeonCatalog.Of(ClientConfig.Dungeon);
        var heading = new Label
        {
            Text = info.Name.ToUpperInvariant(),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        heading.AddThemeFontOverride("font", UiTheme.DisplayFont());
        heading.AddThemeFontSizeOverride("font_size", 64);
        heading.AddThemeColorOverride("font_color", UiTheme.BoneSilver);
        heading.AddThemeColorOverride("font_outline_color", new Color(0.02f, 0.04f, 0.03f));
        heading.AddThemeConstantOverride("outline_size", 8);
        column.AddChild(heading);

        var subhead = new Label
        {
            Text = "MUSTER YOUR WARBAND",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subhead.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 8 });
        subhead.AddThemeFontSizeOverride("font_size", 21);
        subhead.AddThemeColorOverride("font_color", new Color(UiTheme.WoadDim, 0.95f));
        column.AddChild(subhead);

        var divider = new CelticKnotDivider
        {
            CustomMinimumSize = new Vector2(640, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        column.AddChild(divider);

        // Forge row: name the raid, light it. Empty name → the server names it.
        var forgeRow = new HBoxContainer();
        forgeRow.AddThemeConstantOverride("separation", 12);
        column.AddChild(forgeRow);

        _nameEdit = new LineEdit
        {
            Text = $"{ClientConfig.PlayerName}'s Raid",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        UiTheme.StyleInput(_nameEdit);
        _nameEdit.TextSubmitted += _ => Forge(); // Enter in the name box lights the raid
        forgeRow.AddChild(_nameEdit);

        _forgeButton = new GothicButton { Text = "New raid", CustomMinimumSize = new Vector2(250, 54) };
        _forgeButton.Pressed += Forge;
        forgeRow.AddChild(_forgeButton);

        var joinLabel = new Label
        {
            Text = "·  OR JOIN A WARBAND ALREADY BELOW  ·",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        joinLabel.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 6 });
        joinLabel.AddThemeFontSizeOverride("font_size", 17);
        joinLabel.AddThemeColorOverride("font_color", new Color(UiTheme.BoneSilver, 0.5f));
        column.AddChild(joinLabel);

        // The live-instance list; RebuildList fills it as replies arrive.
        _list = new VBoxContainer();
        _list.AddThemeConstantOverride("separation", 8);
        column.AddChild(_list);
        ShowEmptyList();

        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        _status.AddThemeFontOverride("font", UiTheme.BodyFont());
        _status.AddThemeFontSizeOverride("font_size", 17);
        _status.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.4f)); // amber, like the HUD banner
        column.AddChild(_status);

        var hint = new Label
        {
            Text = "·  ESC TURNS BACK  ·",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        hint.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 6 });
        hint.AddThemeFontSizeOverride("font_size", 15);
        hint.AddThemeColorOverride("font_color", new Color(UiTheme.BoneSilver, 0.4f));
        column.AddChild(hint);

        _forgeButton.GrabFocus();
    }

    /// <summary>Redraw the joinable-instance list from a server reply, keeping only
    /// this screen's dungeon. Skipped when nothing changed, so the 2 s refresh
    /// doesn't stomp keyboard focus mid-navigation.</summary>
    private void RebuildList(InstanceListPacket packet)
    {
        var mine = packet.Instances.Where(e => e.Dungeon == (byte)ClientConfig.Dungeon).ToArray();

        var fingerprint = string.Join(';', mine.Select(e => $"{e.Id}:{e.Name}:{e.Players}/{e.MaxPlayers}"));
        if (fingerprint == _listFingerprint)
            return;
        _listFingerprint = fingerprint;

        foreach (var child in _list.GetChildren())
            child.QueueFree();

        if (mine.Length == 0)
        {
            ShowEmptyList();
            return;
        }

        foreach (var entry in mine)
        {
            var full = entry.Players >= entry.MaxPlayers;
            var row = new GothicButton
            {
                Text = $"{entry.Name}   ·   {entry.Players}/{entry.MaxPlayers} raiders{(full ? "   ·   FULL" : "")}",
                Disabled = full,
            };
            var id = entry.Id; // capture per row, not the loop variable
            row.Pressed += () => Join(id);
            _list.AddChild(row);
        }
    }

    private void ShowEmptyList()
    {
        var empty = new Label
        {
            Text = "No raids burn below. Forge one.",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        empty.AddThemeFontOverride("font", UiTheme.BodyFont());
        empty.AddThemeFontSizeOverride("font_size", 18);
        empty.AddThemeColorOverride("font_color", new Color(UiTheme.WoadDim, 0.7f));
        _list.AddChild(empty);
    }

    private void Forge()
    {
        ClientConfig.Mode = JoinMode.Create;
        ClientConfig.InstanceName = _nameEdit.Text.Trim(); // the server sanitizes/defaults it
        GetTree().ChangeSceneToFile(GameScreen.ScenePath);
    }

    private void Join(int instanceId)
    {
        ClientConfig.Mode = JoinMode.Join;
        ClientConfig.InstanceId = instanceId;
        GetTree().ChangeSceneToFile(GameScreen.ScenePath);
    }

    /// <summary>Dev helper behind --raid-select --screenshot: one still as the screen
    /// settles (list fetched if a server is up), a second courting the forge button.</summary>
    private void CaptureScreenshots(string path)
    {
        GetTree().CreateTimer(1.6).Timeout += () =>
        {
            SaveStill(path);
            _forgeButton.GrabFocus();
            GetTree().CreateTimer(1.2).Timeout += () =>
            {
                SaveStill(System.IO.Path.ChangeExtension(path, null) + "-2.png");
                GetTree().Quit();
            };
        };
    }

    private void SaveStill(string path) => GetViewport().GetTexture().GetImage().SavePng(path);
}

using Godot;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Client;

/// <summary>
/// The end-of-run screen shown after stepping through the boss portal: the raid
/// and dungeon conquered, the haul carried out (time, gold, relics, the
/// warband's kill tally), and the three roads onward — character select,
/// dungeon select, or the main menu. The <see cref="GameScreen"/> stores the
/// server's <see cref="RunCompletePacket"/> in <see cref="Summary"/> before
/// changing scene. Code-first like every screen — the .tscn is just this
/// script on an empty Control.
/// </summary>
public partial class RunSummaryScreen : Control
{
    public const string ScenePath = "res://screens/RunSummary.tscn";

    /// <summary>The finished run's report; set by the game screen before arriving.
    /// Null only when the scene is run directly from the editor (F6).</summary>
    public static RunCompletePacket? Summary;

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();
        BuildUi();
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel")) // Esc — the main menu
            GetTree().ChangeSceneToFile(TitleScreen.ScenePath);
    }

    private void BuildUi()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        AddChild(new FogBackground());
        AddChild(new CelticFrame());
        MusicPlayer.Loop(this, "res://assets/audio/title_theme.wav", -8f); // back to the hearth-fire theme

        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var column = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        column.AddThemeConstantOverride("separation", 14);
        center.AddChild(column);

        var run = Summary ?? new RunCompletePacket(); // F6 fallback: an empty report

        var heading = new Label
        {
            Text = "THE DEEP IS CONQUERED",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        heading.AddThemeFontOverride("font", UiTheme.DisplayFont());
        heading.AddThemeFontSizeOverride("font_size", 64);
        heading.AddThemeColorOverride("font_color", UiTheme.OozeGreen);
        heading.AddThemeColorOverride("font_outline_color", new Color(0.02f, 0.04f, 0.03f));
        heading.AddThemeConstantOverride("outline_size", 8);
        column.AddChild(heading);

        var dungeon = DungeonCatalog.Of(DungeonCatalog.Sanitize(run.Dungeon));
        var subhead = new Label
        {
            Text = run.RaidName.Length > 0
                ? $"{run.RaidName.ToUpperInvariant()}  ·  {dungeon.Name.ToUpperInvariant()}"
                : dungeon.Name.ToUpperInvariant(),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        subhead.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 8 });
        subhead.AddThemeFontSizeOverride("font_size", 21);
        subhead.AddThemeColorOverride("font_color", new Color(UiTheme.WoadDim, 0.95f));
        column.AddChild(subhead);

        var divider = new CelticKnotDivider
        {
            CustomMinimumSize = new Vector2(620, 44),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        column.AddChild(divider);

        AddStat(column, "TIME IN THE DEEP", FormatDuration(run.DurationSeconds));
        AddStat(column, "GOLD PLUNDERED", run.Gold.ToString());
        AddStat(column, "RELICS CARRIED OUT", run.ItemsLooted.ToString());
        AddStat(column, "FOES FELLED BY THE WARBAND", run.FoesSlain.ToString());

        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 14) }); // spacer

        var characterSelect = new GothicButton { Text = "Character select" };
        characterSelect.Pressed += () => GetTree().ChangeSceneToFile(CharacterSelectScreen.ScenePath);
        column.AddChild(characterSelect);

        var dungeonSelect = new GothicButton { Text = "Dungeon select" };
        dungeonSelect.Pressed += () => GetTree().ChangeSceneToFile(DungeonSelectScreen.ScenePath);
        column.AddChild(dungeonSelect);

        var mainMenu = new GothicButton { Text = "Main menu" };
        mainMenu.Pressed += () => GetTree().ChangeSceneToFile(TitleScreen.ScenePath);
        column.AddChild(mainMenu);

        dungeonSelect.GrabFocus(); // the likeliest next step: raid again
    }

    /// <summary>One summary line: the label in tracked small caps, the value in ooze green.</summary>
    private static void AddStat(VBoxContainer column, string label, string value)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        column.AddChild(row);

        var caption = new Label
        {
            Text = label,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        caption.AddThemeFontOverride("font", new FontVariation { BaseFont = UiTheme.BodyFont(), SpacingGlyph = 4 });
        caption.AddThemeFontSizeOverride("font_size", 19);
        caption.AddThemeColorOverride("font_color", new Color(UiTheme.BoneSilver, 0.75f));
        row.AddChild(caption);

        var amount = new Label
        {
            Text = value,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        amount.AddThemeFontOverride("font", UiTheme.DisplayFont());
        amount.AddThemeFontSizeOverride("font_size", 26);
        amount.AddThemeColorOverride("font_color", UiTheme.OozeGreen);
        row.AddChild(amount);
    }

    private static string FormatDuration(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
}

using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// One class on the character-select screen: a live 3D preview of the KayKit
/// adventurer (idling and slowly turning in its own SubViewport) over the class
/// name, a role line, and stat bars from the real <see cref="ClassArchetypes"/>
/// numbers. The whole card is a Button — mouse hover and keyboard focus share
/// the same green-ignite highlight as the rest of the menu kit, so arrow-key
/// selection looks identical to pointing.
/// </summary>
public partial class ClassCard : Button
{
    private const int PreviewWidth = 320;
    private const int PreviewHeight = 300;

    /// <summary>Which class this card offers. Set before adding to the tree.</summary>
    public CharacterClass Class { get; set; }

    // Stat bars are normalized against fixed caps a notch above the strongest
    // class, so every bar has headroom and the relative reads stay honest.
    private const float HealthCap = 120f;
    private const float SpeedCap = 300f;
    private const float DamageCap = 36f;

    private const string KayKit = "res://addons/kaykit_character_pack_adventures/Characters/gltf";

    // ModelHeight (world units, unscaled) frames the preview camera: migrated
    // characters are authored in metres, KayKit chibis stand ~1.4 with hats.
    private static readonly Dictionary<CharacterClass, (string ScenePath, string Role, float ModelHeight)> Flavor = new()
    {
        [CharacterClass.Warrior] = ("res://assets/characters/Warrior.glb", "Shield-sworn line-holder", 1.85f),
        [CharacterClass.Rogue] = (KayKit + "/Rogue.glb", "Knife-quick shadow", 2.5f),
        [CharacterClass.Mage] = (KayKit + "/Mage.glb", "Wielder of the sickly fire", 2.5f),
        [CharacterClass.Ranger] = (KayKit + "/Rogue_Hooded.glb", "Cold eye, colder bolt", 2.5f),
    };

    private float _highlight;
    private Tween? _tween;
    private bool _hovered;
    private bool _focused;
    private Node3D _turntable = null!;

    public override void _Ready()
    {
        // A Button takes no height from child controls, so the card sizes itself:
        // margins + preview + name + role + three stat rows.
        CustomMinimumSize = new Vector2(PreviewWidth + 24, 520);
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

    public override void _Process(double delta) =>
        _turntable.RotateY((float)delta * (0.5f + 0.9f * _highlight)); // spins up when courted

    private void BuildContent()
    {
        // A Button draws its own text over everything; the card composes its face
        // from child controls instead, all mouse-transparent so clicks reach the card.
        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);
        column.AddThemeConstantOverride("separation", 8);
        AddChild(column);

        column.AddChild(BuildPreview());

        var name = new Label
        {
            Text = Class.ToString().ToUpperInvariant(),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        name.AddThemeFontOverride("font", UiTheme.DisplayFont());
        name.AddThemeFontSizeOverride("font_size", 40);
        name.AddThemeColorOverride("font_color", UiTheme.BoneSilver);
        column.AddChild(name);

        var arch = ClassArchetypes.Of(Class);
        var role = new Label
        {
            Text = $"{Flavor[Class].Role}  ·  {(arch.ProjectileSpeed > 0f ? "RANGED" : "MELEE")}",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        role.AddThemeFontOverride("font", UiTheme.BodyFont());
        role.AddThemeFontSizeOverride("font_size", 17);
        role.AddThemeColorOverride("font_color", new Color(UiTheme.WoadDim, 0.95f));
        column.AddChild(role);

        column.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4), MouseFilter = MouseFilterEnum.Ignore });
        column.AddChild(StatRow("HEALTH", arch.MaxHealth / HealthCap));
        column.AddChild(StatRow("SPEED", arch.MoveSpeed / SpeedCap));
        column.AddChild(StatRow("DAMAGE", arch.AttackDamage / DamageCap));
    }

    private Control BuildPreview()
    {
        // The adventurer idles on a slow turntable in its own little 3D world.
        // OwnWorld3D matters: without it every card's viewport shares the root
        // world and all four models pile up at the origin as one chimera.
        var viewport = new SubViewport
        {
            Size = new Vector2I(PreviewWidth, PreviewHeight),
            TransparentBg = true,
            OwnWorld3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(viewport);

        _turntable = new Node3D();
        var model = GD.Load<PackedScene>(Flavor[Class].ScenePath).Instantiate<Node3D>();
        CharacterLoadout.Apply(model, Class); // just the class's primary weapon, not the whole rack
        _turntable.AddChild(model);
        viewport.AddChild(_turntable);
        model.FindDescendant<AnimationPlayer>()?.Play("Idle");

        // Frame by declared model height so a full-size human and a chibi both
        // fill the card the same way. With the default 75° vertical FOV the
        // visible height at distance d is ~1.53*d; aim for the figure filling
        // ~75% of the frame: d ≈ h / (1.53 * 0.75).
        var h = Flavor[Class].ModelHeight;
        var camera = new Camera3D { Position = new Vector3(0f, 0.58f * h, 0.92f * h) };
        viewport.AddChild(camera);
        camera.LookAt(new Vector3(0f, 0.5f * h, 0f));

        // A warm key light plus a cold woad fill — the game's torch-against-night palette.
        var key = new DirectionalLight3D { LightColor = new Color(1f, 0.85f, 0.65f), LightEnergy = 1.6f };
        viewport.AddChild(key);
        key.RotationDegrees = new Vector3(-35f, 35f, 0f);
        var fill = new DirectionalLight3D { LightColor = new Color(0.5f, 0.65f, 1f), LightEnergy = 0.5f };
        viewport.AddChild(fill);
        fill.RotationDegrees = new Vector3(-20f, -140f, 0f);

        return new TextureRect
        {
            Texture = viewport.GetTexture(),
            CustomMinimumSize = new Vector2(PreviewWidth, PreviewHeight),
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
    }

    /// <summary>A labeled stat bar: dim woad track, radioactive green fill.</summary>
    private static Control StatRow(string caption, float fraction)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 10);

        var label = new Label
        {
            Text = caption,
            CustomMinimumSize = new Vector2(78, 0),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontOverride("font", UiTheme.BodyFont());
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", new Color(UiTheme.WoadDim, 0.9f));
        row.AddChild(label);

        var track = new ColorRect
        {
            Color = new Color(0.06f, 0.09f, 0.13f, 0.9f),
            CustomMinimumSize = new Vector2(0, 10),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        row.AddChild(track);

        var fill = new ColorRect
        {
            Color = new Color(UiTheme.OozeGreen, 0.85f),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        fill.SetAnchorsPreset(LayoutPreset.FullRect);
        fill.AnchorRight = Mathf.Clamp(fraction, 0f, 1f);
        track.AddChild(fill);

        return row;
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
        Scale = Vector2.One * (1f + 0.03f * value);
    }
}

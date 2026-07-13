using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// An animated character. This node holds the world position and is never
/// rotated (so the billboard health bars stay upright); a child pivot yaws for
/// facing; the KayKit model's AnimationPlayer plays idle/run/attack. The view
/// owns its own animation state machine and, for enemies, the chip-trailed
/// billboard health bar.
/// </summary>
public partial class CharacterView : Node3D
{
    public const string DefaultAttackClip = "1H_Melee_Attack_Chop";

    private const string AnimIdle = "Idle";
    private const string AnimRun = "Running_A";
    private const float MoveAnimSpeed = 25f;  // units/s at/above which the run clip plays
    private const float TurnSpeed = 14f;      // facing lerp rate
    private const float VelSmoothRate = 12f;  // facing-velocity smoothing (kills twitch)
    private const float ModelYawOffset = 0f;  // KayKit chars face +Z (glTF convention); flip to Mathf.Pi if they moonwalk
    private const float BarWidth = 40f;       // matches the shared bar QuadMesh width

    // Every character is a moving light source in the dark dungeon: a cheap,
    // shadowless spotlight aimed straight down, so it reads as a circular pool on
    // the ground around the body rather than an omnidirectional sphere. The circle's
    // radius is roughly LightHeight * tan(LightConeAngle); the mount needs some
    // height for the cone to spread that far.
    private const float LightHeight = 60f;      // mount above the feet
    private const float LightRange = 200f;      // cone reach (well past the floor below)
    private const float LightEnergy = 4f;
    private const float LightConeAngle = 50f;   // half-cone degrees; widen for a bigger ground circle

    /// <summary>Play the attack clip this frame? Snapshot flag for remotes, predicted for the local player.</summary>
    public bool Attacking { get; set; }

    /// <summary>Latest authoritative position; remote views ease toward it every frame.</summary>
    public Vector3 Target { get; set; }

    /// <summary>Which swing clip this character plays (per enemy type; players use the default).</summary>
    public string AttackClip { get; set; } = DefaultAttackClip;

    /// <summary>
    /// Ask the character to turn to face this ground-plane direction this frame,
    /// ahead of its movement heading. The local player calls it with the cursor
    /// direction while swinging, so a left click faces the character where you
    /// clicked. Re-request each frame it should apply (<see cref="Animate"/> consumes it).
    /// </summary>
    public void FaceToward(Vector3 direction) => _requestedFacing = direction;

    private Node3D _pivot = null!;      // yaw rotation for facing
    private AnimationPlayer? _anim;
    private MeshInstance3D? _healthFill; // enemies only
    private MeshInstance3D? _healthChip; // enemies only — the lagging "recently lost" trail
    private DamageChip _chip = DamageChip.Full;
    private float _healthFrac = 1f;      // current authoritative health (target for fill + chip)
    private float _barHeight;
    private float _barScale = 1f;
    private Vector3 _lastPos;
    private Vector3 _smoothVel;          // low-passed velocity used for facing (kills reconcile twitch)
    private Vector3? _requestedFacing;   // a direction to face this frame (the cursor while swinging); see FaceToward
    private string _clip = "";
    private float _yaw;                  // the character's current facing angle — always applied to the pivot

    /// <summary>Instantiate the model, snap to its real spot (no lerp-in from the origin), and enter the tree.
    /// A player class, when given, trims the KayKit model down to that class's primary loadout;
    /// enemies pass null and keep their model as authored.</summary>
    public static CharacterView Spawn(Node parent, PackedScene scene, Vector3 feet, float scale, Color lightColor,
                                      CharacterClass? loadout = null)
    {
        var view = new CharacterView { Position = feet, Target = feet, _lastPos = feet };
        view._pivot = new Node3D();
        var model = scene.Instantiate<Node3D>();
        model.Scale = Vector3.One * scale;
        if (loadout is { } cls)
            CharacterLoadout.Apply(model, cls); // hide the rest of the armory
        view._pivot.AddChild(model);
        view.AddChild(view._pivot);

        // Light lives on the (unrotated) holder, not the pivot, so it stays put as the
        // body turns and its height is independent of the model's scale. Aimed straight
        // down for a circular pool on the ground.
        view.AddChild(new SpotLight3D
        {
            Position = new Vector3(0, LightHeight, 0),
            RotationDegrees = new Vector3(-90, 0, 0), // point -Z straight down
            LightColor = lightColor,
            LightEnergy = LightEnergy,
            SpotRange = LightRange,
            SpotAngle = LightConeAngle,
            ShadowEnabled = false, // many characters on screen — keep each light cheap
        });
        parent.AddChild(view);

        view._anim = model.FindDescendant<AnimationPlayer>();
        if (view._anim is not null)
        {
            view._anim.Play(AnimIdle);
            view._clip = AnimIdle;
        }
        return view;
    }

    /// <summary>
    /// Faces the character along its movement and plays idle/run/attack. Replaying a
    /// clip once it finishes makes every clip loop without touching import settings.
    /// </summary>
    public void Animate(double delta)
    {
        var pos = Position;
        var flat = new Vector3(pos.X - _lastPos.X, 0f, pos.Z - _lastPos.Z);
        _lastPos = pos;

        // Low-pass the velocity so per-frame reconciliation micro-corrections don't make the
        // model twitch its facing or flicker between idle/run.
        var frameVel = flat / Mathf.Max((float)delta, 0.0001f);
        _smoothVel = _smoothVel.Lerp(frameVel, Mathf.Clamp((float)delta * VelSmoothRate, 0f, 1f));
        var speed = _smoothVel.Length();

        // The character is always facing _yaw; each frame it eases _yaw toward the
        // direction it should face. That's a requested facing when one was asked for
        // this frame (the local player's cursor while swinging, so a click faces where
        // you clicked), otherwise the movement heading. Standing still with no request,
        // it just holds. The run/idle clip below still keys off real movement, so a
        // requested facing while walking reads as a strafe.
        var moving = speed > MoveAnimSpeed;
        var faceDir = _requestedFacing ?? (moving ? _smoothVel : (Vector3?)null);
        _requestedFacing = null; // consumed — the caller re-requests it each frame it applies
        if (faceDir is { } dir && dir.LengthSquared() > 0.0001f)
        {
            var targetYaw = Mathf.Atan2(dir.X, dir.Z) + ModelYawOffset;
            _yaw = Mathf.LerpAngle(_yaw, targetYaw, Mathf.Clamp((float)delta * TurnSpeed, 0f, 1f));
            _pivot.Rotation = new Vector3(0f, _yaw, 0f);
        }

        if (_anim is null)
            return;

        var desired = Attacking ? AttackClip : moving ? AnimRun : AnimIdle;
        if (desired != _clip || !_anim.IsPlaying())
        {
            _anim.SpeedScale = desired == AttackClip ? AttackSpeedScale(_anim, AttackClip) : 1f;
            _anim.Play(desired);
            _clip = desired;
        }
    }

    /// <summary>Give this view (an enemy) a billboard health bar above its head.</summary>
    public void AttachHealthBar(QuadMesh barMesh, float barHeight, float barScale)
    {
        _barHeight = barHeight;
        _barScale = barScale;

        var bg = new MeshInstance3D
        {
            Mesh = barMesh,
            Position = new Vector3(0, barHeight, 0),
            Scale = new Vector3(barScale, barScale, 1f),
            MaterialOverride = BarMaterial(new Color(0.05f, 0.05f, 0.05f)),
        };
        AddChild(bg);

        // Chip sits between the track and the fill; it lingers where the fill was,
        // marking freshly-lost health, then drains down to meet it.
        _healthChip = new MeshInstance3D
        {
            Mesh = barMesh,
            Position = new Vector3(0, barHeight, 0.1f),
            MaterialOverride = BarMaterial(new Color(1.0f, 0.7f, 0.7f)), // very light red trail
        };
        AddChild(_healthChip);

        _healthFill = new MeshInstance3D
        {
            Mesh = barMesh,
            Position = new Vector3(0, barHeight, 0.2f),
            MaterialOverride = BarMaterial(new Color(0.85f, 0.15f, 0.15f)), // solid red (hostile)
        };
        AddChild(_healthFill);
    }

    /// <summary>Record the authoritative health fraction; a drop arms the chip's linger.</summary>
    public void SetHealthFraction(float frac)
    {
        frac = Mathf.Clamp(frac, 0f, 1f);
        if (frac < _healthFrac)
            _chip.OnDamage(); // took a hit → linger the chip
        _healthFrac = frac;   // fill + chip are rendered per-frame in UpdateBar
    }

    /// <summary>Per-frame: drain the chip toward current health, then place both bars.</summary>
    public void UpdateBar(float delta)
    {
        if (_healthFill is null)
            return;

        _chip.Advance(_healthFrac, delta);

        PlaceBar(_healthChip, _chip.Fraction, 0.1f);
        PlaceBar(_healthFill, _healthFrac, 0.2f);
    }

    /// <summary>
    /// Scale a billboard bar to its fraction and left-anchor it. The quad billboards
    /// (keep-scale), so its width shrinks along the camera's right axis; shifting the
    /// centre left along that SAME axis by half the lost width keeps the left edge fixed
    /// and drains from the right, like the HUD bar. (A world-X shift skews diagonally and
    /// reads as a detached second bar.) Boss bars sit higher and are drawn larger.
    /// </summary>
    private void PlaceBar(MeshInstance3D? bar, float frac, float z)
    {
        if (bar is null)
            return;
        frac = Mathf.Clamp(frac, 0f, 1f);
        bar.Scale = new Vector3(Mathf.Max(frac, 0.001f) * _barScale, _barScale, 1f);
        bar.Position = new Vector3(0f, _barHeight, z)
                       - BarWidth * _barScale * (1f - frac) / 2f * CameraRig.BillboardRight;
    }

    /// <summary>Speed the attack clip so its full swing fits the authoritative attack window.</summary>
    private static float AttackSpeedScale(AnimationPlayer anim, string clip) =>
        anim.HasAnimation(clip)
            ? (float)anim.GetAnimation(clip).Length / SimConstants.AttackAnimDuration
            : 1f;

    private static StandardMaterial3D BarMaterial(Color color) => new()
    {
        AlbedoColor = color,
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
        BillboardKeepScale = true,
    };
}

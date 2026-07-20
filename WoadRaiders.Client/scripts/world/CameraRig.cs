using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The Gauntlet-style chase camera: a perspective camera that rides behind and
/// above the local raider, pitched down at the action, and swings lazily around
/// to stay behind the direction of travel — so running a bend wheels the whole
/// realm around you instead of scrolling a fixed diorama past. The look target
/// eases after the player; the yaw eases after their heading; the boom keeps
/// clear of the terrain so a hillside never swallows the view.
///
/// Also the owner of the frame's camera facts that the rest of the client reads:
/// the live ground-plane forward (input mapping, portal placement, spawn walk)
/// and the live screen-right axis (billboard bar anchoring).
/// </summary>
public partial class CameraRig : Camera3D
{
    /// <summary>The rig's starting heading: facing +X, the way every realm's route leads.</summary>
    public const float DefaultYaw = Mathf.Pi / 2f;

    private const float FieldOfView = 55f;
    private const float PitchDegrees = 40f;  // how steeply the camera looks down
    private const float BoomLength = 430f;   // target-to-camera distance
    private const float AimUpBias = 30f;     // look a little above the feet, not at the dirt
    private const float TargetSmoothing = 8f;
    private const float YawFollowRate = 2.1f;   // how eagerly the camera swings behind travel
    private const float HeadingSpeedMin = 60f;  // ground speed below which the heading holds
    private const float VelSmoothRate = 6f;     // low-pass on the followed velocity
    private const float GroundClearance = 60f;  // the boom never dips closer to the terrain

    /// <summary>
    /// Live screen-right in world space, updated every frame. Billboarded bars
    /// scale along this axis, so they also shift along it to left-anchor.
    /// </summary>
    public static Vector3 BillboardRight { get; private set; } = new(0f, 0f, 1f);

    /// <summary>Live ground-plane camera forward ("into the screen"), updated every
    /// frame — what input mapping, the entrance portal, and the spawn walk read.</summary>
    public static Vector3 LiveGroundForward { get; private set; } = new(1f, 0f, 0f);

    /// <summary>The realm's geometry, once known — lets the boom stay above the terrain.</summary>
    public IDungeonGeometry? Geometry { get; set; }

    private Vector3 _target;
    private Vector3 _smoothVel;
    private float _yaw = DefaultYaw;
    private bool _initialised;

    /// <summary>Ground-plane unit forward for the current yaw.</summary>
    public Vector3 GroundForward => new(Mathf.Sin(_yaw), 0f, Mathf.Cos(_yaw));

    /// <summary>Ground-plane unit right for the current yaw.</summary>
    public Vector3 GroundRight => new(-Mathf.Cos(_yaw), 0f, Mathf.Sin(_yaw));

    public CameraRig()
    {
        Projection = ProjectionType.Perspective;
        Fov = FieldOfView;
        Near = 8f;
        Far = 9000f; // the realm is ~6400 across; the sky closes the rest
        Current = true;
    }

    /// <summary>Ease after <paramref name="target"/> (snaps on the first call), swing
    /// behind its travel heading, and place the camera for this frame.</summary>
    public void Follow(Vector3 target, double delta)
    {
        var dt = (float)delta;
        if (!_initialised)
        {
            _target = target; // snap on the first frame so we don't pan in from the origin
            _initialised = true;
        }
        else if (dt > 0f)
        {
            var newTarget = _target.Lerp(target, Mathf.Clamp(dt * TargetSmoothing, 0f, 1f));
            _smoothVel = _smoothVel.Lerp((newTarget - _target) / dt, Mathf.Clamp(dt * VelSmoothRate, 0f, 1f));
            _target = newTarget;

            // Swing behind the direction of travel. The follow eagerness scales
            // with how forward the motion is: running onward re-centres briskly,
            // a sideways strafe only drifts — so circling an enemy doesn't send
            // the world spinning.
            var flat = new Vector3(_smoothVel.X, 0f, _smoothVel.Z);
            if (flat.Length() > HeadingSpeedMin)
            {
                var heading = Mathf.Atan2(flat.X, flat.Z);
                var alignment = 0.35f + 0.65f * Mathf.Max(0f, flat.Normalized().Dot(GroundForward));
                _yaw = Mathf.LerpAngle(_yaw, heading, Mathf.Clamp(dt * YawFollowRate * alignment, 0f, 1f));
            }
        }

        var pitch = Mathf.DegToRad(PitchDegrees);
        var position = _target
                       - GroundForward * (BoomLength * Mathf.Cos(pitch))
                       + Vector3.Up * (BoomLength * Mathf.Sin(pitch));

        // Never let a hillside swallow the camera: hold the boom above the
        // terrain under it (checked at the camera and halfway along the boom).
        if (Geometry is { } geometry)
        {
            var mid = (position + _target) * 0.5f;
            var floor = Mathf.Max(
                geometry.GroundHeight(position.X, position.Z),
                geometry.GroundHeight(mid.X, mid.Z));
            position.Y = Mathf.Max(position.Y, floor + GroundClearance);
        }

        Position = position;
        LookAt(_target + Vector3.Up * AimUpBias, Vector3.Up);

        BillboardRight = GlobalTransform.Basis.X.Normalized();
        LiveGroundForward = GroundForward;
    }
}

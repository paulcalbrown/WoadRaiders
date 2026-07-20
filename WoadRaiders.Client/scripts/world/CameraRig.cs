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
/// Indoors it comes in with you. An open-sky boom is far taller than any room
/// the crypt can afford, so under a roof the rig FITS itself to the space: it
/// flattens its pitch and draws its boom in until the raider is in plain sight,
/// then springs shorter still if a wall is in the way. There is no room concept
/// to consult — realms are slabs, not volumes — so the fit is measured, by
/// asking the geometry whether it can see the player from where it means to
/// stand. Ducking under a roof is quick (a doorway must not clip); opening back
/// out under the sky is slow, so the view never lurches.
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
    private const float AimUpBias = 30f;     // look a little above the feet, not at the dirt
    private const float TargetSmoothing = 8f;
    private const float YawFollowRate = 2.1f;   // how eagerly the camera swings behind travel
    private const float HeadingSpeedMin = 60f;  // ground speed below which the heading holds
    private const float VelSmoothRate = 6f;     // low-pass on the followed velocity
    private const float GroundClearance = 60f;  // the boom never dips closer to the terrain

    // The two ends of the fit. Open sky is the Crag's camera, unchanged; roofed
    // is the flattest, closest the rig settles at of its own accord — chosen to
    // clear a crypt chamber's ceiling while staying a good five raiders back.
    private const float OpenPitchDegrees = 40f;
    private const float OpenBoomLength = 430f;
    private const float RoofedPitchDegrees = 25f;
    private const float RoofedBoomLength = 250f;

    private const int FitSteps = 8;             // how finely the fit is searched
    private const float FitTightenRate = 14f;   // duck under a roof at once...
    private const float FitLoosenRate = 2.2f;   // ...and rise back out unhurriedly
    private const float MinBoomLength = 120f;   // the spring arm never comes closer than this
    private const float BoomSkin = 20f;         // and stops short of the stone, not against it
    private const int ReelSteps = 6;            // bisections to find the longest clear reach
    private const float FlankProbe = 60f;       // half-width of what counts as worth moving for
    private const float CeilingClearance = 25f; // the rig stays this far under the raider's roof

    /// <summary>
    /// Live screen-right in world space, updated every frame. Billboarded bars
    /// scale along this axis, so they also shift along it to left-anchor.
    /// </summary>
    public static Vector3 BillboardRight { get; private set; } = new(0f, 0f, 1f);

    /// <summary>Live ground-plane camera forward ("into the screen"), updated every
    /// frame — what input mapping, the entrance portal, and the spawn walk read.</summary>
    public static Vector3 LiveGroundForward { get; private set; } = new(1f, 0f, 0f);

    /// <summary>The realm's geometry, once known — lets the boom stay above the terrain.</summary>
    public IRealmGeometry? Geometry { get; set; }

    private Vector3 _target;
    private Vector3 _smoothVel;
    private float _yaw = DefaultYaw;
    private float _fit;  // 0 = open sky, 1 = tucked under a roof
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
        var snapped = !_initialised;
        if (snapped)
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

        var eye = _target + Vector3.Up * AimUpBias;
        var headroom = (Geometry?.CeilingHeight(eye.ToSim()) ?? float.PositiveInfinity) - CeilingClearance;
        var wanted = FitFor(eye, headroom);
        _fit = snapped
            ? wanted // spawning inside the crypt must not pan down through its roof
            : Mathf.Lerp(_fit, wanted,
                         Mathf.Clamp(dt * (wanted > _fit ? FitTightenRate : FitLoosenRate), 0f, 1f));

        Position = Reel(eye, Place(_fit), headroom);
        LookAt(eye, Vector3.Up);

        BillboardRight = GlobalTransform.Basis.X.Normalized();
        LiveGroundForward = GroundForward;
    }

    /// <summary>
    /// Where the rig would stand at a given fit — 0 under open sky, 1 tucked
    /// under a roof — held clear of the ground beneath it so a hillside never
    /// swallows the view.
    /// </summary>
    private Vector3 Place(float fit)
    {
        var pitch = Mathf.DegToRad(Mathf.Lerp(OpenPitchDegrees, RoofedPitchDegrees, fit));
        var boom = Mathf.Lerp(OpenBoomLength, RoofedBoomLength, fit);
        var position = _target
                       - GroundForward * (boom * Mathf.Cos(pitch))
                       + Vector3.Up * (boom * Mathf.Sin(pitch));

        if (Geometry is { } geometry)
        {
            var mid = (position + _target) * 0.5f;
            var floor = Mathf.Max(
                geometry.GroundHeight(position.ToSim()),
                geometry.GroundHeight(mid.ToSim()));
            position.Y = Mathf.Max(position.Y, floor + GroundClearance);
        }
        return position;
    }

    /// <summary>
    /// The loosest fit the rig can stand at — the open-sky boom out on the
    /// Crag, something drawn in and flattened under a crypt roof. Falls back to
    /// the tightest fit when none will do; the spring arm takes it from there.
    /// </summary>
    private float FitFor(Vector3 eye, float headroom)
    {
        if (Geometry is null)
            return 0f;
        for (var step = 0; step < FitSteps; step++)
        {
            var fit = step / (float)(FitSteps - 1);
            if (!Unusable(eye, Place(fit), headroom))
                return fit;
        }
        return 1f;
    }

    /// <summary>
    /// Is this a place the rig must not stand? Two ways to fail: it would climb
    /// out through the raider's roof, or it cannot see them from there.
    /// </summary>
    private bool Unusable(Vector3 eye, Vector3 position, float headroom) =>
        position.Y > headroom || Blocked(eye, position);

    /// <summary>
    /// Is the raider hidden from a camera standing here — by something worth
    /// MOVING for? A pillar is not: the occlusion fader dissolves narrow stone,
    /// and a rig that flinched at every column in the hall of the dead would
    /// never settle. Only obstruction that blocks the boom's flanks too — a
    /// wall, a roof — earns a reel.
    /// </summary>
    private bool Blocked(Vector3 eye, Vector3 position)
    {
        var geometry = Geometry!;
        if (geometry.HasLineOfSight(eye.ToSim(), position.ToSim()))
            return false;
        // Narrow means the boom sees past it on BOTH sides. One clear flank is
        // not enough: pressed against a wall, the probe on the open side sails
        // straight out of the crypt and would call the whole wall a pillar.
        var flank = GroundRight * FlankProbe;
        return !(geometry.HasLineOfSight((eye - flank).ToSim(), (position - flank).ToSim())
              && geometry.HasLineOfSight((eye + flank).ToSim(), (position + flank).ToSim()));
    }

    /// <summary>
    /// The spring arm: draw the boom in along its own line until the rig has a
    /// place it can stand — what saves the view when the raider backs into a
    /// corner, or the roof sits lower than even the tightest fit allows for.
    /// </summary>
    private Vector3 Reel(Vector3 eye, Vector3 position, float headroom)
    {
        if (Geometry is null)
            return position;
        var boom = position - eye;
        var length = boom.Length();
        if (length < 1e-3f || !Unusable(eye, position, headroom))
            return position;

        var dir = boom / length;
        if (Unusable(eye, eye + dir * MinBoomLength, headroom))
            return eye + dir * MinBoomLength; // pinned: nothing along this line will do

        // Bisect for the longest reach that still sees the raider — finer than
        // stepping, and a handful of probes rather than a sweep.
        var clear = MinBoomLength;
        var stuck = length;
        for (var i = 0; i < ReelSteps; i++)
        {
            var mid = (clear + stuck) * 0.5f;
            if (Unusable(eye, eye + dir * mid, headroom))
                stuck = mid;
            else
                clear = mid;
        }
        return eye + dir * Mathf.Max(MinBoomLength, clear - BoomSkin);
    }
}

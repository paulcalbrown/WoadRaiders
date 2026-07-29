using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// The placement math of the Gauntlet-style chase camera: where the camera
/// stands and what it looks at, given the raider it follows and the realm's
/// geometry. Pure System.Numerics — engine-free, like <see cref="OcclusionFade"/>,
/// so the behaviors that make or break the ride (ducking under roofs, reeling
/// off walls, riding terrain) are unit-testable and measurable headlessly
/// (tools/MeasureCameraRide.cs walks the Crypt and scores every frame).
/// The client's CameraRig is a thin Godot adapter over this.
///
/// The camera rides behind and above the raider, pitched down at the action,
/// and swings lazily around to stay behind the direction of travel. Indoors it
/// comes in with you: an open-sky boom is far taller than any room the crypt
/// can afford, so under a roof the rig FITS itself to the space — flattens its
/// pitch and draws its boom in until the raider is in plain sight, then springs
/// shorter still if a wall is in the way. There is no room concept to consult —
/// a realm is loose geometry, not a set of volumes — so the fit is measured,
/// by asking the geometry whether it can see the player from where it means to
/// stand.
///
/// Two layers share the work, on different clocks. The REEL is the reflex: it
/// answers this frame, snaps shorter instantly, and is the only thing trusted
/// to keep the lens out of the stone — backed by a clearance ball around the
/// camera point, because a sight line is thin and a lens is not. The FIT is
/// the framing: it re-poses the shot for the space the raider is in, and since
/// the reel already guards every frame, the fit is allowed to be deliberate —
/// it must see a tighter space HOLD before it commits, so a doorjamb grazing
/// the sight line for a frame no longer nods the whole view. Everything the
/// player sees changes at a bounded rate; everything that keeps stone out of
/// the lens is immediate.
/// </summary>
public sealed class ChaseCamera
{
    /// <summary>The rig's starting heading: facing +X, the way every realm's route leads.</summary>
    public const float DefaultYaw = MathF.PI / 2f;

    /// <summary>The look point sits this far above the followed feet — a little
    /// above the dirt, below the head.</summary>
    public const float AimUpBias = 30f;

    /// <summary>
    /// Radius of the clearance ball a camera position must keep free of stone.
    /// A sight line proves a point can be SEEN from; only clearance proves a
    /// lens can STAND there — the near plane reaches ~7.4 units to its frustum
    /// corner (Near 4, FOV 62, ultrawide), and this holds over twice that,
    /// so a wall beside the camera or a pillar around it never crosses the
    /// image plane. Probed as a cross of segments, not a true ball: three
    /// spans through the point (along the boom, side to side, up and down).
    /// </summary>
    public const float CameraClearance = 16f;

    private const float TargetSmoothing = 8f;
    private const float TargetSmoothingY = 5f;  // stairs are sawtooth ground now; the boom rides the mean, not the treads
    private const float YawFollowRate = 2.1f;   // how eagerly the camera swings behind travel
    private const float HeadingSpeedMin = 60f;  // ground speed below which the heading holds
    private const float VelSmoothRate = 6f;     // low-pass on the followed velocity
    private const float GroundClearance = 60f;  // the boom never dips closer to the terrain

    // The ground clamp is judged instantly but SHOWN eased: the boom-mid floor
    // sample steps discontinuously when the boom crosses a wall top or a crate,
    // and an instant clamp turned every such crossing into a vertical pop. The
    // rise chases a hillside comfortably (running uphill moves the floor ~110
    // u/s; the lag at this rate stays inside GroundClearance); the fall is
    // unhurried, like every other release.
    private const float LiftRiseRate = 8f;
    private const float LiftFallRate = 2f;

    // The two ends of the fit. Open sky is the Crag's camera; roofed is the
    // flattest, closest the rig settles at of its own accord — chosen to clear
    // a crypt chamber's ceiling while staying a good five raiders back.
    // LOAD-BEARING, one way: realms are authored so their ceilings clear the
    // open pose (boom * sin(pitch) + CeilingClearance — the Crypt was built
    // against the 430-boom era's 302 and now has slack; today's law is ~256).
    // Shortening the boom only loosens that constraint; LENGTHENING it past
    // an authored realm's law re-pins the camera through every one of its
    // doors — re-measure with tools/MeasureHeadroom.cs before raising.
    // 360 (from 430) pairs with the client's FOV 62 (from 55) to keep the
    // framed battlefield nearly unchanged while adding parallax and giving
    // walls fewer chances to stand between the lens and the raider.
    public const float OpenPitchDegrees = 40f;
    public const float OpenBoomLength = 360f;
    public const float CeilingClearance = 25f;  // the rig stays this far under the raider's roof
    private const float RoofedPitchDegrees = 25f;
    private const float RoofedBoomLength = 250f;

    private const int FitSteps = 8;             // how finely the fit is searched
    private const float FitTightenRate = 6f;    // re-pose for a tighter space, smoothly...
    private const float FitLoosenRate = 2.2f;   // ...and rise back out slower still
    private const float TightenConfirmSeconds = 0.08f; // a tighter wish must hold this long to move the fit
    private const float MinBoomLength = 120f;   // the spring arm never comes closer than this
    private const float BoomSkin = 20f;         // and stops short of the stone, not against it
    private const int ReelSteps = 6;            // bisections to find the longest clear reach
    private const float ReelRecoverRate = 1.5f; // reach lost to a wall is regained gently
    private const float ReelSweepSpeed = 600f;  // how fast lost reach is given up, absent stone in the lens
    private const float ReelRecoverHold = 0.25f; // reach must be stably clear this long before it's regained
    private const float FlankProbe = 60f;       // half-width of what counts as worth moving for

    /// <summary>Where the camera stands this frame, and the point it looks at.</summary>
    public readonly record struct Frame(Vector3 Position, Vector3 LookTarget);

    /// <summary>The realm's geometry, once known — lets the boom stay above the terrain.</summary>
    public IRealmGeometry? Geometry { get; set; }

    private Vector3 _target;
    private Vector3 _smoothVel;
    private Vector3 _position;   // where the camera stood last frame — its own motion is checked for wall crossings
    private float _yaw = DefaultYaw;
    private float _fit;          // 0 = open sky, 1 = tucked under a roof
    private float _tightenHeld;  // seconds the wish for a tighter fit has held so far
    private float _lift;         // the eased ground-clamp rise actually shown
    private float _boomClamp = float.PositiveInfinity; // the smoothed reach the reel currently allows
    private float _reachClear;   // seconds the full wanted reach has been clear so far
    private bool _initialised;

    /// <summary>Ground-plane unit forward for the current yaw.</summary>
    public Vector3 GroundForward => new(MathF.Sin(_yaw), 0f, MathF.Cos(_yaw));

    /// <summary>Ground-plane unit right for the current yaw.</summary>
    public Vector3 GroundRight => new(-MathF.Cos(_yaw), 0f, MathF.Sin(_yaw));

    /// <summary>Ease after <paramref name="target"/> (snaps on the first call), swing
    /// behind its travel heading, and place the camera for this frame.</summary>
    public Frame Follow(Vector3 target, float dt)
    {
        var snapped = !_initialised;
        if (snapped)
        {
            _target = target; // snap on the first frame so we don't pan in from the origin
            _initialised = true;
        }
        else if (dt > 0f)
        {
            // Vertical follow is slower on purpose: feet track every stair
            // tread exactly, and a camera that follows Y as eagerly as XZ
            // relays the whole sawtooth to the horizon line.
            var t = Math.Clamp(dt * TargetSmoothing, 0f, 1f);
            var newTarget = new Vector3(
                Lerp(_target.X, target.X, t),
                Lerp(_target.Y, target.Y, Math.Clamp(dt * TargetSmoothingY, 0f, 1f)),
                Lerp(_target.Z, target.Z, t));
            _smoothVel = Vector3.Lerp(_smoothVel, (newTarget - _target) / dt, Math.Clamp(dt * VelSmoothRate, 0f, 1f));
            _target = newTarget;

            // Swing behind the direction of travel. The follow eagerness scales
            // with how forward the motion is: running onward re-centres briskly,
            // a sideways strafe only drifts — so circling an enemy doesn't send
            // the world spinning.
            var flat = new Vector3(_smoothVel.X, 0f, _smoothVel.Z);
            if (flat.Length() > HeadingSpeedMin)
            {
                var heading = MathF.Atan2(flat.X, flat.Z);
                var alignment = 0.35f + 0.65f * MathF.Max(0f, Vector3.Dot(Vector3.Normalize(flat), GroundForward));
                _yaw = LerpAngle(_yaw, heading, Math.Clamp(dt * YawFollowRate * alignment, 0f, 1f));
            }
        }

        var eye = _target + Vector3.UnitY * AimUpBias;
        var headroom = (Geometry?.CeilingHeight(eye) ?? float.PositiveInfinity) - CeilingClearance;

        // The fit re-frames; the reel protects. A wish for a tighter fit must
        // HOLD before the pose moves (one-frame sight flickers — a doorjamb, a
        // torch bracket — used to yank it), and while the wish waits, the reel
        // below still keeps this very frame clear.
        var wanted = FitFor(eye, headroom);
        if (snapped)
        {
            _fit = wanted; // spawning inside the crypt must not pan down through its roof
        }
        else if (wanted > _fit + 1e-3f)
        {
            _tightenHeld += dt;
            if (_tightenHeld >= TightenConfirmSeconds)
                _fit = Lerp(_fit, wanted, Math.Clamp(dt * FitTightenRate, 0f, 1f));
        }
        else
        {
            _tightenHeld = 0f;
            _fit = Lerp(_fit, wanted, Math.Clamp(dt * FitLoosenRate, 0f, 1f));
        }

        // Ease the ground clamp into view. Judgment above used the instant
        // clamp; what is SHOWN climbs and settles at a bounded rate, so the
        // floor sample stepping onto a wall top swells the boom over it
        // instead of popping it.
        var desired = Place(_fit, out var wantLift);
        _lift = snapped
            ? wantLift
            : Lerp(_lift, wantLift, Math.Clamp(dt * (wantLift > _lift ? LiftRiseRate : LiftFallRate), 0f, 1f));
        desired.Y += _lift - wantLift;

        // The reel, on two urgencies. A newly-walled-off boom SWEEPS in at a
        // bounded speed — the fader dissolves the wall while the camera dives
        // through the doorway with you, where an instant snap was a 300-unit
        // teleport (the "bounce" a player feels most). Regained reach comes
        // back gentler still. The one thing never shown for even a frame is
        // stone crowding the lens itself: if the swept position is embedded,
        // it snaps the rest of the way to clear air at once.
        var boom = desired - eye;
        var length = boom.Length();
        if (length < 1e-3f)
        {
            _position = desired;
            return new Frame(desired, eye);
        }
        var dir = boom / length;
        var safe = SafeReach(eye, dir, length, headroom);
        if (snapped)
        {
            _boomClamp = safe;
        }
        else if (safe < _boomClamp)
        {
            // Descending a stair shaft the reach is caught, cleared and caught
            // again every few steps; recovering the instant it clears made the
            // camera saw back and forth. Losing reach starts a sweep at once —
            // regaining it waits until the reach has stayed clear a beat.
            _reachClear = 0f;
            _boomClamp = MathF.Max(safe, _boomClamp - ReelSweepSpeed * dt);
        }
        else
        {
            _reachClear += dt;
            if (_reachClear >= ReelRecoverHold)
                _boomClamp = Lerp(_boomClamp, safe, Math.Clamp(dt * ReelRecoverRate, 0f, 1f));
        }

        // Stone in the lens is the one thing never shown for even a frame.
        // Embedded() spots stone crowding the point; the motion test spots the
        // point PASSING INTO stone — a clearance cross is 32 units of probe
        // and a wall is 40 to 80 thick, so a camera buried mid-wall reads
        // clear, but its own path in always crosses the face, the frame it
        // happens and at any thickness.
        var shown = MathF.Min(length, _boomClamp);
        var candidate = eye + dir * shown;
        if (Embedded(eye, candidate) ||
            (!snapped && Geometry is { } g && !g.HasLineOfSight(_position, candidate)))
            _boomClamp = shown = ClearReach(eye, dir, shown);
        _position = eye + dir * shown;
        return new Frame(_position, eye);
    }

    /// <summary>
    /// Where the rig would stand at a given fit — 0 under open sky, 1 tucked
    /// under a roof — held clear of the ground beneath it so a hillside never
    /// swallows the view. <paramref name="lift"/> reports how much of that
    /// height is the ground clamp's doing, so the caller can ease it.
    /// </summary>
    private Vector3 Place(float fit, out float lift)
    {
        var pitch = Lerp(OpenPitchDegrees, RoofedPitchDegrees, fit) * (MathF.PI / 180f);
        var boom = Lerp(OpenBoomLength, RoofedBoomLength, fit);
        var position = _target
                       - GroundForward * (boom * MathF.Cos(pitch))
                       + Vector3.UnitY * (boom * MathF.Sin(pitch));

        lift = 0f;
        if (Geometry is { } geometry)
        {
            var mid = (position + _target) * 0.5f;
            var floor = MathF.Max(
                geometry.GroundHeight(position),
                geometry.GroundHeight(mid));
            lift = MathF.Max(0f, floor + GroundClearance - position.Y);
            position.Y += lift;
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
            if (!Unusable(eye, Place(fit, out _), headroom))
                return fit;
        }
        return 1f;
    }

    /// <summary>
    /// Is this a place the rig must not stand? Three ways to fail: it would
    /// climb out through the raider's roof, it cannot see them from there, or
    /// the stone crowds the lens itself (<see cref="Embedded"/>).
    /// </summary>
    private bool Unusable(Vector3 eye, Vector3 position, float headroom) =>
        position.Y > headroom || Blocked(eye, position) || Embedded(eye, position);

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
        if (geometry.HasLineOfSight(eye, position))
            return false;
        // Narrow means the boom sees past it on BOTH sides. One clear flank is
        // not enough: pressed against a wall, the probe on the open side sails
        // straight out of the crypt and would call the whole wall a pillar.
        var flank = GroundRight * FlankProbe;
        return !(geometry.HasLineOfSight(eye - flank, position - flank)
              && geometry.HasLineOfSight(eye + flank, position + flank));
    }

    /// <summary>
    /// Does stone crowd the camera point itself? The sight-line tests above
    /// prove the raider is visible; they prove nothing about the lens — a
    /// camera can hold a clear sight line while a pillar the fader spared
    /// stands INSIDE it, or a wall runs along the boom close enough to cross
    /// the near plane. Three clearance spans through the point (boom axis,
    /// flank, vertical) keep <see cref="CameraClearance"/> of air around it.
    /// </summary>
    private bool Embedded(Vector3 eye, Vector3 position)
    {
        if (Geometry is not { } geometry)
            return false;
        var boom = position - eye;
        var axis = boom.Length() > 1e-3f ? boom / boom.Length() : new Vector3(1f, 0f, 0f);
        var flank = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, axis)); // boom pitch < 90°, never degenerate
        var up = Vector3.Normalize(Vector3.Cross(axis, flank));
        return !geometry.HasLineOfSight(position - axis * CameraClearance, position + axis * CameraClearance)
            || !geometry.HasLineOfSight(position - flank * CameraClearance, position + flank * CameraClearance)
            || !geometry.HasLineOfSight(position - up * CameraClearance, position + up * CameraClearance);
    }

    /// <summary>
    /// The spring arm's answer for THIS frame: the longest reach along the
    /// boom at which the rig may stand — the full length when the wanted spot
    /// is fine, something shorter when the raider backs into a corner or the
    /// roof sits lower than even the tightest fit allows for. Never less than
    /// <see cref="MinBoomLength"/>: past that the camera would stand in the
    /// raider, and being pinned against stone is the lesser harm.
    /// </summary>
    private float SafeReach(Vector3 eye, Vector3 dir, float length, float headroom)
    {
        if (Geometry is null || !Unusable(eye, eye + dir * length, headroom))
            return length;
        if (Unusable(eye, eye + dir * MinBoomLength, headroom))
            return MinBoomLength; // pinned: nothing along this line will do

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
        return MathF.Max(MinBoomLength, clear - BoomSkin);
    }

    /// <summary>
    /// The nearest reach at or inside <paramref name="from"/> whose clearance
    /// ball is free of stone — where the lens lands NOW when its swept position
    /// turns out embedded (a pillar crossed mid-sweep, a recovery easing back
    /// into the stone it left). Only embedding is tested: a blocked sight line
    /// is the sweep's business, and the fader's, not this snap's.
    /// </summary>
    private float ClearReach(Vector3 eye, Vector3 dir, float from)
    {
        if (Embedded(eye, eye + dir * MinBoomLength))
            return MinBoomLength; // pinned: being crowded is the lesser harm to standing in the raider
        var clear = MinBoomLength;
        var stuck = from;
        for (var i = 0; i < ReelSteps; i++)
        {
            var mid = (clear + stuck) * 0.5f;
            if (Embedded(eye, eye + dir * mid))
                stuck = mid;
            else
                clear = mid;
        }
        return MathF.Max(MinBoomLength, clear - BoomSkin);
    }

    private static float Lerp(float from, float to, float weight) => from + (to - from) * weight;

    /// <summary>Angle interpolation along the shortest arc (Godot's LerpAngle).</summary>
    private static float LerpAngle(float from, float to, float weight)
    {
        var difference = (to - from) % MathF.Tau;
        var distance = 2f * difference % MathF.Tau - difference;
        return from + distance * weight;
    }
}

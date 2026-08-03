using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The chase camera's ride, tested as behaviors: what may never happen for
/// even one frame (stone in the lens, panning through a roof), and what must
/// happen at a bounded rate (reeling in off a wall, riding a ground rise,
/// following feet up a stair). The realm is scripted per test through a fake
/// <see cref="IRealmGeometry"/>; the full-realm dynamics are measured against
/// the real Crypt by tools/MeasureCameraRide.cs.
/// </summary>
public class ChaseCameraTests
{
    private const float Dt = 1f / 60f;

    // With no travel the yaw holds DefaultYaw: forward is +X, the boom points -X.
    private static readonly Vector3 Target = new(1000f, 0f, 1000f);
    private static readonly Vector3 Eye = Target + Vector3.UnitY * ChaseCamera.AimUpBias;

    /// <summary>A realm scripted by lambdas; everything unset keeps the open-arena defaults.</summary>
    private sealed class FakeRealm : IRealmGeometry
    {
        public Func<Vector3, Vector3, bool>? Sight;
        public Func<Vector3, float>? Ground;
        public Func<Vector3, float>? Ceiling;

        public Vector3 SpawnPoint => Vector3.Zero;
        public Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius) => position + delta;
        public bool HasLineOfSight(Vector3 from, Vector3 to) => Sight?.Invoke(from, to) ?? true;
        public float GroundHeight(Vector3 near) => Ground?.Invoke(near) ?? 0f;
        public float CeilingHeight(Vector3 above) => Ceiling?.Invoke(above) ?? float.PositiveInfinity;
    }

    /// <summary>Does the segment cross the plane x = <paramref name="planeX"/>?
    /// (An infinite wall for scripting sight lines; endpoints touching don't count,
    /// matching the soup's open-interval test.)</summary>
    private static bool CrossesX(Vector3 a, Vector3 b, float planeX) =>
        (a.X - planeX) * (b.X - planeX) < 0f;

    private static ChaseCamera.Frame Settle(ChaseCamera camera, Vector3 target, int frames)
    {
        var frame = camera.Follow(target, Dt);
        for (var i = 1; i < frames; i++)
            frame = camera.Follow(target, Dt);
        return frame;
    }

    [Fact]
    public void Open_sky_stands_at_the_open_pose()
    {
        var camera = new ChaseCamera();
        var frame = camera.Follow(Target, Dt);

        var pitch = ChaseCamera.OpenPitchDegrees * MathF.PI / 180f;
        var expected = Eye
                       - new Vector3(1f, 0f, 0f) * (ChaseCamera.OpenBoomLength * MathF.Cos(pitch))
                       + Vector3.UnitY * (ChaseCamera.OpenBoomLength * MathF.Sin(pitch));
        // The look target rides AimUpBias above the feet; with no geometry the
        // pose is exactly the open boom. (Place() measures the boom from the
        // followed point, so the vertical rise is relative to the feet.)
        expected -= Vector3.UnitY * ChaseCamera.AimUpBias;
        Assert.Equal(Eye, frame.LookTarget);
        Assert.True(Vector3.Distance(frame.Position, expected) < 0.5f,
            $"expected the open pose at {expected}, camera stood at {frame.Position}");
    }

    [Fact]
    public void Spawning_under_a_low_roof_never_pans_through_it()
    {
        var ceiling = Eye.Y + 140f; // headroom 115: only the tighter fits can stand
        var realm = new FakeRealm { Ceiling = _ => ceiling };
        var camera = new ChaseCamera { Geometry = realm };

        // The FIRST frame already matters — spawning inside the crypt used to
        // be the case that motivated snapping the fit rather than easing it.
        var frame = camera.Follow(Target, Dt);
        for (var i = 0; i < 300; i++)
        {
            Assert.True(frame.Position.Y <= ceiling - ChaseCamera.CeilingClearance + 0.5f,
                $"frame {i}: camera at Y {frame.Position.Y} broke through the roof at {ceiling}");
            frame = camera.Follow(Target, Dt);
        }
    }

    [Fact]
    public void A_wall_cutting_the_boom_sweeps_the_camera_in_bounded_steps()
    {
        var realm = new FakeRealm();
        var camera = new ChaseCamera { Geometry = realm };
        var settled = Settle(camera, Target, 300);

        // Raise a wall between the camera and the raider (the camera stands at
        // x < eye.X - 300; the wall is well clear of both endpoints).
        var wall = Eye.X - 150f;
        realm.Sight = (a, b) => !CrossesX(a, b, wall);

        var prev = settled.Position;
        var arrived = false;
        for (var i = 0; i < 600; i++)
        {
            var frame = camera.Follow(Target, Dt);
            var step = Vector3.Distance(prev, frame.Position);
            // Bounded: the sweep, plus the one through-the-face snap the frame
            // the camera passes the wall — never a boom-length teleport. The
            // snap's size tracks the boom geometry: ~60.5 at the 260 boom
            // (2026-08-02), which is a face-pass, not a jump.
            Assert.True(step < 70f, $"frame {i}: the camera jumped {step} in one frame");
            prev = frame.Position;
            arrived |= frame.Position.X > wall;
        }
        Assert.True(arrived, "the camera never came through to the raider's side of the wall");
        // And once through, it must not rest embedded against the stone.
        Assert.True(prev.X > wall + 10f, $"the camera settled at x {prev.X}, hugging the wall at {wall}");
    }

    [Fact]
    public void A_grazing_pillar_moves_nothing_the_fader_owns_it()
    {
        var realm = new FakeRealm();
        var camera = new ChaseCamera { Geometry = realm };
        var settled = Settle(camera, Target, 300);

        // A narrow pillar: it clips only the exact centre sight line; the
        // flank and clearance probes (offset in Z or Y) all pass beside it.
        realm.Sight = (a, b) => !(CrossesX(a, b, Eye.X - 150f)
                                  && MathF.Abs(a.Z - Eye.Z) < 1f && MathF.Abs(b.Z - Eye.Z) < 1f
                                  && a.Y < Eye.Y + 200f);

        var frame = camera.Follow(Target, Dt);
        Assert.True(Vector3.Distance(frame.Position, settled.Position) < 1f,
            "a pillar the occlusion fader dissolves must not move the camera");
    }

    [Fact]
    public void The_camera_is_never_within_clearance_of_a_wall_it_crosses()
    {
        var realm = new FakeRealm();
        var camera = new ChaseCamera { Geometry = realm };
        Settle(camera, Target, 300);

        // Back the raider up so the boom pushes at (and past) a wall standing
        // just behind the settled camera — but never so far that the eye itself
        // reaches min boom of it, which is the pinned concession, not a hole.
        var wall = Eye.X - 340f;
        realm.Sight = (a, b) => !CrossesX(a, b, wall);
        for (var i = 0; i < 600; i++)
        {
            var back = MathF.Min(i * 2f, 180f);
            var frame = camera.Follow(Target - new Vector3(back, 0f, 0f), Dt);
            // The invariant that matters is the image plane: the near frustum
            // reaches ~6.6 units from the camera point (Near 4, FOV 55), and
            // stone must never cross it. (The clearance probes hold a wider
            // ball wherever the camera RESTS; mid-sweep it may pass closer.)
            var nearWall = MathF.Abs(frame.Position.X - wall) < 8f;
            Assert.False(nearWall,
                $"frame {i}: camera at x {frame.Position.X} put the wall at {wall} into the near plane");
        }
    }

    [Fact]
    public void A_ground_rise_under_the_boom_lifts_the_camera_smoothly()
    {
        var realm = new FakeRealm();
        var camera = new ChaseCamera { Geometry = realm };
        var settled = Settle(camera, Target, 300);

        // The boom's floor sample steps up 300 at once — a wall top sliding
        // under the boom mid (the open pose already rides 276 up, so this
        // demands a real lift). The old instant clamp popped the camera the
        // full rise in a single frame.
        realm.Ground = _ => 300f;

        var prev = settled.Position;
        var frame = prev;
        for (var i = 0; i < 600; i++)
        {
            frame = camera.Follow(Target, Dt).Position;
            Assert.True(frame.Y - prev.Y < 25f,
                $"frame {i}: the camera popped {frame.Y - prev.Y} up in one frame");
            prev = frame;
        }
        Assert.True(frame.Y >= 300f + 55f, $"the camera never rose over the new floor (Y {frame.Y})");
    }

    [Fact]
    public void Stair_treads_reach_the_horizon_attenuated()
    {
        var camera = new ChaseCamera();
        Settle(camera, Target, 300);

        // Feet jump a full tread at once; the camera's vertical follow is the
        // slow axis, so the first-frame response must be a fraction of it.
        var stepped = Target + Vector3.UnitY * 16f;
        var before = camera.Follow(Target, Dt).Position;
        var after = camera.Follow(stepped, Dt).Position;
        Assert.True(MathF.Abs(after.Y - before.Y) < 3f,
            $"a 16-unit tread moved the camera {after.Y - before.Y} in one frame");
    }

    [Fact]
    public void Lost_reach_returns_gently_after_a_squeeze()
    {
        var realm = new FakeRealm();
        var camera = new ChaseCamera { Geometry = realm };
        Settle(camera, Target, 300);

        // Squeeze: a wall just outside min boom for a spell, then gone.
        var wall = Eye.X - 160f;
        realm.Sight = (a, b) => !CrossesX(a, b, wall);
        Settle(camera, Target, 300);
        realm.Sight = null;

        var prev = camera.Follow(Target, Dt).Position;
        for (var i = 0; i < 600; i++)
        {
            var frame = camera.Follow(Target, Dt);
            var step = Vector3.Distance(prev, frame.Position);
            Assert.True(step < 15f, $"frame {i}: recovery jumped {step} in one frame");
            prev = frame.Position;
        }
        // And it does recover — back out toward the open pose.
        Assert.True(Vector3.Distance(prev, Eye) > ChaseCamera.OpenBoomLength * 0.9f,
            $"the boom never re-extended (still {Vector3.Distance(prev, Eye)} from the eye)");
    }
}

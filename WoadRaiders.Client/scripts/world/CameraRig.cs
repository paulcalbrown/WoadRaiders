using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The Gauntlet-style chase camera, as a Godot node: a perspective camera whose
/// placement each frame — riding behind the raider, fitting itself under roofs,
/// reeling in off walls — is decided by the engine-free <see cref="ChaseCamera"/>
/// solver in Core (where that behavior is unit-tested and measured headlessly).
/// This node feeds the solver and applies its answer to the scene.
///
/// Also the owner of the frame's camera facts that the rest of the client reads:
/// the live ground-plane forward (input mapping, portal placement, spawn walk)
/// and the live screen-right axis (billboard bar anchoring).
/// </summary>
public partial class CameraRig : Camera3D
{
    /// <summary>The rig's starting heading: facing +X, the way every realm's route leads.</summary>
    public const float DefaultYaw = ChaseCamera.DefaultYaw;

    // 56 pairs with the solver's OpenBoomLength 260 (the 2026-08-02 close-up
    // for the full-size Warrior era; the 62/360 and 55/430 pairings framed the
    // wider chibi battlefield). Narrower glass + shorter boom ≈ +35% character
    // on screen versus the 62/300 step before it.
    private const float FieldOfView = 56f;

    private readonly ChaseCamera _solver = new();

    /// <summary>
    /// Live screen-right in world space, updated every frame. Billboarded bars
    /// scale along this axis, so they also shift along it to left-anchor.
    /// </summary>
    public static Vector3 BillboardRight { get; private set; } = new(0f, 0f, 1f);

    /// <summary>Live ground-plane camera forward ("into the screen"), updated every
    /// frame — what input mapping, the entrance portal, and the spawn walk read.</summary>
    public static Vector3 LiveGroundForward { get; private set; } = new(1f, 0f, 0f);

    /// <summary>The realm's geometry, once known — lets the boom stay above the terrain.</summary>
    public IRealmGeometry? Geometry
    {
        get => _solver.Geometry;
        set => _solver.Geometry = value;
    }

    /// <summary>Ground-plane unit forward for the current yaw.</summary>
    public Vector3 GroundForward => _solver.GroundForward.ToGodot();

    /// <summary>Ground-plane unit right for the current yaw.</summary>
    public Vector3 GroundRight => _solver.GroundRight.ToGodot();

    public CameraRig()
    {
        Projection = ProjectionType.Perspective;
        Fov = FieldOfView;
        Near = 4f;   // paired with the solver's clearance ball: the near-frustum
                     // corner reaches ~7.4 units, well inside CameraClearance
        Far = 9000f; // the realm is ~6400 across; the sky closes the rest
        Current = true;
    }

    /// <summary>Ease after <paramref name="target"/> (snaps on the first call), swing
    /// behind its travel heading, and place the camera for this frame.</summary>
    public void Follow(Vector3 target, double delta)
    {
        var frame = _solver.Follow(target.ToSim(), (float)delta);
        Position = frame.Position.ToGodot();
        LookAt(frame.LookTarget.ToGodot(), Vector3.Up);

        BillboardRight = GlobalTransform.Basis.X.Normalized();
        LiveGroundForward = GroundForward;
    }
}

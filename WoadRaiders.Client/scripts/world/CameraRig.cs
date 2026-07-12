using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The fixed-angle isometric camera. It smooths the LOOK-TARGET (not the camera
/// position) and keeps a constant offset from it — the camera-to-target vector
/// never changes, so the camera translates to follow the player but never
/// rotates or tilts. Also the single owner of the camera's fixed geometry, which
/// billboarding (bars) and occlusion fading derive from.
/// </summary>
public partial class CameraRig : Camera3D
{
    /// <summary>Fixed world offset from the look target: 45° yaw, ~40° pitch.</summary>
    public static readonly Vector3 Offset = new(600f, 700f, 600f);

    /// <summary>Unit direction from any world point toward the camera (the sight-ray direction).</summary>
    public static readonly Vector3 ToCamera = Offset.Normalized();

    /// <summary>
    /// World-space "screen right" for the fixed iso camera (= Camera3D basis.X = up × viewZ).
    /// Billboarded bars scale along this axis, so they also shift along it to left-anchor.
    /// </summary>
    public static readonly Vector3 BillboardRight = Vector3.Up.Cross(ToCamera).Normalized();

    private const float OrthoSize = 600f; // ortho view height in world units at the 1080 base; lower = more zoomed in
    private const float Smoothing = 8f;

    private Vector3 _target;
    private bool _initialised;

    public CameraRig()
    {
        Projection = ProjectionType.Orthogonal;
        Size = OrthoSize;
        Current = true;
        Position = Offset;
    }

    /// <summary>Ease the look target toward <paramref name="target"/>; snaps on the first call.</summary>
    public void Follow(Vector3 target, double delta)
    {
        if (!_initialised)
        {
            _target = target; // snap on the first frame so we don't pan in from the origin
            _initialised = true;
        }
        else
        {
            _target = _target.Lerp(target, Mathf.Clamp((float)delta * Smoothing, 0f, 1f));
        }

        Position = _target + Offset;
        LookAt(_target, Vector3.Up);
    }
}

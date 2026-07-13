using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// A transient "move here" marker: a red X on the ground that shrinks and fades,
/// then frees itself. Dropped where the player right-clicks to path. Self-animating
/// and fire-and-forget — it manages its own lifetime, so nothing tracks it.
/// </summary>
public partial class MoveMarker : Node3D
{
    private const float Lifetime = 0.5f;
    private const float StartSpan = 44f;     // width of the X at spawn, in world units
    private const float GroundOffset = 1.5f; // above the floor, to avoid z-fighting

    private StandardMaterial3D _material = null!;
    private float _age;

    /// <summary>Drop a marker at the clicked ground point.</summary>
    public static void Spawn(Node parent, Vector3 groundPoint)
    {
        var marker = new MoveMarker
        {
            Position = new Vector3(groundPoint.X, groundPoint.Y + GroundOffset, groundPoint.Z),
        };
        parent.AddChild(marker);
    }

    public override void _Ready()
    {
        _material = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.15f, 0.15f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, // reads clearly on the dark floor
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };
        // Two flat bars crossed at right angles form an X on the ground plane.
        AddChild(MakeBar(Mathf.Pi / 4f));
        AddChild(MakeBar(-Mathf.Pi / 4f));
    }

    private MeshInstance3D MakeBar(float yaw) => new()
    {
        Mesh = new BoxMesh { Size = new Vector3(StartSpan, 1.5f, StartSpan * 0.16f) },
        MaterialOverride = _material,
        Rotation = new Vector3(0f, yaw, 0f),
    };

    public override void _Process(double delta)
    {
        _age += (float)delta;
        var t = _age / Lifetime;
        if (t >= 1f)
        {
            QueueFree();
            return;
        }

        Scale = Vector3.One * (1f - t); // shrink away
        var c = _material.AlbedoColor;
        _material.AlbedoColor = new Color(c.R, c.G, c.B, 1f - t * 0.5f); // gentle fade
    }
}

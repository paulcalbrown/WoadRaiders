using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// A standing dungeon portal: a ring of light around a dark void disc, tilted
/// inner rings precessing around the mouth like a gyroscope, sparks swirling
/// with them, and a glow pooling on the floor. Two wear it in different colours —
/// the green EXIT torn open when the boss falls (<see cref="WorldView"/> spawns
/// it from the snapshot; walking in ends the run), and the blue ENTRANCE standing
/// at the dungeon mouth where raiders arrive (<see cref="DungeonVisualBuilder"/>
/// stands it at the spawn; purely a landmark). Set <see cref="Tint"/> before
/// adding it to the tree. Entirely code-built like the rest of the world views:
/// it scales in when it spawns, churns in its own plane (the gate itself stands
/// still, angled toward the fixed iso camera), and its light breathes.
/// </summary>
public partial class PortalView : Node3D
{
    private const float RingRadius = 46f;   // the mouth, sized to dwarf a ~49-unit raider
    private const float RingThickness = 7f;
    private const float CenterHeight = 58f; // ring centre above the floor
    private const float ScaleInSeconds = 0.7f;

    // The portal faces the fixed isometric camera (offset 600,700,600 → 45° on
    // the ground plane), so the mouth always reads as a mouth, never edge-on.
    private const float FaceCameraYawDegrees = 45f;

    /// <summary>The portal's colour. Green (default) = the boss exit; woad blue =
    /// the entrance. Set in the object initializer, before the node enters the tree.</summary>
    public Color Tint { get; init; } = UiTheme.OozeGreen;

    private OmniLight3D _light = null!;
    private Node3D _rotorA = null!; // tilted inner rings; precessing them around
    private Node3D _rotorB = null!; // the portal's axis is the visible rotation
    private double _age;

    public override void _Ready()
    {
        RotationDegrees = new Vector3(0, FaceCameraYawDegrees, 0);

        // The ring: a vertical torus, unshaded and emissive so it burns through
        // the dungeon gloom.
        var ring = new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = RingRadius - RingThickness, OuterRadius = RingRadius + RingThickness },
            Position = new Vector3(0, CenterHeight, 0),
            RotationDegrees = new Vector3(90, 0, 0), // torus lies flat by default; stand it up
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Tint,
                EmissionEnabled = true,
                Emission = Tint,
                EmissionEnergyMultiplier = 2.5f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(ring);

        // The void inside the mouth: a squashed dark sphere in a deep shade of the
        // tint, translucent enough that the chamber glimmers through.
        var voidDisc = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = RingRadius - 2f, Height = (RingRadius - 2f) * 2f },
            Position = new Vector3(0, CenterHeight, 0),
            Scale = new Vector3(1f, 1f, 0.10f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(Tint.Darkened(0.9f), 0.82f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                EmissionEnabled = true,
                Emission = Tint,
                EmissionEnergyMultiplier = 0.35f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(voidDisc);

        // Two thin rings tilted off the portal plane, counter-precessing around
        // its axis — the churn that makes the portal read as ROTATING without
        // the whole gate spinning like a top. Their arcs dip in and out of the
        // void disc as they turn.
        _rotorA = MakeRotor(tiltDegrees: 14f, radius: RingRadius - 11f, thickness: 2.8f);
        _rotorB = MakeRotor(tiltDegrees: -9f, radius: RingRadius - 21f, thickness: 2.2f);

        // Sparks swirling around the mouth. The emitter is pitched so its local
        // Y is the portal's normal: the ring emission then sits in the portal
        // plane and orbit velocity swirls the sparks around the mouth.
        var swirl = new GpuParticles3D
        {
            Position = new Vector3(0, CenterHeight, 0),
            RotationDegrees = new Vector3(90, 0, 0),
            Amount = 80,
            Lifetime = 1.6,
            DrawPass1 = new SphereMesh { Radius = 2.6f, Height = 5.2f, RadialSegments = 6, Rings = 3 },
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
                EmissionRingAxis = Vector3.Up, // local Y = the portal normal after the pitch
                EmissionRingRadius = RingRadius,
                EmissionRingInnerRadius = RingRadius - 10f,
                EmissionRingHeight = 4f,
                OrbitVelocityMin = 0.45f,
                OrbitVelocityMax = 0.85f,
                Gravity = Vector3.Zero,
                ScaleMin = 0.5f,
                ScaleMax = 1.3f,
                Color = Tint,
            },
        };
        // Unshaded emissive sparks — vertex color carries the tint.
        swirl.MaterialOverride = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            EmissionEnabled = true,
            Emission = Tint,
            EmissionEnergyMultiplier = 2f,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        AddChild(swirl);

        // The glow that pools on the floor and walls around the mouth.
        _light = new OmniLight3D
        {
            Position = new Vector3(0, CenterHeight, 0),
            LightColor = Tint,
            LightEnergy = 5f,
            OmniRange = 320f,
            ShadowEnabled = false,
        };
        AddChild(_light);

        Scale = Vector3.One * 0.05f; // torn open from a spark — _Process scales it in
    }

    /// <summary>A thin ring tilted off the portal plane, hung on its own pivot at the
    /// mouth's centre. Rotating the pivot around local Z (the portal's axis) sweeps the
    /// tilt around — a precession the eye reads as the portal churning.</summary>
    private Node3D MakeRotor(float tiltDegrees, float radius, float thickness)
    {
        var rotor = new Node3D { Position = new Vector3(0, CenterHeight, 0) };
        rotor.AddChild(new MeshInstance3D
        {
            Mesh = new TorusMesh { InnerRadius = radius - thickness, OuterRadius = radius + thickness },
            RotationDegrees = new Vector3(90 + tiltDegrees, 0, 0), // stood up, then tipped off-plane
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(Tint, 0.85f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                EmissionEnabled = true,
                Emission = Tint,
                EmissionEnergyMultiplier = 1.6f,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        });
        AddChild(rotor);
        return rotor;
    }

    public override void _Process(double delta)
    {
        _age += delta;

        // Scale in with an ease-out, then hold.
        var t = Mathf.Clamp((float)(_age / ScaleInSeconds), 0f, 1f);
        var eased = 1f - Mathf.Pow(1f - t, 3f);
        Scale = Vector3.One * Mathf.Lerp(0.05f, 1f, eased);

        // All rotation happens in the portal's own plane: the tilted rings
        // counter-precess around its axis (local Z = the mouth's normal).
        _rotorA.RotateZ((float)delta * 1.5f);
        _rotorB.RotateZ((float)delta * -1.0f);
        _light.LightEnergy = 5f + Mathf.Sin((float)_age * 2.2f) * 1.4f; // the glow breathes
    }
}

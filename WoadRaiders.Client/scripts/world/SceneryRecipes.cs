using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// A LIBRARY of ready-made scenery, offered to any realm that wants it. Nothing
/// obliges a realm to use these, and nothing but the realm's own .tscn ever
/// renders them: a design builds them here, hangs them off the scene, and the
/// nodes are serialized into the map file. Two realms may look nothing alike.
///
/// Scenery is invisible to the simulation. It carries no group, no collision,
/// no marker name, so the bake walks straight past it — which is why a design
/// may place as much of it as it likes without touching the served geometry.
/// </summary>
public static class SceneryRecipes
{
    /// <summary>A burning waymarker: a dark iron bowl, a warm pool of light, and a
    /// billboarded flame — the realm's landmarks after dusk.</summary>
    public static Node3D MakeBrazier(Vector3 position)
    {
        var brazier = new Node3D { Position = position };

        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.09f, 0.09f), Roughness = 0.9f };
        brazier.AddChild(new MeshInstance3D
        {
            Name = "Bowl",
            Mesh = new CylinderMesh { TopRadius = 15f, BottomRadius = 9f, Height = 26f, RadialSegments = 10, Material = iron },
            Position = new Vector3(0f, 13f, 0f),
        });

        brazier.AddChild(new OmniLight3D
        {
            Name = "Ember",
            Position = new Vector3(0f, 46f, 0f),
            LightColor = new Color(1.0f, 0.62f, 0.30f),
            LightEnergy = 6f,
            OmniRange = 380f,
            ShadowEnabled = false, // dozens of braziers — keep each cheap
        });

        var flame = MakeFlame(new Vector3(0f, 28f, 0f));
        flame.Name = "Flame";
        brazier.AddChild(flame);
        return brazier;
    }

    /// <summary>The proven torch-flame recipe (tall tapering embers, billboarded,
    /// preprocessed so it burns from the first frame), built in code. Public
    /// because a design lighting its own scenery wants exactly this.</summary>
    public static GpuParticles3D MakeFlame(Vector3 position)
    {
        var colorRamp = new Gradient();
        colorRamp.SetColor(0, new Color(0.9f, 0.16f, 0.04f));
        colorRamp.SetColor(1, new Color(0.35f, 0.01f, 0.005f, 0f));
        colorRamp.AddPoint(0.45f, new Color(0.72f, 0.06f, 0.02f)); // after the ends — AddPoint reindexes

        var scaleCurve = new Curve();
        scaleCurve.AddPoint(new Vector2(0f, 1f));
        scaleCurve.AddPoint(new Vector2(0.5f, 0.4f));
        scaleCurve.AddPoint(new Vector2(1f, 0f));

        var dot = new Gradient();
        dot.SetColor(0, Colors.White);
        dot.SetColor(1, new Color(1f, 1f, 1f, 0f));
        dot.AddPoint(0.6f, Colors.White); // after the ends — AddPoint reindexes

        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 3f,
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 5f,
            Gravity = new Vector3(0f, 6f, 0f),
            InitialVelocityMin = 34f,
            InitialVelocityMax = 52f,
            ScaleMin = 9f,
            ScaleMax = 15f,
            ScaleCurve = new CurveTexture { Curve = scaleCurve },
            Color = Colors.White,
            ColorRamp = new GradientTexture1D { Gradient = colorRamp },
        };

        var flameMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = new GradientTexture2D
            {
                Gradient = dot, Width = 32, Height = 32,
                Fill = GradientTexture2D.FillEnum.Radial,
                FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
            },
        };

        return new GpuParticles3D
        {
            Position = position,
            Amount = 18,
            Lifetime = 0.6f,
            Randomness = 0.4f,
            Preprocess = 0.6f,
            ProcessMaterial = process,
            DrawPass1 = new QuadMesh { Material = flameMaterial, Size = new Vector2(1f, 1f) },
        };
    }
}

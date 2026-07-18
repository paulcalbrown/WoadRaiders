using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The realm's shared dressing recipes — the dusk sky and light rig, the
/// burning brazier, weathered stone — used by BOTH the client's from-geometry
/// fallback renderer (<see cref="DungeonVisualBuilder"/>) and the offline
/// scene builder (<see cref="RealmSceneBuilder"/>) that bakes generated realms
/// into natural, script-free .tscn files. One set of values, one look,
/// wherever the realm is stood up.
/// </summary>
public static class RealmDressing
{
    /// <summary>An open dusk over the highland: procedural sky, sky ambient,
    /// and distance fog to sink the far crags into.</summary>
    public static Godot.Environment RealmEnvironment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Sky,
        Sky = new Sky
        {
            SkyMaterial = new ProceduralSkyMaterial
            {
                SkyTopColor = new Color(0.09f, 0.12f, 0.22f),
                SkyHorizonColor = new Color(0.46f, 0.28f, 0.22f), // dusk ember at the rim
                GroundBottomColor = new Color(0.05f, 0.05f, 0.07f),
                GroundHorizonColor = new Color(0.30f, 0.20f, 0.17f),
                SunAngleMax = 30f,
                SunCurve = 0.6f,
            },
        },
        AmbientLightSource = Godot.Environment.AmbientSource.Sky,
        AmbientLightEnergy = 0.55f,
        // Gentle depth fog: sinks the far crags, leaves the brazier pools alone.
        FogEnabled = true,
        FogLightColor = new Color(0.23f, 0.20f, 0.24f),
        FogDensity = 0.00016f,
        FogSkyAffect = 0.25f,
    };

    /// <summary>The setting sun: low, warm, and the realm's only shadow-caster.</summary>
    public static DirectionalLight3D MakeSun() => new()
    {
        RotationDegrees = new Vector3(-26f, -40f, 0f),
        LightColor = new Color(1.0f, 0.80f, 0.58f),
        LightEnergy = 1.05f,
        ShadowEnabled = true,
        DirectionalShadowMaxDistance = 2400f, // shadows near the action; the fog owns the distance
    };

    /// <summary>The cold woad counter-glow opposite the sun.</summary>
    public static DirectionalLight3D MakeFill() => new()
    {
        RotationDegrees = new Vector3(-32f, 145f, 0f),
        LightColor = new Color(0.55f, 0.62f, 0.85f),
        LightEnergy = 0.18f,
    };

    /// <summary>Weathered stone for the realm's solids: seamless world-triplanar
    /// noise, no vertex-colour tricks — safe for authored scenes and the
    /// occlusion fader's transparency path alike.</summary>
    public static StandardMaterial3D StoneMaterial()
    {
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.21f, 0.20f, 0.22f));
        ramp.SetColor(1, new Color(0.38f, 0.37f, 0.41f));

        var albedoNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.05f, Seed = 3 };
        var albedo = new NoiseTexture2D { Noise = albedoNoise, Width = 256, Height = 256, Seamless = true, ColorRamp = ramp };

        var normalNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.08f, Seed = 4 };
        var normal = new NoiseTexture2D
        {
            Noise = normalNoise, Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 3f,
        };

        return new StandardMaterial3D
        {
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            Roughness = 0.95f,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.012f, 0.012f, 0.012f),
        };
    }

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
    /// preprocessed so it burns from the first frame), built in code.</summary>
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

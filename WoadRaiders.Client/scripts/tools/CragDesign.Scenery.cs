using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crag's LOOK and scenery — the half of the design that touches the engine,
/// kept apart from the engine-free layout math in CragDesign.cs (which works in
/// System.Numerics vectors).
///
/// Everything here is this realm's alone: an open dusk over a highland, its
/// light rig, the weathered stone of its works, and the boulder fields. It is
/// deliberately PRIVATE — a second realm that wants a dusk sky should state its
/// own, not inherit The Crag's, and nothing outside this design can reach in and
/// accidentally make these the game's look.
/// </summary>
public sealed partial class CragDesign
{
    /// <summary>An open dusk over the highland: procedural sky, sky ambient, and
    /// distance fog to sink the far crags into, lit by a low setting sun and a
    /// cold woad counter-glow.</summary>
    private static void DressWithDusk(RealmScene scene)
    {
        scene.Add(new WorldEnvironment
        {
            Environment = new Godot.Environment
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
            },
        }, "Environment");

        // The setting sun: low, warm, and this realm's only shadow-caster.
        scene.Add(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-26f, -40f, 0f),
            LightColor = new Color(1.0f, 0.80f, 0.58f),
            LightEnergy = 1.05f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 2400f, // shadows near the action; the fog owns the distance
        }, "Sun");

        // The cold woad counter-glow opposite the sun.
        scene.Add(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-32f, 145f, 0f),
            LightColor = new Color(0.55f, 0.62f, 0.85f),
            LightEnergy = 0.18f,
        }, "Fill");
    }

    /// <summary>Weathered stone for the bridge, the ramparts, and the standing
    /// stones: seamless world-triplanar noise, no vertex-colour tricks — safe for
    /// the occlusion fader's transparency path.</summary>
    private static StandardMaterial3D Stone()
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

    /// <summary>The Crag's ground palette, baked into its terrain mesh's vertices.
    /// Keyed to ABSOLUTE world heights that are this realm's own elevations —
    /// gorge floor, glen, moor, upland, then the border crags — which is exactly
    /// why it lives here and not in a shared library: at another realm's
    /// altitudes these bands are meaningless. Steep ground sheds its soil toward
    /// bare rock at any height.</summary>
    private static Color HighlandColour(float height, float normalY)
    {
        var gorge = new Color(0.14f, 0.13f, 0.15f);
        var glen = new Color(0.22f, 0.32f, 0.16f);
        var moor = new Color(0.30f, 0.27f, 0.18f);
        var upland = new Color(0.33f, 0.33f, 0.23f);
        var crag = new Color(0.33f, 0.32f, 0.35f);

        var byHeight = height switch
        {
            < -60f => gorge,
            < 20f => gorge.Lerp(glen, (height + 60f) / 80f),
            < 140f => glen.Lerp(moor, (height - 20f) / 120f),
            < 280f => moor.Lerp(upland, (height - 140f) / 140f),
            < 420f => upland.Lerp(crag, (height - 280f) / 140f),
            _ => crag,
        };

        // Steep ground sheds its soil: blend toward bare rock as the surface
        // tips past walkable — cliffs read as cliffs at a glance.
        var rockiness = Mathf.Clamp((0.80f - normalY) / 0.35f, 0f, 1f);
        return byHeight.Lerp(crag * 0.9f, rockiness);
    }

    /// <summary>Set the burning waymarkers along the route. Pure scenery — the
    /// simulation has never known about them; they are landmarks for the eye.</summary>
    private static void DressWithBraziers(RealmScene scene)
    {
        var braziers = scene.Folder("Braziers");
        var count = 0;
        foreach (var (x, z) in BrazierSpots)
        {
            var brazier = SceneryRecipes.MakeBrazier(scene.OnGround(x, z));
            brazier.Name = $"Brazier{count++}";
            braziers.AddChild(brazier);
        }
    }

    /// <summary>Scatter weathered boulders over the crag faces.</summary>
    private static void DressWithBoulders(RealmScene scene)
    {
        var rocks = scene.Folder("Boulders");

        // Three rock variants: shared low-detail spheres in differing greys,
        // squashed and tilted per instance so no two read alike.
        var variants = new Mesh[]
        {
            new SphereMesh { Radius = 1f, Height = 1.6f, RadialSegments = 10, Rings = 6,
                             Material = Rock(new Color(0.32f, 0.31f, 0.34f)) },
            new SphereMesh { Radius = 1f, Height = 1.4f, RadialSegments = 8, Rings = 5,
                             Material = Rock(new Color(0.27f, 0.27f, 0.30f)) },
            new SphereMesh { Radius = 1f, Height = 1.8f, RadialSegments = 9, Rings = 6,
                             Material = Rock(new Color(0.36f, 0.34f, 0.33f)) },
        };

        var count = 0;
        foreach (var (position, size, yaw, variant) in Boulders())
        {
            var rock = new MeshInstance3D
            {
                Name = $"Rock_{count++}",
                Mesh = variants[variant],
                Position = position.ToGodot(),
                Rotation = new Vector3(0f, yaw, (variant - 1) * 0.16f), // a lean, so they sit into the slope
                Scale = new Vector3(size, size * 0.62f, size * 0.84f),
            };
            rock.AddToGroup("no_fade", persistent: true); // scenery on the slopes must never dissolve
            rocks.AddChild(rock);
        }
        GD.Print($"scattered {count} boulders over the crag faces.");
    }

    private static StandardMaterial3D Rock(Color color) => new() { AlbedoColor = color, Roughness = 1f };
}

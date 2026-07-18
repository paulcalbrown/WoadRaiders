using System;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// Builds the map's visuals once the geometry arrives. A terrain-bearing realm
/// is built straight from the geometry the server sent — the smooth heightfield
/// becomes one continuous mesh under an open dusk sky, the solids become stone,
/// the braziers burn along the way — so what you see IS what you collide with.
/// Hand-crafted maps render their authored scene; a missing scene falls back to
/// placeholder boxes. Either way, the fade-eligible meshes are registered with
/// the <see cref="OcclusionFader"/>, and a blue entrance portal is stood at the
/// spawn — the mirror of the boss's green exit.
/// </summary>
public static class DungeonVisualBuilder
{
    /// <summary>How far behind the spawn (toward the camera) the entrance portal
    /// stands. Far enough that the chase camera's sight line to the raider passes
    /// OVER the gate — the mouth must never eclipse the character it delivered.</summary>
    private const float PortalSetback = 180f;

    public static void Build(Node3D parent, DungeonGeometry geometry, OcclusionFader fader)
    {
        if (geometry.Terrain is { } terrain)
            BuildRealm(parent, geometry, terrain, fader);
        else if (!TryLoadAuthoredScene(parent, geometry, fader))
            BuildPlaceholderMeshes(parent, geometry, fader);

        // The entrance portal stands at the realm's mouth, set back BEHIND the spawn
        // (between spawn and the chase camera) so raiders walk forward out of it — a
        // blue twin of the boss's green exit, so they arrive through a gate and leave
        // through one. The spawn walk-out (LocalPlayer) starts the character further
        // back still, so they emerge through the gate and stop ahead of it. Purely a
        // landmark — no sim meaning — so it lives with the map visuals, rebuilt and
        // torn down with them.
        var forward = CameraRig.LiveGroundForward;
        var mouth = geometry.SpawnPoint.ToGodot() - forward * PortalSetback;
        mouth.Y = geometry.GroundHeight(mouth.X, mouth.Z); // seat the gate on the land
        parent.AddChild(new PortalView
        {
            Tint = UiTheme.WoadBlue,
            Position = mouth,
            FacingYawDegrees = Mathf.RadToDeg(Mathf.Atan2(forward.X, forward.Z)),
        });
    }

    // ------------------------------------------------------------- open realms

    /// <summary>Stand up a terrain realm: the heightfield as one smooth mesh, solids
    /// as stone, braziers as fire and light, all under an open dusk sky.</summary>
    private static void BuildRealm(Node3D parent, DungeonGeometry geometry, HeightField terrain, OcclusionFader fader)
    {
        parent.AddChild(new MeshInstance3D
        {
            Mesh = BuildTerrainMesh(terrain),
            MaterialOverride = TerrainMaterial(),
        });

        BuildSolids(parent, geometry, fader);

        foreach (var prop in geometry.Props)
            if (prop.Type == PropType.Brazier)
                parent.AddChild(MakeBrazier(prop.Position.ToGodot()));

        AddRealmSky(parent);
        GD.Print($"Rendering open realm '{geometry.ScenePath}' " +
                 $"({terrain.Width}x{terrain.Depth} terrain, {geometry.Solids.Count} solids, {geometry.Props.Count} props)");
    }

    /// <summary>One continuous smooth-shaded mesh over the whole heightfield, coloured
    /// per vertex by height and steepness — grass in the glens, heather on the moor,
    /// bare rock where it's too steep to walk, dark stone in the gorge.</summary>
    private static ArrayMesh BuildTerrainMesh(HeightField terrain)
    {
        int w = terrain.Width, d = terrain.Depth;
        var cell = terrain.CellSize;
        var vertices = new Vector3[w * d];
        var normals = new Vector3[w * d];
        var colors = new Color[w * d];

        for (var j = 0; j < d; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var idx = j * w + i;
                var h = terrain.At(i, j);
                vertices[idx] = new Vector3(terrain.OriginX + i * cell, h, terrain.OriginZ + j * cell);

                // Central differences (clamped at the rim) give smooth normals.
                var hw = terrain.At(Math.Max(i - 1, 0), j);
                var he = terrain.At(Math.Min(i + 1, w - 1), j);
                var hn = terrain.At(i, Math.Max(j - 1, 0));
                var hs = terrain.At(i, Math.Min(j + 1, d - 1));
                var normal = new Vector3(hw - he, 2f * cell, hn - hs).Normalized();
                normals[idx] = normal;
                colors[idx] = TerrainColor(h, normal.Y);
            }
        }

        var indices = new int[(w - 1) * (d - 1) * 6];
        var k = 0;
        for (var j = 0; j < d - 1; j++)
        {
            for (var i = 0; i < w - 1; i++)
            {
                var a = j * w + i;         // clockwise winding — Godot front faces
                indices[k++] = a;
                indices[k++] = a + 1;
                indices[k++] = a + w;
                indices[k++] = a + 1;
                indices[k++] = a + w + 1;
                indices[k++] = a + w;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static Color TerrainColor(float height, float normalY)
    {
        // The height bands of the realm, dusk-lit: gorge stone, glen grass,
        // heather moor, pale upland, then bare crag on the border peaks.
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

    private static StandardMaterial3D TerrainMaterial()
    {
        // Vertex colours carry the biome; a seamless world-triplanar noise pair
        // breaks them up so the ground reads as land, not as a gradient.
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.78f, 0.76f, 0.72f));
        ramp.SetColor(1, new Color(1.06f, 1.05f, 1.02f));

        var albedoNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.04f, Seed = 11 };
        var albedo = new NoiseTexture2D { Noise = albedoNoise, Width = 256, Height = 256, Seamless = true, ColorRamp = ramp };

        var normalNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.07f, Seed = 12 };
        var normal = new NoiseTexture2D
        {
            Noise = normalNoise, Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 4f,
        };

        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            Roughness = 1f,
            Metallic = 0f,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.010f, 0.010f, 0.010f),
        };
    }

    /// <summary>A burning waymarker: a dark iron bowl, a warm pool of light, and a
    /// billboarded flame — the realm's landmarks after dusk.</summary>
    private static Node3D MakeBrazier(Vector3 ground)
    {
        var brazier = new Node3D { Position = ground };

        var iron = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.09f, 0.09f), Roughness = 0.9f };
        brazier.AddChild(new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 15f, BottomRadius = 9f, Height = 26f, RadialSegments = 10 },
            Position = new Vector3(0f, 13f, 0f),
            MaterialOverride = iron,
        });

        brazier.AddChild(new OmniLight3D
        {
            Position = new Vector3(0f, 46f, 0f),
            LightColor = new Color(1.0f, 0.62f, 0.30f),
            LightEnergy = 6f,
            OmniRange = 380f,
            ShadowEnabled = false, // dozens of braziers — keep each cheap
        });

        brazier.AddChild(MakeFlame(new Vector3(0f, 28f, 0f)));
        return brazier;
    }

    /// <summary>The proven torch-flame recipe (tall tapering embers, billboarded,
    /// preprocessed so it burns from the first frame), built in code.</summary>
    private static GpuParticles3D MakeFlame(Vector3 position)
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

    /// <summary>An open dusk over the highland: a procedural sky, one low warm sun,
    /// a faint cool counter-light, and distance fog to sink the far crags into.</summary>
    private static void AddRealmSky(Node3D parent)
    {
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.09f, 0.12f, 0.22f),
            SkyHorizonColor = new Color(0.46f, 0.28f, 0.22f), // dusk ember at the rim
            GroundBottomColor = new Color(0.05f, 0.05f, 0.07f),
            GroundHorizonColor = new Color(0.30f, 0.20f, 0.17f),
            SunAngleMax = 30f,
            SunCurve = 0.6f,
        };

        parent.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.55f,
                // Gentle depth fog: sinks the far crags, leaves the brazier pools alone.
                FogEnabled = true,
                FogLightColor = new Color(0.23f, 0.20f, 0.24f),
                FogDensity = 0.00016f,
                FogSkyAffect = 0.25f,
            },
        });

        // The setting sun: low, warm, and the realm's only shadow-caster.
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-26f, -40f, 0f),
            LightColor = new Color(1.0f, 0.80f, 0.58f),
            LightEnergy = 1.05f,
            ShadowEnabled = true,
            DirectionalShadowMaxDistance = 2400f, // shadows near the action; the fog owns the distance
        });
        parent.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-32f, 145f, 0f),
            LightColor = new Color(0.55f, 0.62f, 0.85f), // cold woad counter-glow
            LightEnergy = 0.18f,
        });
    }

    private static bool TryLoadAuthoredScene(Node3D parent, DungeonGeometry geometry, OcclusionFader fader)
    {
        var path = geometry.ScenePath;
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
            return false;
        if (ResourceLoader.Load<PackedScene>(path) is not { } packed)
            return false;

        var scene = packed.Instantiate<Node>();
        parent.AddChild(scene); // must be in-tree before reading global transforms
        fader.TrackSceneMeshes(scene);
        var selfLit = scene.FindDescendant<WorldEnvironment>() is not null;
        if (!selfLit)
            AddDefaultLighting(parent); // map brings no WorldEnvironment → light it with the default
        GD.Print($"Rendering authored map scene '{path}' ({fader.TrackedSceneMeshCount} fade-aware meshes, " +
                 $"{(selfLit ? "self-lit" : "default lighting")})");
        return true;
    }

    private static void BuildPlaceholderMeshes(Node3D parent, DungeonGeometry geometry, OcclusionFader fader)
    {
        AddDefaultLighting(parent); // placeholder rendering brings no scene, so light it here

        // One floor slab spanning the world extent (authored scenes bring their own floors).
        var bounds = geometry.Bounds;
        var size = bounds.Size.ToGodot();
        var center = bounds.Center.ToGodot();
        var floorMesh = new BoxMesh { Size = new Vector3(size.X, 4f, size.Z) };
        var floorPositions = new List<Vector3> { new(center.X, -2f, center.Z) };
        parent.AddChild(MakeTileField(floorMesh, floorPositions, FloorMaterial()));

        BuildSolids(parent, geometry, fader);
    }

    private static void BuildSolids(Node3D parent, DungeonGeometry geometry, OcclusionFader fader)
    {
        var solids = geometry.Solids;

        // One unit cube, scaled per instance to each solid's size.
        var mesh = new BoxMesh { Size = Vector3.One };
        var walls = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, // must be set before InstanceCount
            Mesh = mesh,
            InstanceCount = solids.Count,
        };
        for (var i = 0; i < solids.Count; i++)
        {
            var basis = Basis.Identity.Scaled(solids[i].Size.ToGodot());
            walls.SetInstanceTransform(i, new Transform3D(basis, solids[i].Center.ToGodot()));
            walls.SetInstanceColor(i, Colors.White); // white = full stone texture; alpha = fade
        }

        parent.AddChild(new MultiMeshInstance3D { Multimesh = walls, MaterialOverride = WallMaterial() });
        fader.TrackWalls(walls, solids);
    }

    // Fallback lighting for placeholder rendering, or an authored scene that has no
    // WorldEnvironment of its own — the dim, cool "dark torch-lit dungeon" default.
    private static void AddDefaultLighting(Node3D parent)
    {
        var key = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55, -50, 0),
            LightEnergy = 0.28f,
            LightColor = new Color(0.70f, 0.78f, 1.0f), // cool moonlight → contrasts warm torches
            ShadowEnabled = true,
        };
        parent.AddChild(key);
        parent.AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-25, 130, 0), LightEnergy = 0.08f });
        parent.AddChild(new WorldEnvironment { Environment = DungeonEnvironment() });
    }

    private static Godot.Environment DungeonEnvironment() => new()
    {
        BackgroundMode = Godot.Environment.BGMode.Color,
        BackgroundColor = new Color(0.015f, 0.015f, 0.025f),
        AmbientLightSource = Godot.Environment.AmbientSource.Color,
        AmbientLightColor = new Color(0.28f, 0.30f, 0.48f),
        AmbientLightEnergy = 0.12f, // low, so torch pools stand out against the dark
        // Very light fog only — at the far ortho camera (~1100 units) even a small
        // density greatly flattens the scene and washes out the torch pools.
        FogEnabled = true,
        FogLightColor = new Color(0.03f, 0.03f, 0.05f),
        FogDensity = 0.0005f,
    };

    private static MultiMeshInstance3D MakeTileField(Mesh mesh, List<Vector3> positions, Material material)
    {
        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = mesh,
            InstanceCount = positions.Count,
        };
        for (var i = 0; i < positions.Count; i++)
            multi.SetInstanceTransform(i, new Transform3D(Basis.Identity, positions[i]));

        return new MultiMeshInstance3D { Multimesh = multi, MaterialOverride = material };
    }

    private static StandardMaterial3D FloorMaterial() => StoneMaterial(
        albedoSeed: 1, normalSeed: 2,
        dark: new Color(0.14f, 0.14f, 0.17f), light: new Color(0.34f, 0.33f, 0.37f), fadeable: false);

    private static StandardMaterial3D WallMaterial() => StoneMaterial(
        albedoSeed: 3, normalSeed: 4,
        dark: new Color(0.06f, 0.06f, 0.09f), light: new Color(0.20f, 0.19f, 0.25f), fadeable: true);

    private static StandardMaterial3D StoneMaterial(int albedoSeed, int normalSeed, Color dark, Color light, bool fadeable)
    {
        var ramp = new Gradient();
        ramp.SetColor(0, dark);
        ramp.SetColor(1, light);

        var albedoNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.05f, Seed = albedoSeed };
        var albedo = new NoiseTexture2D { Noise = albedoNoise, Width = 256, Height = 256, Seamless = true, ColorRamp = ramp };

        var normalNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.08f, Seed = normalSeed };
        var normal = new NoiseTexture2D
        {
            Noise = normalNoise, Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 3f,
        };

        var material = new StandardMaterial3D
        {
            AlbedoColor = Colors.White,
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            Roughness = 0.95f,
            Metallic = 0f,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.012f, 0.012f, 0.012f),
        };

        if (fadeable)
        {
            material.VertexColorUseAsAlbedo = true;
            material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaHash;
        }

        return material;
    }
}

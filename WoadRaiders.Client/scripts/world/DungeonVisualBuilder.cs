using System;
using System.Linq;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// Builds the map's visuals once the geometry arrives. The authored .tscn is
/// the visual truth: when the map names a scene this client has (generated
/// realms like Crag.tscn, or hand-made ones), it is instantiated and renders
/// itself — terrain via its <see cref="RealmTerrain"/> node, braziers, lights,
/// and sky all as authored. Without the scene, a terrain-bearing realm is
/// rebuilt from the geometry the server sent (heightfield mesh, stone solids,
/// braziers, dusk sky — playable even when a custom map's scene isn't in this
/// build), and a flat map falls back to placeholder boxes. Either way, the
/// fade-eligible meshes are registered with the <see cref="OcclusionFader"/>,
/// and a blue entrance portal is stood at the spawn — the mirror of the boss's
/// green exit.
/// </summary>
public static class DungeonVisualBuilder
{
    /// <summary>How far behind the spawn (toward the camera) the entrance portal
    /// stands. Far enough that the chase camera's sight line to the raider passes
    /// OVER the gate — the mouth must never eclipse the character it delivered.</summary>
    private const float PortalSetback = 180f;

    public static void Build(Node3D parent, DungeonGeometry geometry, OcclusionFader fader)
    {
        if (!TryLoadAuthoredScene(parent, geometry, fader))
        {
            if (geometry.Terrain is { } terrain)
                BuildRealm(parent, geometry, terrain, fader);
            else
                BuildPlaceholderMeshes(parent, geometry, fader);
        }

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

    /// <summary>Stand up a terrain realm straight from the wire geometry (the
    /// fallback when the map's authored scene isn't in this build): the
    /// heightfield as a <see cref="RealmTerrain"/> node, solids as stone,
    /// braziers as fire and light, all under an open dusk sky.</summary>
    private static void BuildRealm(Node3D parent, DungeonGeometry geometry, HeightField terrain, OcclusionFader fader)
    {
        parent.AddChild(new RealmTerrain
        {
            OriginX = terrain.OriginX,
            OriginZ = terrain.OriginZ,
            CellSize = terrain.CellSize,
            TerrainWidth = terrain.Width,
            TerrainDepth = terrain.Depth,
            Heights = terrain.Heights.ToArray(),
        });

        BuildSolids(parent, geometry, fader);

        foreach (var prop in geometry.Props)
            if (prop.Type == PropType.Brazier)
                parent.AddChild(RealmDressing.MakeBrazier(prop.Position.ToGodot()));

        // The open dusk: sky environment, the low warm sun, the cold counter-glow.
        parent.AddChild(new WorldEnvironment { Environment = RealmDressing.RealmEnvironment() });
        parent.AddChild(RealmDressing.MakeSun());
        parent.AddChild(RealmDressing.MakeFill());

        GD.Print($"Rendering open realm '{geometry.ScenePath}' from geometry " +
                 $"({terrain.Width}x{terrain.Depth} terrain, {geometry.Solids.Count} solids, {geometry.Props.Count} props)");
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

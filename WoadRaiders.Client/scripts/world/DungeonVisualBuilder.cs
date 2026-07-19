using System;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// Builds the map's visuals once the geometry arrives. The authored .tscn is
/// the visual truth — the ONLY visual truth: the map names a scene, this build
/// instantiates it, and it renders itself (terrain, braziers, lights, and sky
/// all as authored). Fade-eligible meshes are registered with the
/// <see cref="OcclusionFader"/>, and a blue entrance portal is stood at the
/// spawn — the mirror of the boss's green exit.
///
/// There is deliberately NO rendering from the wire geometry. A realm this
/// build lacks the scene for is refused outright (<see cref="Build"/> returns
/// false and the caller ends the session with the download link) rather than
/// approximated: the served geometry is simulation truth, not a second,
/// silently-diverging description of how the realm looks. That divergence is
/// what a from-geometry renderer always is — and the worst version of it, an
/// invisible world with live collision, is far crueller than a clear refusal.
/// </summary>
public static class DungeonVisualBuilder
{
    /// <summary>How far behind the spawn (toward the camera) the entrance portal
    /// stands. Far enough that the chase camera's sight line to the raider passes
    /// OVER the gate — the mouth must never eclipse the character it delivered.</summary>
    private const float PortalSetback = 180f;

    /// <summary>Stand the map up. Returns false when this build has no scene for
    /// the realm the server is hosting — nothing is added in that case, and the
    /// caller must end the session rather than leave the player in a world they
    /// cannot see but can still collide with.</summary>
    public static bool Build(Node3D parent, DungeonGeometry geometry, OcclusionFader fader)
    {
        if (!TryLoadAuthoredScene(parent, geometry, fader))
            return false;

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
        return true;
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

    // For an authored scene that brings no WorldEnvironment of its own — the dim,
    // cool "dark torch-lit dungeon" default.
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

}

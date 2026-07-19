using Godot;
using WoadRaiders.Core;
using SysVec3 = System.Numerics.Vector3;

namespace WoadRaiders.Client;

/// <summary>
/// Fades whatever is between the camera and the local player so the character is
/// never hidden. Tracks the authored scene's fade candidates (their boxes cached
/// in sim space) and applies the per-box score from <see cref="OcclusionFade"/>
/// every frame.
/// </summary>
public sealed class OcclusionFader
{
    private const float FadeMinHeight = 35f; // meshes whose top is below this never fade (floors)

    // Authored-scene visuals: tall meshes that participate in the occlusion fade.
    private readonly List<(GeometryInstance3D Node, SysVec3 Min, SysVec3 Max)> _sceneMeshes = new();

    public int TrackedSceneMeshCount => _sceneMeshes.Count;

    /// <summary>
    /// Register every fade-eligible mesh in an authored scene: tall meshes take
    /// part in the occlusion fade; floors (low tops) never do. Opt any mesh out by
    /// adding it to the "no_fade" group in the editor. The scene must already be
    /// in the tree (global transforms are read here).
    /// </summary>
    public void TrackSceneMeshes(Node sceneRoot)
    {
        foreach (var node in sceneRoot.SelfAndDescendants())
        {
            if (node is not MeshInstance3D mesh || mesh.IsInGroup("no_fade"))
                continue;
            var aabb = mesh.GlobalTransform * mesh.GetAabb();
            if (aabb.Position.Y + aabb.Size.Y > FadeMinHeight)
                _sceneMeshes.Add((mesh, aabb.Position.ToSim(), aabb.End.ToSim()));
        }
    }

    /// <summary>Forget every tracked mesh — the map is being torn down for a rebuild.</summary>
    public void Clear()
    {
        _sceneMeshes.Clear();
    }

    /// <summary>Score every tracked box against the player's body column and apply the
    /// fade. <paramref name="toCamera"/> is this frame's unit direction from the player
    /// toward the camera — live now that the chase camera swings around the raider.</summary>
    public void Update(Vector3 playerBodyCentre, Vector3 toCamera)
    {
        var player = playerBodyCentre.ToSim();
        var camDir = toCamera.ToSim();
        if (camDir.Y <= 0.05f)
            return; // the fade math needs a climbing sight ray; a level camera skips a frame

        foreach (var (node, min, max) in _sceneMeshes)
            node.Transparency = 1f - OcclusionFade.Alpha(player, camDir, min, max);
    }
}

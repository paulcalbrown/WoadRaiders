using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// The occlusion-fade scoring math: how strongly a world-space box hides the
/// local character's body column from a fixed camera. Pure geometry over
/// System.Numerics — engine-free so the fade behavior (occluders fade, walls the
/// character merely stands beside stay solid, micro-fades clamp to fully solid)
/// is unit-testable. The client feeds it every candidate mesh box per frame.
/// </summary>
public static class OcclusionFade
{
    /// <summary>Fully faded alpha is 1 - MaxFade — occluders never vanish entirely.</summary>
    public const float MaxFade = 0.82f;

    // Sample points along the character's height, relative to the body centre.
    // The first/last also bound the cheap cull below (body spread).
    private static readonly float[] BodyFadeOffsets = { -18f, -7f, 4f, 15f, 26f };

    private const float RayRadius = 14f; // per-ray screen aura (body half-width ~16)
    private const float Deadzone = 0.08f;

    /// <summary>
    /// 1 = fully visible; falls toward 1 - <see cref="MaxFade"/> when the box hides
    /// the character's body column from the camera. The question asked per box is
    /// "how much of the body column does this box cover on screen?": several rays
    /// are cast toward the camera from points along the character's height, each
    /// ray scores the box by its exact closest approach (distance to a sight ray IS
    /// screen-space distance under a fixed ortho camera), and the scores are
    /// averaged. Walls the character merely stands beside or in front of —
    /// including torch mounts and trims sticking out of camera-away walls — hide
    /// none of the body column and stay fully solid.
    /// </summary>
    /// <param name="player">The character's body centre (feet + body height).</param>
    /// <param name="camDir">Unit direction from the character toward the camera; must rise (Y &gt; 0).</param>
    public static float Alpha(Vector3 player, Vector3 camDir, Vector3 boxMin, Vector3 boxMax)
    {
        // Cheap cull. The sight rays climb as they travel; past tCap even the lowest
        // ray has cleared the box top by more than the aura, so within the window a ray
        // never gets farther than tCap from its origin — a box beyond that plus the
        // aura and the body spread can never fade. Skips almost every mesh in the map.
        var tCapAll = MathF.Max(0f, (boxMax.Y - (player.Y - 18f)) / camDir.Y) + RayRadius / camDir.Y;
        if (Vector3.Distance(player, Vector3.Clamp(player, boxMin, boxMax)) > tCapAll + RayRadius + 26f)
            return 1f;

        var total = 0f;
        foreach (var offset in BodyFadeOffsets)
        {
            var origin = new Vector3(player.X, player.Y + offset, player.Z);

            // Last ray parameter at which any part of the box is still ahead of the ray.
            var far = MathF.Max((boxMin.X - origin.X) * camDir.X, (boxMax.X - origin.X) * camDir.X)
                    + MathF.Max((boxMin.Y - origin.Y) * camDir.Y, (boxMax.Y - origin.Y) * camDir.Y)
                    + MathF.Max((boxMin.Z - origin.Z) * camDir.Z, (boxMax.Z - origin.Z) * camDir.Z);
            if (far <= 0f)
                continue; // the whole box is behind this body point

            // Ray-to-box distance is convex in the ray parameter, so a ternary search
            // finds the true closest approach. Exactness matters: a fixed-count
            // projection here left a seed-dependent residual on long walls, and that
            // residual wobbling across the in-front threshold made them flicker. Ties
            // walk `hi` down so t lands on the EARLIEST closest approach (t = 0 when
            // the box surrounds the origin, e.g. a doorway arch — which stays solid).
            var tCap = MathF.Max(0f, (boxMax.Y - origin.Y) / camDir.Y) + RayRadius / camDir.Y;
            float lo = 0f, hi = MathF.Min(far, tCap);
            for (var i = 0; i < 24; i++)
            {
                var m1 = lo + (hi - lo) / 3f;
                var m2 = hi - (hi - lo) / 3f;
                if (RayBoxDistSq(origin, camDir, m1, boxMin, boxMax) <=
                    RayBoxDistSq(origin, camDir, m2, boxMin, boxMax))
                    hi = m2;
                else
                    lo = m1;
            }
            var t = (lo + hi) * 0.5f;
            var rayPoint = origin + camDir * t;
            var dist = Vector3.Distance(rayPoint, Vector3.Clamp(rayPoint, boxMin, boxMax));

            // "In front" credit: beside/behind boxes close at t ≈ 0 and score nothing;
            // genuine occluders sit at t ≳ 18 even when touched. A ramp (not a hard
            // gate) keeps the score continuous under per-frame render-position wobble.
            var inFront = Math.Clamp((t - 4f) / 14f, 0f, 1f);
            total += inFront * MathF.Max(0f, 1f - dist / RayRadius) * MaxFade;
        }

        // Deadzone (rescaled so a full fade is unchanged): micro-fades are worse than
        // no fade — any Transparency > 0 moves the mesh into the transparent pipeline,
        // and a wall oscillating around "barely faded" visibly shimmers from the
        // pipeline switch alone. Below the deadzone a wall renders exactly solid.
        var fade = total / BodyFadeOffsets.Length;
        fade = MathF.Max(0f, fade - Deadzone) * (MaxFade / (MaxFade - Deadzone));
        return 1f - fade;
    }

    private static float RayBoxDistSq(Vector3 origin, Vector3 dir, float t, Vector3 boxMin, Vector3 boxMax)
    {
        var p = origin + dir * t;
        return Vector3.DistanceSquared(p, Vector3.Clamp(p, boxMin, boxMax));
    }
}

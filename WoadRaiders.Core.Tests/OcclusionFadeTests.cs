using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class OcclusionFadeTests
{
    // The client's fixed iso camera direction (offset 600,700,600 normalized).
    private static readonly Vector3 CamDir = Vector3.Normalize(new Vector3(600f, 700f, 600f));

    private static readonly Vector3 Player = new(0f, 22f, 0f); // body centre above the feet

    private static (Vector3 Min, Vector3 Max) BoxAt(Vector3 center, float halfSize) =>
        (center - new Vector3(halfSize), center + new Vector3(halfSize));

    [Fact]
    public void A_wall_between_the_player_and_the_camera_fades()
    {
        var (min, max) = BoxAt(Player + CamDir * 120f, 40f);
        var alpha = OcclusionFade.Alpha(Player, CamDir, min, max);

        Assert.True(alpha < 0.5f, $"expected a strong fade, got alpha {alpha}");
    }

    [Fact]
    public void A_wall_beside_the_player_stays_fully_solid()
    {
        // Perpendicular to the sight line on the ground plane: (1, 0, -1) · camDir = 0.
        var side = Vector3.Normalize(new Vector3(1f, 0f, -1f));
        var (min, max) = BoxAt(Player + side * 90f, 30f);

        Assert.Equal(1f, OcclusionFade.Alpha(Player, CamDir, min, max));
    }

    [Fact]
    public void A_wall_behind_the_player_stays_fully_solid()
    {
        var (min, max) = BoxAt(Player - CamDir * 120f, 40f);
        Assert.Equal(1f, OcclusionFade.Alpha(Player, CamDir, min, max));
    }

    [Fact]
    public void A_box_surrounding_the_player_stays_solid()
    {
        // A doorway arch the character stands inside: the closest approach is at
        // t = 0, which earns no "in front" credit — the arch must not fade.
        var (min, max) = BoxAt(Player, 60f);
        Assert.Equal(1f, OcclusionFade.Alpha(Player, CamDir, min, max));
    }

    [Fact]
    public void A_distant_box_is_culled_to_fully_solid()
    {
        var (min, max) = BoxAt(Player + new Vector3(2000f, 0f, 2000f), 50f);
        Assert.Equal(1f, OcclusionFade.Alpha(Player, CamDir, min, max));
    }

    [Fact]
    public void Alpha_is_always_within_the_fade_bounds()
    {
        // Sweep boxes through the whole neighbourhood — including grazing and
        // partially-occluding placements — and require every alpha to stay inside
        // [1 - MaxFade, 1]. Guards the deadzone rescale from over/undershooting.
        var floor = 1f - OcclusionFade.MaxFade;
        for (var x = -300f; x <= 300f; x += 60f)
        for (var y = -60f; y <= 300f; y += 60f)
        for (var z = -300f; z <= 300f; z += 60f)
        {
            var (min, max) = BoxAt(new Vector3(x, y, z), 35f);
            var alpha = OcclusionFade.Alpha(Player, CamDir, min, max);
            Assert.InRange(alpha, floor - 1e-4f, 1f + 1e-4f);
        }
    }

    [Fact]
    public void Fade_grows_as_the_wall_covers_more_of_the_body_column()
    {
        // A thin wall crossing only the top of the body column fades less than a
        // tall wall crossing all of it — the per-ray scores are averaged.
        var tall = BoxAt(Player + CamDir * 120f, 60f);
        var center = Player + CamDir * 120f;
        var thinMin = new Vector3(center.X - 60f, Player.Y + 20f, center.Z - 60f);
        var thinMax = new Vector3(center.X + 60f, Player.Y + 30f, center.Z + 60f);

        var tallAlpha = OcclusionFade.Alpha(Player, CamDir, tall.Min, tall.Max);
        var thinAlpha = OcclusionFade.Alpha(Player, CamDir, thinMin, thinMax);

        Assert.True(tallAlpha < thinAlpha,
            $"tall occluder (alpha {tallAlpha}) should fade more than a sliver (alpha {thinAlpha})");
    }
}

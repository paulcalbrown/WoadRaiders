using System;
using System.Collections.Generic;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The mesh-terrain sampling math (behind the in-Godot bake tool): world-space
/// triangles rasterized straight down onto a heightfield grid — highest surface
/// wins, uncovered cells sink into a pit, seams between triangles never miss.
/// </summary>
public class TerrainSamplerTests
{
    private static List<Vector3> Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) =>
        new() { a, b, c, a, c, d }; // two triangles sharing the a-c seam

    [Fact]
    public void A_tilted_quad_samples_to_a_smooth_ramp()
    {
        // Flat at z=0, rising to 50 at z=100 — like a hand-modelled hillside.
        var tris = Quad(new(0, 0, 0), new(100, 0, 0), new(100, 50, 100), new(0, 50, 100));
        var field = TerrainSampler.Sample(tris, 50f, out var uncovered);

        Assert.Equal(0, uncovered);
        Assert.Equal(3, field.Width);
        Assert.Equal(3, field.Depth);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(0f, field.At(i, 0), 2);
            Assert.Equal(25f, field.At(i, 1), 2);  // the seam row — covered by both triangles
            Assert.Equal(50f, field.At(i, 2), 2);
        }
    }

    [Fact]
    public void The_highest_surface_wins_under_overlap()
    {
        var tris = Quad(new(0, 0, 0), new(100, 0, 0), new(100, 0, 100), new(0, 0, 100));
        // A shelf floating at 90 over the north-west quarter.
        tris.AddRange(Quad(new(0, 90, 0), new(50, 90, 0), new(50, 90, 50), new(0, 90, 50)));

        var field = TerrainSampler.Sample(tris, 50f, out _);
        Assert.Equal(90f, field.At(0, 0), 2); // under the shelf
        Assert.Equal(0f, field.At(2, 2), 2);  // open ground
    }

    [Fact]
    public void Uncovered_cells_sink_into_a_pit()
    {
        // A single triangle: the far corner of its bounding grid is uncovered.
        var tris = new List<Vector3> { new(0, 10, 0), new(100, 10, 0), new(0, 10, 100) };
        var field = TerrainSampler.Sample(tris, 50f, out var uncovered);

        Assert.True(uncovered > 0);
        Assert.Equal(10f, field.At(0, 0), 2);
        Assert.Equal(10f - TerrainSampler.UncoveredDrop, field.At(2, 2), 2); // the pit
    }

    [Fact]
    public void Vertical_slivers_are_ignored()
    {
        var tris = Quad(new(0, 0, 0), new(100, 0, 0), new(100, 0, 100), new(0, 0, 100));
        // A wall standing on the ground: no XZ footprint, nothing to stand on.
        tris.AddRange(new Vector3[] { new(50, 0, 0), new(50, 80, 0), new(50, 80, 100) });

        var field = TerrainSampler.Sample(tris, 50f, out _);
        Assert.Equal(0f, field.At(1, 1), 2); // the wall contributed no height
    }

    [Fact]
    public void Bad_input_is_refused()
    {
        var quad = Quad(new(0, 0, 0), new(100, 0, 0), new(100, 0, 100), new(0, 0, 100));
        Assert.Throws<ArgumentException>(() => TerrainSampler.Sample(quad, 0f, out _));
        Assert.Throws<ArgumentException>(() =>
            TerrainSampler.Sample(new List<Vector3> { new(0, 0, 0) }, 40f, out _));
        // Only vertical slivers (zero XZ span — nothing to stand over).
        Assert.Throws<ArgumentException>(() => TerrainSampler.Sample(
            new List<Vector3> { new(0, 0, 0), new(0, 5, 0), new(0, 9, 0) }, 40f, out _));
    }
}

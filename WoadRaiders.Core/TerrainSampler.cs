using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Bakes a heightfield from raw triangle geometry — the math behind sampling a
/// hand-sculpted terrain (any meshes a map maker grouped as "terrain") onto
/// the regular grid the simulation walks. Pure C# over world-space triangles,
/// so it is unit-tested here; the in-Godot bake tool only extracts the
/// triangles and hands them over.
///
/// Sampling is straight down: at every grid point the HIGHEST surface wins (an
/// overhang's underside never becomes the ground). Grid points no triangle
/// covers become a deep pit far below the lowest hit — falling in means
/// stranding, which RealmValidator reports, which makes authors seal their
/// borders rather than ship holes.
/// </summary>
public static class TerrainSampler
{
    /// <summary>How far below the lowest sampled point an uncovered cell sinks.</summary>
    public const float UncoveredDrop = 200f;

    /// <summary>Heights are rounded to this many decimals, like the realm generator's.</summary>
    public const int RoundDecimals = 3;

    private const int MaxSamples = 4_000_000; // mirrors the wire guard in DungeonGeometryPacket

    /// <summary>
    /// Sample world-space triangles (three <see cref="Vector3"/> per triangle)
    /// onto a heightfield grid of <paramref name="cellSize"/>. The grid spans
    /// the triangles' XZ bounds. <paramref name="uncoveredCells"/> reports how
    /// many grid points no triangle covered (they became the pit).
    /// </summary>
    public static HeightField Sample(IReadOnlyList<Vector3> triangleVertices, float cellSize, out int uncoveredCells)
    {
        if (triangleVertices.Count < 3)
            throw new ArgumentException("terrain sampling needs at least one triangle");
        if (!(cellSize > 0f) || !float.IsFinite(cellSize))
            throw new ArgumentException($"cell size must be a positive finite number (got {cellSize})");

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in triangleVertices)
        {
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }

        var width = (int)MathF.Ceiling((max.X - min.X) / cellSize) + 1;
        var depth = (int)MathF.Ceiling((max.Z - min.Z) / cellSize) + 1;
        if (width < 2 || depth < 2)
            throw new ArgumentException(
                $"the terrain spans a {width}x{depth} grid at cell size {cellSize} — too small to sample");
        if ((long)width * depth > MaxSamples)
            throw new ArgumentException(
                $"the terrain spans a {width}x{depth} grid at cell size {cellSize} — too many samples; raise the cell size");

        var heights = new float[width * depth];
        Array.Fill(heights, float.NegativeInfinity);

        // Rasterize each triangle over the grid points its XZ footprint covers:
        // point-in-triangle by barycentrics, height by barycentric interpolation.
        // Straight-down sampling needs no rays at all.
        var triangles = triangleVertices.Count / 3;
        for (var t = 0; t < triangles; t++)
        {
            var a = triangleVertices[t * 3];
            var b = triangleVertices[t * 3 + 1];
            var c = triangleVertices[t * 3 + 2];

            var denom = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(denom) < 1e-9f)
                continue; // degenerate in XZ (a vertical sliver) — nothing to stand on

            var loI = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.X, MathF.Min(b.X, c.X)) - min.X) / cellSize - 1e-4f));
            var hiI = Math.Min(width - 1, (int)MathF.Floor((MathF.Max(a.X, MathF.Max(b.X, c.X)) - min.X) / cellSize + 1e-4f));
            var loJ = Math.Max(0, (int)MathF.Ceiling((MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) - min.Z) / cellSize - 1e-4f));
            var hiJ = Math.Min(depth - 1, (int)MathF.Floor((MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - min.Z) / cellSize + 1e-4f));

            for (var j = loJ; j <= hiJ; j++)
            {
                for (var i = loI; i <= hiI; i++)
                {
                    var x = min.X + i * cellSize;
                    var z = min.Z + j * cellSize;
                    var w1 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / denom;
                    var w2 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / denom;
                    var w3 = 1f - w1 - w2;
                    const float edge = -1e-4f; // inclusive edges, so seams between triangles never miss
                    if (w1 < edge || w2 < edge || w3 < edge)
                        continue;
                    var y = w1 * a.Y + w2 * b.Y + w3 * c.Y;
                    var idx = j * width + i;
                    if (y > heights[idx])
                        heights[idx] = y;
                }
            }
        }

        var lowest = float.MaxValue;
        foreach (var h in heights)
            if (!float.IsNegativeInfinity(h))
                lowest = MathF.Min(lowest, h);
        if (lowest == float.MaxValue)
            throw new ArgumentException("no grid point lies over any terrain triangle — is the terrain far from its own bounds?");

        uncoveredCells = 0;
        var pit = MathF.Round(lowest - UncoveredDrop, RoundDecimals);
        for (var i = 0; i < heights.Length; i++)
        {
            if (float.IsNegativeInfinity(heights[i]))
            {
                heights[i] = pit;
                uncoveredCells++;
            }
            else
            {
                heights[i] = MathF.Round(heights[i], RoundDecimals);
            }
        }

        return new HeightField(min.X, min.Z, cellSize, width, depth, heights);
    }
}

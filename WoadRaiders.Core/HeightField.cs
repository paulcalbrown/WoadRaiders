namespace WoadRaiders.Core;

/// <summary>
/// A smooth terrain surface: a regular grid of height samples, bilinearly
/// interpolated between samples, clamped at the edges. This is the realm's base
/// plane — the ground everything stands on where no solid says otherwise — and
/// what makes an outdoor map read as rolling, continuous land rather than tiles.
/// Engine-free and immutable; the server and every client sample the exact same
/// floats, so prediction can never drift from the server's ground.
/// </summary>
public sealed class HeightField
{
    private readonly float[] _heights; // row-major: [z * Width + x]

    /// <summary>World X of sample column 0.</summary>
    public float OriginX { get; }

    /// <summary>World Z of sample row 0.</summary>
    public float OriginZ { get; }

    /// <summary>World units between adjacent samples.</summary>
    public float CellSize { get; }

    /// <summary>Samples along X.</summary>
    public int Width { get; }

    /// <summary>Samples along Z.</summary>
    public int Depth { get; }

    /// <summary>The raw samples, row-major ([z * Width + x]) — for serialization.</summary>
    public IReadOnlyList<float> Heights => _heights;

    public float MaxX => OriginX + (Width - 1) * CellSize;
    public float MaxZ => OriginZ + (Depth - 1) * CellSize;

    public HeightField(float originX, float originZ, float cellSize, int width, int depth, float[] heights)
    {
        if (width < 2 || depth < 2)
            throw new ArgumentException($"a height field needs at least 2x2 samples (got {width}x{depth})");
        if (!(cellSize > 0f) || !float.IsFinite(cellSize))
            throw new ArgumentException($"cell size must be a positive finite number (got {cellSize})");
        if (heights.Length != width * depth)
            throw new ArgumentException($"expected {width * depth} height samples, got {heights.Length}");
        foreach (var h in heights)
            if (!float.IsFinite(h))
                throw new ArgumentException("height samples must be finite");

        OriginX = originX;
        OriginZ = originZ;
        CellSize = cellSize;
        Width = width;
        Depth = depth;
        _heights = heights;
    }

    /// <summary>The sample at a grid coordinate (no interpolation).</summary>
    public float At(int x, int z) => _heights[z * Width + x];

    /// <summary>
    /// The terrain height at a world XZ point: bilinear between the four
    /// surrounding samples, clamped to the grid at the edges (the terrain
    /// extends flat beyond its last sample rather than falling to nothing).
    /// </summary>
    public float Sample(float x, float z)
    {
        var fx = Math.Clamp((x - OriginX) / CellSize, 0f, Width - 1f);
        var fz = Math.Clamp((z - OriginZ) / CellSize, 0f, Depth - 1f);
        var x0 = Math.Min((int)fx, Width - 2);
        var z0 = Math.Min((int)fz, Depth - 2);
        var tx = fx - x0;
        var tz = fz - z0;

        var h00 = _heights[z0 * Width + x0];
        var h10 = _heights[z0 * Width + x0 + 1];
        var h01 = _heights[(z0 + 1) * Width + x0];
        var h11 = _heights[(z0 + 1) * Width + x0 + 1];

        var north = h00 + (h10 - h00) * tx;
        var south = h01 + (h11 - h01) * tx;
        return north + (south - north) * tz;
    }
}

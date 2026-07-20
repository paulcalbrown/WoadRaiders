using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// A realm's geometry as raw triangles with a coarse XZ grid index — the
/// exact-surface half of the mesh-based realm. The navmesh answers "where can
/// feet go"; the soup answers the rest: line-of-sight segments, cursor rays,
/// and the floor height under a point. FLOOR triangles (surfaces authored to
/// be walked and ridden — terraces, room floors, ramps) come first in the
/// array; the rest is STRUCTURE (walls, roofs, monuments — they block and
/// occlude but are never ridden), so floor-only queries filter by index alone.
///
/// Not thread-safe: query scratch state is reused, one instance per
/// simulation — the same discipline the sim itself already lives by.
/// </summary>
public sealed class TriangleSoup
{
    /// <summary>Vertex positions, xyz triples.</summary>
    public float[] Vertices { get; }

    /// <summary>Vertex indices, one triangle per triple. Floor triangles first.</summary>
    public int[] Triangles { get; }

    /// <summary>Triangles [0, count) are floor; the rest are structure.</summary>
    public int FloorTriangleCount { get; }

    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    // XZ grid over the bounds; each cell lists the triangles whose XZ box overlaps it.
    private readonly List<int>?[] _cells;
    private readonly int _cellsX;
    private readonly int _cellsZ;
    private readonly float _cellSize;

    // Each triangle's unit normal Y, precomputed: the orientation tests below
    // (is this a surface underfoot, is this a wall) are pure geometry, asked
    // often enough that recomputing a cross product per query would show.
    private readonly float[] _normalY;

    // Query scratch: a stamp per triangle dedupes multi-cell hits without allocating.
    private readonly int[] _stamp;
    private readonly List<int> _found = new();
    private int _queryId;

    public TriangleSoup(float[] vertices, int[] triangles, int floorTriangleCount)
    {
        if (vertices.Length < 9 || vertices.Length % 3 != 0)
            throw new ArgumentException($"vertices must be xyz triples for at least one triangle (got {vertices.Length} floats)");
        if (triangles.Length < 3 || triangles.Length % 3 != 0)
            throw new ArgumentException($"triangles must be index triples (got {triangles.Length} indices)");
        if (floorTriangleCount < 0 || floorTriangleCount * 3 > triangles.Length)
            throw new ArgumentException($"terrain triangle count {floorTriangleCount} exceeds the {triangles.Length / 3} triangles");
        foreach (var i in triangles)
            if (i < 0 || i * 3 >= vertices.Length)
                throw new ArgumentException($"triangle index {i} is outside the vertex array");
        foreach (var v in vertices)
            if (!float.IsFinite(v))
                throw new ArgumentException("vertex positions must be finite");

        Vertices = vertices;
        Triangles = triangles;
        FloorTriangleCount = floorTriangleCount;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < vertices.Length; i += 3)
        {
            var v = new Vector3(vertices[i], vertices[i + 1], vertices[i + 2]);
            min = Vector3.Min(min, v);
            max = Vector3.Max(max, v);
        }
        BoundsMin = min;
        BoundsMax = max;

        _cellSize = MathF.Max(8f, MathF.Max(max.X - min.X, max.Z - min.Z) / 128f);
        _cellsX = (int)((max.X - min.X) / _cellSize) + 1;
        _cellsZ = (int)((max.Z - min.Z) / _cellSize) + 1;
        _cells = new List<int>?[_cellsX * _cellsZ];
        _stamp = new int[triangles.Length / 3];
        _normalY = new float[triangles.Length / 3];

        for (var t = 0; t < triangles.Length / 3; t++)
        {
            var (a, b, c) = Corners(t);
            var n = Vector3.Cross(b - a, c - a);
            var len = n.Length();
            _normalY[t] = len > 1e-12f ? n.Y / len : 0f;
            var (i0, j0) = CellOf(MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
            var (i1, j1) = CellOf(MathF.Max(a.X, MathF.Max(b.X, c.X)), MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
            for (var j = j0; j <= j1; j++)
                for (var i = i0; i <= i1; i++)
                    (_cells[j * _cellsX + i] ??= new List<int>()).Add(t);
        }
    }

    /// <summary>
    /// Does anything cut the open segment between the points? With
    /// <paramref name="structureOnly"/> the floors are ignored — the test a
    /// body clearance probe wants, where the ground itself is never a wall.
    /// </summary>
    public bool SegmentHits(Vector3 from, Vector3 to, bool structureOnly = false)
    {
        var d = to - from;
        foreach (var t in Gather(MathF.Min(from.X, to.X), MathF.Min(from.Z, to.Z),
                                 MathF.Max(from.X, to.X), MathF.Max(from.Z, to.Z)))
        {
            if (structureOnly && t < FloorTriangleCount)
                continue;
            // Open interval: touching a surface at either endpoint is not occlusion.
            if (RayTriangle(from, d, t, out var hit) && hit is > 1e-4f and < 1f - 1e-4f)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The nearest triangle the ray strikes within reach — terrain or solid.
    /// <paramref name="direction"/> must be normalized.
    /// </summary>
    public bool RaycastNearest(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit)
    {
        hit = default;
        var d = direction * maxDistance;
        var to = origin + d;
        var best = float.MaxValue;
        foreach (var t in Gather(MathF.Min(origin.X, to.X), MathF.Min(origin.Z, to.Z),
                                 MathF.Max(origin.X, to.X), MathF.Max(origin.Z, to.Z)))
            if (RayTriangle(origin, d, t, out var f) && f > 1e-4f && f <= 1f && f < best)
                best = f;
        if (best == float.MaxValue)
            return false;
        hit = origin + d * best;
        return true;
    }

    /// <summary>
    /// The floor height at a world XZ point (structure ignored) — the highest
    /// floor surface there, the ground a projectile hugs. Null when the point
    /// lies outside every floor triangle (the void beyond the realm).
    /// </summary>
    public float? FloorHeightAt(float x, float z)
    {
        float? best = null;
        var (i, j) = CellOf(x, z);
        foreach (var t in _cells[j * _cellsX + i] ?? (IEnumerable<int>)Array.Empty<int>())
            if (t < FloorTriangleCount && HeightOfTriangleAt(t, x, z) is { } y && (best is null || y > best))
                best = y;
        return best;
    }

    /// <summary>
    /// How far a face must lean off vertical before it stops being a WALL and
    /// starts being a surface something can rest on (its |normal.Y| above
    /// this, ≈87°). Deliberately NOT the navmesh's walkable cutoff (~67.8°):
    /// the sim lets a mover descend ground of ANY grade and inch up anything
    /// within StepHeight, so treating merely-steep ground as a wall would
    /// silently forbid descents the rules allow. Only the near-vertical walls.
    /// </summary>
    public const float WallNormalY = 0.05f;

    /// <summary>
    /// The surface underfoot at a world XZ: the highest up-facing surface at
    /// or below <paramref name="referenceY"/> (plus <paramref name="tolerance"/>,
    /// so a mover standing exactly ON a surface still finds it).
    ///
    /// Y-AWARE by design. A realm that stacks walkable levels — a bridge deck
    /// over a chasm — has no single "the ground" at an XZ, and answering with
    /// the highest puts the ground on the ROOF of whoever stands underneath.
    /// When the point lies beneath everything here, the lowest surface is the
    /// nearest thing to it, so that is the answer. Null when no surface
    /// covers this XZ at all (the void beyond the realm).
    /// </summary>
    public float? GroundBelow(float x, float z, float referenceY, float tolerance)
    {
        float? below = null, lowest = null;
        var (i, j) = CellOf(x, z);
        foreach (var t in _cells[j * _cellsX + i] ?? (IEnumerable<int>)Array.Empty<int>())
        {
            if (_normalY[t] <= WallNormalY || HeightOfTriangleAt(t, x, z) is not { } y)
                continue; // sheer or downward faces are not ground
            if (y <= referenceY + tolerance && (below is null || y > below))
                below = y;
            if (lowest is null || y < lowest)
                lowest = y;
        }
        return below ?? lowest;
    }

    /// <summary>
    /// The surface — terrain or a solid's face — whose height at this XZ lies
    /// closest to a reference Y, within a tolerance. This is how a voxel-rough
    /// navmesh height is refined back onto the exact geometry: the navmesh
    /// picks the layer, the soup supplies the true surface under the feet.
    /// </summary>
    public float? SurfaceNear(float x, float z, float y, float tolerance)
    {
        float? best = null;
        var (i, j) = CellOf(x, z);
        foreach (var t in _cells[j * _cellsX + i] ?? (IEnumerable<int>)Array.Empty<int>())
            if (HeightOfTriangleAt(t, x, z) is { } h && MathF.Abs(h - y) <= tolerance &&
                (best is null || MathF.Abs(h - y) < MathF.Abs(best.Value - y)))
                best = h;
        return best;
    }

    /// <summary>
    /// The triangle's height where its XZ footprint covers the point; null when
    /// it doesn't, or when the triangle is too near vertical to stand on.
    /// </summary>
    private float? HeightOfTriangleAt(int tri, float x, float z)
    {
        var (a, b, c) = Corners(tri);
        var denom = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(denom) < 1e-9f)
            return null;
        var wa = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / denom;
        var wb = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / denom;
        var wc = 1f - wa - wb;
        if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f)
            return null;
        return wa * a.Y + wb * b.Y + wc * c.Y;
    }

    /// <summary>Möller–Trumbore, both faces; t is in units of <paramref name="d"/>.</summary>
    private bool RayTriangle(Vector3 origin, Vector3 d, int tri, out float t)
    {
        t = 0f;
        var (a, b, c) = Corners(tri);
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;
        var inv = 1f / det;
        var s = origin - a;
        var u = Vector3.Dot(s, p) * inv;
        if (u is < -1e-5f or > 1.00001f)
            return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) * inv;
        if (v < -1e-5f || u + v > 1.00001f)
            return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0f;
    }

    private (Vector3 a, Vector3 b, Vector3 c) Corners(int tri) =>
        (Vertex(Triangles[tri * 3]), Vertex(Triangles[tri * 3 + 1]), Vertex(Triangles[tri * 3 + 2]));

    private Vector3 Vertex(int i) => new(Vertices[i * 3], Vertices[i * 3 + 1], Vertices[i * 3 + 2]);

    private (int i, int j) CellOf(float x, float z) => (
        Math.Clamp((int)((x - BoundsMin.X) / _cellSize), 0, _cellsX - 1),
        Math.Clamp((int)((z - BoundsMin.Z) / _cellSize), 0, _cellsZ - 1));

    /// <summary>Every triangle whose cell range overlaps the XZ box, deduped by stamp.</summary>
    private List<int> Gather(float minX, float minZ, float maxX, float maxZ)
    {
        _queryId++;
        _found.Clear();
        var (i0, j0) = CellOf(minX, minZ);
        var (i1, j1) = CellOf(maxX, maxZ);
        for (var j = j0; j <= j1; j++)
            for (var i = i0; i <= i1; i++)
            {
                var cell = _cells[j * _cellsX + i];
                if (cell is null)
                    continue;
                foreach (var t in cell)
                    if (_stamp[t] != _queryId)
                    {
                        _stamp[t] = _queryId;
                        _found.Add(t);
                    }
            }
        return _found;
    }
}

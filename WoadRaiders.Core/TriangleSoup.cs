using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// A realm's geometry as raw triangles under a bounding-volume hierarchy — the
/// exact-surface half of the mesh-based realm. The navmesh answers "where can
/// feet go"; the soup answers the rest: line-of-sight segments, cursor rays,
/// and the surface under a point.
///
/// The index is a BVH rather than a uniform grid because detail does not
/// arrive evenly. A grid sized from the realm's EXTENT cannot subdivide a
/// carved statue any finer than a bare courtyard, so concentrated geometry
/// degenerates into a linear scan of one crowded cell — measured at 3.3 µs
/// climbing to 94 µs per sight line as 12k triangles became 480k in the same
/// corner. A hierarchy splits where the triangles actually are. It also ends
/// the need to dedupe: a triangle lands in exactly ONE leaf, where a grid had
/// to stamp every triangle straddling a cell edge.
///
/// The soup is UNTYPED: nothing here knows which triangles an author thought
/// of as floor and which as wall. It does not need to. Whether a surface can
/// be rested on, and whether it blocks a body, are properties of its own
/// normal (see <see cref="WallNormalY"/>) — and whether it can be WALKED is
/// the navmesh's answer, computed from slope, clearance and step height. Both
/// were once carried by a hand-maintained floor/structure split that authors
/// had to keep honest; both are geometry, so both are now derived.
///
/// Not thread-safe: query scratch state is reused, one instance per
/// simulation — the same discipline the sim itself already lives by.
/// </summary>
public sealed class TriangleSoup
{
    /// <summary>Vertex positions, xyz triples.</summary>
    public float[] Vertices { get; }

    /// <summary>Vertex indices, one triangle per triple.</summary>
    public int[] Triangles { get; }

    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    /// <summary>Triangles per leaf: small enough to prune, large enough that the walk pays.</summary>
    private const int LeafSize = 8;

    /// <summary>
    /// A depth no balanced split reaches (2^40 triangles); the cap only exists
    /// so pathological geometry degrades into a fat leaf instead of a stack
    /// overflow, and so the traversal stack can be a fixed size.
    /// </summary>
    private const int MaxDepth = 40;

    /// <summary>One node of the hierarchy. A leaf owns a run of
    /// <see cref="Payload"/>..+<see cref="Count"/> slots in the order array; an
    /// inner node (Count 0) keeps its LEFT child at the next index and its
    /// right in <see cref="Payload"/>.</summary>
    private readonly struct BvhNode(Vector3 min, Vector3 max, int payload, int count)
    {
        public readonly Vector3 Min = min;
        public readonly Vector3 Max = max;
        public readonly int Payload = payload;
        public readonly int Count = count;
    }

    private readonly BvhNode[] _nodes;
    private readonly int[] _order; // triangle indices, grouped so each leaf owns a run

    // Each triangle's unit normal Y, precomputed: the orientation tests below
    // (is this a surface underfoot, is this a wall) are pure geometry, asked
    // often enough that recomputing a cross product per query would show.
    private readonly float[] _normalY;

    // Query scratch, reused: hence "not thread-safe" above.
    private readonly int[] _stack = new int[2 * MaxDepth + 8];
    private readonly List<int> _found = new();

    public TriangleSoup(float[] vertices, int[] triangles)
    {
        if (vertices.Length < 9 || vertices.Length % 3 != 0)
            throw new ArgumentException($"vertices must be xyz triples for at least one triangle (got {vertices.Length} floats)");
        if (triangles.Length < 3 || triangles.Length % 3 != 0)
            throw new ArgumentException($"triangles must be index triples (got {triangles.Length} indices)");
        foreach (var i in triangles)
            if (i < 0 || i * 3 >= vertices.Length)
                throw new ArgumentException($"triangle index {i} is outside the vertex array");
        foreach (var v in vertices)
            if (!float.IsFinite(v))
                throw new ArgumentException("vertex positions must be finite");

        Vertices = vertices;
        Triangles = triangles;

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

        var triangleCount = triangles.Length / 3;
        _normalY = new float[triangleCount];
        _order = new int[triangleCount];
        var triMin = new Vector3[triangleCount];
        var triMax = new Vector3[triangleCount];

        for (var t = 0; t < triangleCount; t++)
        {
            var (a, b, c) = Corners(t);
            var n = Vector3.Cross(b - a, c - a);
            var len = n.Length();
            _normalY[t] = len > 1e-12f ? n.Y / len : 0f;
            triMin[t] = Vector3.Min(a, Vector3.Min(b, c));
            triMax[t] = Vector3.Max(a, Vector3.Max(b, c));
            _order[t] = t;
        }

        // Nodes land in build order, so a node's LEFT child is always the very
        // next one and only the right needs storing.
        var nodes = new List<BvhNode>(Math.Max(1, triangleCount / 4));
        Build(nodes, triMin, triMax, 0, triangleCount, 0);
        _nodes = nodes.ToArray();
    }

    /// <summary>Build the subtree over order[start, start+count), returning its node index.</summary>
    private int Build(List<BvhNode> nodes, Vector3[] triMin, Vector3[] triMax, int start, int count, int depth)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = start; i < start + count; i++)
        {
            min = Vector3.Min(min, triMin[_order[i]]);
            max = Vector3.Max(max, triMax[_order[i]]);
        }

        var index = nodes.Count;
        nodes.Add(default); // reserve this slot; children follow it

        if (count <= LeafSize || depth >= MaxDepth)
        {
            nodes[index] = new BvhNode(min, max, start, count);
            return index;
        }

        var split = Partition(triMin, triMax, start, count);
        Build(nodes, triMin, triMax, start, split - start, depth + 1); // left: index + 1
        var right = Build(nodes, triMin, triMax, split, start + count - split, depth + 1);
        nodes[index] = new BvhNode(min, max, right, 0);
        return index;
    }

    /// <summary>
    /// Split the run about the midpoint of its centroid spread on the widest
    /// axis, shuffling <see cref="_order"/> in place. A split that leaves one
    /// side empty (co-located triangles) falls back to halving the run, so the
    /// tree always makes progress. Deterministic: the swaps depend on nothing
    /// but the array's contents, so every peer builds the identical tree.
    /// </summary>
    private int Partition(Vector3[] triMin, Vector3[] triMax, int start, int count)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (var i = start; i < start + count; i++)
        {
            var c = (triMin[_order[i]] + triMax[_order[i]]) * 0.5f;
            lo = Vector3.Min(lo, c);
            hi = Vector3.Max(hi, c);
        }

        var extent = hi - lo;
        var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        var mid = 0.5f * (Component(lo, axis) + Component(hi, axis));

        var left = start;
        var right = start + count - 1;
        while (left <= right)
        {
            var t = _order[left];
            var centre = 0.5f * (Component(triMin[t], axis) + Component(triMax[t], axis));
            if (centre < mid)
            {
                left++;
            }
            else
            {
                (_order[left], _order[right]) = (_order[right], _order[left]);
                right--;
            }
        }
        return left == start || left == start + count ? start + count / 2 : left;
    }

    private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>
    /// Does anything cut the open segment between the points? With
    /// <paramref name="blockersOnly"/> only near-vertical faces count — the
    /// test a body's clearance probe wants, where ground of any grade is
    /// something to walk on rather than something to walk into.
    /// </summary>
    public bool SegmentHits(Vector3 from, Vector3 to, bool blockersOnly = false)
    {
        // Tested DURING the descent, not after gathering: this is the question
        // the sim asks most (every enemy's sight line, every tick), it only
        // needs the first blocker, and in crowded geometry the difference
        // between stopping at that blocker and cataloguing the whole crowd is
        // most of the cost.
        var d = to - from;
        var depth = 0;
        _stack[depth++] = 0;
        while (depth > 0)
        {
            var index = _stack[--depth];
            var node = _nodes[index];
            if (!SegmentMeets(from, d, node.Min, node.Max))
                continue;
            if (node.Count == 0)
            {
                _stack[depth++] = node.Payload; // right
                _stack[depth++] = index + 1;    // left
                continue;
            }
            for (var i = 0; i < node.Count; i++)
            {
                var t = _order[node.Payload + i];
                if (blockersOnly && MathF.Abs(_normalY[t]) > WallNormalY)
                    continue;
                // Open interval: touching a surface at either endpoint is not occlusion.
                if (RayTriangle(from, d, t, out var hit) && hit is > 1e-4f and < 1f - 1e-4f)
                    return true;
            }
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
        var best = float.MaxValue;
        foreach (var t in GatherAlong(origin, d))
            if (RayTriangle(origin, d, t, out var f) && f > 1e-4f && f <= 1f && f < best)
                best = f;
        if (best == float.MaxValue)
            return false;
        hit = origin + d * best;
        return true;
    }

    /// <summary>
    /// The topmost surface at a world XZ, whatever its height — for callers
    /// with no vantage of their own to resolve a stack from (a top-down map,
    /// a sweep looking for anywhere to stand).
    /// </summary>
    public float? TopSurfaceAt(float x, float z) =>
        GroundBelow(x, z, float.PositiveInfinity, 0f);

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
        foreach (var t in Gather(x, z, x, z))
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
        foreach (var t in Gather(x, z, x, z))
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

    /// <summary>
    /// Every triangle in a node the segment actually passes through — a much
    /// tighter net than the segment's bounding box, which for a diagonal line
    /// through crowded geometry sweeps up most of the crowd. Still a superset
    /// of the true hits (a triangle lies inside its node's bounds), so the
    /// exact test downstream settles the answer unchanged.
    /// </summary>
    private List<int> GatherAlong(Vector3 origin, Vector3 d)
    {
        _found.Clear();
        var depth = 0;
        _stack[depth++] = 0;
        while (depth > 0)
        {
            var index = _stack[--depth];
            var node = _nodes[index];
            if (!SegmentMeets(origin, d, node.Min, node.Max))
                continue;
            if (node.Count > 0)
            {
                for (var i = 0; i < node.Count; i++)
                    _found.Add(_order[node.Payload + i]);
            }
            else
            {
                _stack[depth++] = node.Payload; // right
                _stack[depth++] = index + 1;    // left
            }
        }
        return _found;
    }

    /// <summary>Slab test: does origin + d*t, t in [0,1], pass through the box?</summary>
    private static bool SegmentMeets(Vector3 origin, Vector3 d, Vector3 min, Vector3 max)
    {
        float enter = 0f, exit = 1f;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = Component(origin, axis);
            var step = Component(d, axis);
            var lo = Component(min, axis);
            var hi = Component(max, axis);
            if (MathF.Abs(step) < 1e-12f)
            {
                if (o < lo || o > hi)
                    return false; // parallel to this slab and outside it
                continue;
            }
            var inv = 1f / step;
            var near = (lo - o) * inv;
            var far = (hi - o) * inv;
            if (near > far)
                (near, far) = (far, near);
            enter = MathF.Max(enter, near);
            exit = MathF.Min(exit, far);
            if (enter > exit)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Every triangle whose bounds overlap the XZ box — a superset of what can
    /// really be hit, which the exact tests above then settle. Degenerate the
    /// box to a point for a query straight down a column.
    /// </summary>
    private List<int> Gather(float minX, float minZ, float maxX, float maxZ)
    {
        _found.Clear();
        var depth = 0;
        _stack[depth++] = 0;
        while (depth > 0)
        {
            var index = _stack[--depth];
            var node = _nodes[index];
            if (node.Max.X < minX || node.Min.X > maxX || node.Max.Z < minZ || node.Min.Z > maxZ)
                continue;
            if (node.Count > 0)
            {
                for (var i = 0; i < node.Count; i++)
                    _found.Add(_order[node.Payload + i]);
            }
            else
            {
                _stack[depth++] = node.Payload; // right
                _stack[depth++] = index + 1;    // left
            }
        }
        return _found;
    }
}

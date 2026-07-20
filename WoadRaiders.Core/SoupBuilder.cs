using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Assembles a <see cref="TriangleSoup"/> from authored pieces — boxes, quads,
/// and raw triangle batches. It keeps no floor/structure split, because the
/// soup has none: what can be rested on and what blocks are read from each
/// triangle's own normal, and what can be WALKED is the navmesh's answer.
/// Used by test fixtures and any tool that composes realm geometry in code;
/// the scene bake hands in world-space triangles it sampled from meshes.
///
/// Winding matters, and only here: a surface must be wound counter-clockwise
/// seen from above, or its normal points at the floor and both the soup and
/// Recast's slope filter read it as an overhang. The helpers below keep that
/// straight for the shapes they build.
/// </summary>
public sealed class SoupBuilder
{
    private readonly List<float> _verts = new();
    private readonly List<int> _tris = new();

    /// <summary>An axis-aligned box, all twelve triangles wound outward.</summary>
    public SoupBuilder AddBox(Aabb box)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var k = 0; k < 8; k++)
            corners[k] = LocalCorner(k, (box.Max - box.Min) * 0.5f) + box.Center;
        return AddBoxCorners(corners);
    }

    /// <summary>
    /// A box given its 8 world-space corners in the builder's ring order
    /// (see <see cref="LocalCorner"/>): 0-3 the bottom ring, 4-7 the top,
    /// both wound min→max. This is how a ROTATED slab enters the soup — its
    /// corners transformed by the caller, the outward winding preserved by
    /// any proper rotation.
    /// </summary>
    public SoupBuilder AddBoxCorners(ReadOnlySpan<Vector3> corners)
    {
        if (corners.Length != 8)
            throw new ArgumentException($"a box has 8 corners, got {corners.Length}");
        var baseVertex = _verts.Count / 3;
        foreach (var c in corners)
        {
            _verts.Add(c.X);
            _verts.Add(c.Y);
            _verts.Add(c.Z);
        }
        ReadOnlySpan<int> faces = stackalloc int[]
        {
            0, 1, 2,  0, 2, 3,  // bottom (-Y)
            4, 7, 5,  5, 7, 6,  // top (+Y) — the face feet ride when this slab is ground
            0, 4, 1,  1, 4, 5,  // z-min
            3, 2, 6,  3, 6, 7,  // z-max
            0, 3, 4,  3, 7, 4,  // x-min
            1, 5, 2,  2, 5, 6,  // x-max
        };
        foreach (var f in faces)
            _tris.Add(baseVertex + f);
        return this;
    }

    /// <summary>Corner k of a box centred at the origin with the given half-size,
    /// in the ring order <see cref="AddBoxCorners"/> expects.</summary>
    public static Vector3 LocalCorner(int k, Vector3 half) => new(
        k is 1 or 2 or 5 or 6 ? half.X : -half.X,
        k < 4 ? -half.Y : half.Y,
        k is 2 or 3 or 6 or 7 ? half.Z : -half.Z);

    /// <summary>
    /// A surface quad (two triangles), corners in ring order, wound so its
    /// normal points at the sky — a ramp authored corner-by-corner reads as
    /// ground either way it was handed in. A quad standing on edge is left
    /// where it is by the flip and stays the wall it is.
    /// </summary>
    public SoupBuilder AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        if (Vector3.Cross(b - a, d - a).Y < 0f)
            (b, d) = (d, b);
        var baseVertex = _verts.Count / 3;
        foreach (var v in (ReadOnlySpan<Vector3>)[a, b, c, d])
        {
            _verts.Add(v.X);
            _verts.Add(v.Y);
            _verts.Add(v.Z);
        }
        _tris.AddRange([baseVertex, baseVertex + 1, baseVertex + 3, baseVertex + 1, baseVertex + 2, baseVertex + 3]);
        return this;
    }

    /// <summary>A batch of world-space triangles (the scene bake's bulk path).</summary>
    public SoupBuilder AddTriangles(ReadOnlySpan<float> vertices, ReadOnlySpan<int> triangles)
    {
        var baseVertex = _verts.Count / 3;
        foreach (var v in vertices)
            _verts.Add(v);
        foreach (var t in triangles)
            _tris.Add(baseVertex + t);
        return this;
    }

    /// <summary>
    /// Assemble the soup, welding vertices as it goes. Everything upstream
    /// hands over corner positions per FACE — a box arrives with its eight
    /// corners repeated across twelve triangles, and the engine bake, which
    /// reads triangles straight out of Godot, shares nothing at all — so a
    /// realm ships three unique vertices per triangle where a handful serve
    /// dozens. Matching is exact-bit, never epsilon: those corners are
    /// computed by the same expression, so they agree to the last bit, and an
    /// exact test cannot make the result depend on the order they arrived in.
    /// The triangles are untouched as geometry; only how they name their
    /// corners changes.
    /// </summary>
    public TriangleSoup Build()
    {
        var welded = new Dictionary<(float X, float Y, float Z), int>(_verts.Count / 3);
        var verts = new List<float>(_verts.Count);
        var tris = new int[_tris.Count];
        for (var i = 0; i < _tris.Count; i++)
        {
            var v = _tris[i] * 3;
            var corner = (_verts[v], _verts[v + 1], _verts[v + 2]);
            if (!welded.TryGetValue(corner, out var index))
            {
                index = welded.Count;
                welded[corner] = index;
                verts.Add(corner.Item1);
                verts.Add(corner.Item2);
                verts.Add(corner.Item3);
            }
            tris[i] = index;
        }
        return new TriangleSoup(verts.ToArray(), tris);
    }
}

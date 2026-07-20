using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Assembles a <see cref="TriangleSoup"/> from authored pieces — boxes, quads,
/// and raw triangle batches — keeping the floor/structure split the soup's
/// queries depend on. Floors are what feet ride (terraces, room floors,
/// ramps); structure is what blocks and occludes (walls, roofs, monuments).
/// Used by test fixtures and any tool that composes realm geometry in code;
/// the scene bake hands in world-space triangles it sampled from meshes.
/// </summary>
public sealed class SoupBuilder
{
    private readonly List<float> _floorVerts = new();
    private readonly List<int> _floorTris = new();
    private readonly List<float> _structureVerts = new();
    private readonly List<int> _structureTris = new();

    /// <summary>An axis-aligned box, all twelve triangles wound outward.</summary>
    public SoupBuilder AddBox(Aabb box, bool floor)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var k = 0; k < 8; k++)
            corners[k] = LocalCorner(k, (box.Max - box.Min) * 0.5f) + box.Center;
        return AddBoxCorners(corners, floor);
    }

    /// <summary>
    /// A box given its 8 world-space corners in the builder's ring order
    /// (see <see cref="LocalCorner"/>): 0-3 the bottom ring, 4-7 the top,
    /// both wound min→max. This is how a ROTATED slab enters the soup — its
    /// corners transformed by the caller, the outward winding preserved by
    /// any proper rotation.
    /// </summary>
    public SoupBuilder AddBoxCorners(ReadOnlySpan<Vector3> corners, bool floor)
    {
        if (corners.Length != 8)
            throw new ArgumentException($"a box has 8 corners, got {corners.Length}");
        var (verts, tris) = floor ? (_floorVerts, _floorTris) : (_structureVerts, _structureTris);
        var baseVertex = verts.Count / 3;
        foreach (var c in corners)
        {
            verts.Add(c.X);
            verts.Add(c.Y);
            verts.Add(c.Z);
        }
        ReadOnlySpan<int> faces = stackalloc int[]
        {
            0, 1, 2,  0, 2, 3,  // bottom (-Y)
            4, 7, 5,  5, 7, 6,  // top (+Y) — the walkable face when this is floor
            0, 4, 1,  1, 4, 5,  // z-min
            3, 2, 6,  3, 6, 7,  // z-max
            0, 3, 4,  3, 7, 4,  // x-min
            1, 5, 2,  2, 5, 6,  // x-max
        };
        foreach (var f in faces)
            tris.Add(baseVertex + f);
        return this;
    }

    /// <summary>Corner k of a box centred at the origin with the given half-size,
    /// in the ring order <see cref="AddBoxCorners"/> expects.</summary>
    public static Vector3 LocalCorner(int k, Vector3 half) => new(
        k is 1 or 2 or 5 or 6 ? half.X : -half.X,
        k < 4 ? -half.Y : half.Y,
        k is 2 or 3 or 6 or 7 ? half.Z : -half.Z);

    /// <summary>
    /// A surface quad (two triangles), corners in ring order. Floor quads are
    /// wound so their normal points up — a ramp authored corner-by-corner
    /// reads as walkable ground to the bake's slope filter either way it was
    /// handed in.
    /// </summary>
    public SoupBuilder AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, bool floor)
    {
        if (floor && Vector3.Cross(b - a, d - a).Y < 0f)
            (b, d) = (d, b); // flip the ring so the surface faces the sky
        var (verts, tris) = floor ? (_floorVerts, _floorTris) : (_structureVerts, _structureTris);
        var baseVertex = verts.Count / 3;
        foreach (var v in (ReadOnlySpan<Vector3>)[a, b, c, d])
        {
            verts.Add(v.X);
            verts.Add(v.Y);
            verts.Add(v.Z);
        }
        tris.AddRange([baseVertex, baseVertex + 1, baseVertex + 3, baseVertex + 1, baseVertex + 2, baseVertex + 3]);
        return this;
    }

    /// <summary>A batch of world-space triangles (the scene bake's bulk path).</summary>
    public SoupBuilder AddTriangles(ReadOnlySpan<float> vertices, ReadOnlySpan<int> triangles, bool floor)
    {
        var (verts, tris) = floor ? (_floorVerts, _floorTris) : (_structureVerts, _structureTris);
        var baseVertex = verts.Count / 3;
        foreach (var v in vertices)
            verts.Add(v);
        foreach (var t in triangles)
            tris.Add(baseVertex + t);
        return this;
    }

    /// <summary>Floors first, structure after — the order the soup's split relies on.</summary>
    public TriangleSoup Build()
    {
        var verts = new float[_floorVerts.Count + _structureVerts.Count];
        _floorVerts.CopyTo(verts);
        _structureVerts.CopyTo(verts.AsSpan(_floorVerts.Count));

        var floorVertexCount = _floorVerts.Count / 3;
        var tris = new int[_floorTris.Count + _structureTris.Count];
        _floorTris.CopyTo(tris);
        for (var i = 0; i < _structureTris.Count; i++)
            tris[_floorTris.Count + i] = _structureTris[i] + floorVertexCount;

        return new TriangleSoup(verts, tris, _floorTris.Count / 3);
    }
}

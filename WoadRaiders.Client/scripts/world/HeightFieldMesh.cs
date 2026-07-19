using System;
using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Turns a heightfield into a renderable mesh — pure geometry, no realm, no
/// node, no look of its own. One continuous smooth-shaded surface with normals
/// from central differences, and per-vertex colours from whatever function the
/// caller hands in.
///
/// Both routes to a realm's ground come through here: the offline scene builder
/// (a realm design's <see cref="RealmScene.AddTerrain(HeightField, Func{float, float, Color}, Material)"/>,
/// which bakes the result into the .tscn) and the <see cref="RealmTerrain"/>
/// node a hand-made scene may use, which rebuilds it on entering the tree. They
/// share this so both produce identical land from identical heights.
/// </summary>
public static class HeightFieldMesh
{
    /// <summary>One continuous smooth-shaded mesh over the whole heightfield.
    /// Each vertex is coloured by <paramref name="colour"/>, which is handed the
    /// height and the surface's upward tilt (normal.Y, 1 = flat) and decides what
    /// that ground looks like — this function has no opinion about it. Pair the
    /// result with a material that reads vertex colour (see
    /// TerrainSurface.Material), or the palette is discarded.</summary>
    public static ArrayMesh Build(float originX, float originZ, float cell, int w, int d, float[] heights,
                                  Func<float, float, Color> colour) =>
        Build(originX, originZ, cell, w, d, heights, (_, _, h, ny) => colour(h, ny));

    /// <summary>The position-aware variant: <paramref name="colour"/> is handed
    /// the vertex's world (x, z) as well as its height and upward tilt, for
    /// palettes that vary by PLACE — a realm tinting one chamber's floor apart
    /// from another's at the same depth.</summary>
    public static ArrayMesh Build(float originX, float originZ, float cell, int w, int d, float[] heights,
                                  Func<float, float, float, float, Color> colour)
    {
        var vertices = new Vector3[w * d];
        var normals = new Vector3[w * d];
        var colors = new Color[w * d];

        for (var j = 0; j < d; j++)
        {
            for (var i = 0; i < w; i++)
            {
                var idx = j * w + i;
                var h = heights[idx];
                vertices[idx] = new Vector3(originX + i * cell, h, originZ + j * cell);

                // Central differences (clamped at the rim) give smooth normals.
                var hw = heights[j * w + Math.Max(i - 1, 0)];
                var he = heights[j * w + Math.Min(i + 1, w - 1)];
                var hn = heights[Math.Max(j - 1, 0) * w + i];
                var hs = heights[Math.Min(j + 1, d - 1) * w + i];
                var normal = new Vector3(hw - he, 2f * cell, hn - hs).Normalized();
                normals[idx] = normal;
                colors[idx] = colour(originX + i * cell, originZ + j * cell, h, normal.Y);
            }
        }

        var indices = new int[(w - 1) * (d - 1) * 6];
        var k = 0;
        for (var j = 0; j < d - 1; j++)
        {
            for (var i = 0; i < w - 1; i++)
            {
                var a = j * w + i;         // clockwise winding — Godot front faces
                indices[k++] = a;
                indices[k++] = a + 1;
                indices[k++] = a + w;
                indices[k++] = a + 1;
                indices[k++] = a + w + 1;
                indices[k++] = a + w;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Color] = colors;
        arrays[(int)Mesh.ArrayType.Index] = indices;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}

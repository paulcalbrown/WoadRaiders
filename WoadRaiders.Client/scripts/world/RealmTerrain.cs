using System;
using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The terrain of an open realm as a scene node: it stores the heightfield
/// (origin, cell size, and the row-major height samples) in the .tscn and
/// builds its smooth vertex-coloured mesh when it enters the tree — in the
/// editor (it is a tool script, so map makers see the land while placing
/// things) and in the game alike. The mesh lives on an owner-less child, so
/// saving the scene never bakes megabytes of vertices into the file.
///
/// Where it is used: the client's from-geometry fallback (a served map whose
/// scene this build lacks) constructs one directly from the wire heightfield,
/// and hand-made realm scenes MAY use one as their terrain (Core.
/// DungeonSceneFile reads its stored properties). The GENERATED realm scenes
/// no longer carry it — they are built-in-nodes-only: a shader-displaced
/// PlaneMesh for the visual and root metadata for the simulation heights.
/// The node and its mesh join the "no_fade" group — the land is what everything
/// stands on; the occlusion fader must never dissolve it.
/// </summary>
[Tool]
public partial class RealmTerrain : Node3D
{
    [Export] public float OriginX { get; set; }
    [Export] public float OriginZ { get; set; }
    [Export] public float CellSize { get; set; } = 40f;
    [Export] public int TerrainWidth { get; set; }
    [Export] public int TerrainDepth { get; set; }

    /// <summary>Row-major height samples ([z * TerrainWidth + x]), world-space Y.</summary>
    [Export] public float[] Heights { get; set; } = Array.Empty<float>();

    private const string MeshChildName = "GeneratedTerrainMesh";

    public override void _Ready()
    {
        AddToGroup("no_fade");
        Rebuild();
    }

    /// <summary>Build (or rebuild) the terrain mesh child from the stored heights.</summary>
    public void Rebuild()
    {
        if (GetNodeOrNull(MeshChildName) is Node stale)
            stale.QueueFree();

        if (TerrainWidth < 2 || TerrainDepth < 2 || Heights.Length != TerrainWidth * TerrainDepth)
        {
            if (Heights.Length > 0)
                GD.PushWarning($"RealmTerrain '{Name}': expected {TerrainWidth}x{TerrainDepth} " +
                               $"= {TerrainWidth * TerrainDepth} heights, got {Heights.Length} — not building");
            return;
        }

        var child = new MeshInstance3D
        {
            Name = MeshChildName,
            Mesh = BuildMesh(OriginX, OriginZ, CellSize, TerrainWidth, TerrainDepth, Heights),
            MaterialOverride = TerrainMaterial(),
        };
        AddChild(child);           // no Owner: the editor never saves the generated mesh
        child.AddToGroup("no_fade");
    }

    /// <summary>One continuous smooth-shaded mesh over the whole heightfield, coloured
    /// per vertex by height and steepness — grass in the glens, heather on the moor,
    /// bare rock where it's too steep to walk, dark stone in the gorge.</summary>
    public static ArrayMesh BuildMesh(float originX, float originZ, float cell, int w, int d, float[] heights)
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
                colors[idx] = TerrainColor(h, normal.Y);
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

    public static Color TerrainColor(float height, float normalY)
    {
        // The height bands of the realm, dusk-lit: gorge stone, glen grass,
        // heather moor, pale upland, then bare crag on the border peaks.
        var gorge = new Color(0.14f, 0.13f, 0.15f);
        var glen = new Color(0.22f, 0.32f, 0.16f);
        var moor = new Color(0.30f, 0.27f, 0.18f);
        var upland = new Color(0.33f, 0.33f, 0.23f);
        var crag = new Color(0.33f, 0.32f, 0.35f);

        var byHeight = height switch
        {
            < -60f => gorge,
            < 20f => gorge.Lerp(glen, (height + 60f) / 80f),
            < 140f => glen.Lerp(moor, (height - 20f) / 120f),
            < 280f => moor.Lerp(upland, (height - 140f) / 140f),
            < 420f => upland.Lerp(crag, (height - 280f) / 140f),
            _ => crag,
        };

        // Steep ground sheds its soil: blend toward bare rock as the surface
        // tips past walkable — cliffs read as cliffs at a glance.
        var rockiness = Mathf.Clamp((0.80f - normalY) / 0.35f, 0f, 1f);
        return byHeight.Lerp(crag * 0.9f, rockiness);
    }

    public static StandardMaterial3D TerrainMaterial()
    {
        // Vertex colours carry the biome; a seamless world-triplanar noise pair
        // breaks them up so the ground reads as land, not as a gradient.
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.78f, 0.76f, 0.72f));
        ramp.SetColor(1, new Color(1.06f, 1.05f, 1.02f));

        var albedoNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.04f, Seed = 11 };
        var albedo = new NoiseTexture2D { Noise = albedoNoise, Width = 256, Height = 256, Seamless = true, ColorRamp = ramp };

        var normalNoise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin, Frequency = 0.07f, Seed = 12 };
        var normal = new NoiseTexture2D
        {
            Noise = normalNoise, Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 4f,
        };

        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            Roughness = 1f,
            Metallic = 0f,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.010f, 0.010f, 0.010f),
        };
    }
}

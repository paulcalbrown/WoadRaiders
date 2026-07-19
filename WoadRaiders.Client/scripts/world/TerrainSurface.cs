using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The material half of vertex-coloured ground. <see cref="HeightFieldMesh"/>
/// bakes a colour into every vertex; this is what reads them back, and without
/// a material that does, the palette is silently discarded.
///
/// It carries no palette of its own — only near-white detail noise, multiplied
/// over whatever colour the mesh holds, so ground reads as land rather than a
/// smooth gradient. That is what makes it shareable: a realm states its own
/// colours and still uses this. The noise is world-triplanar at this game's
/// scale, so it tiles seamlessly across a realm of any size.
/// </summary>
public static class TerrainSurface
{
    public static StandardMaterial3D Material()
    {
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

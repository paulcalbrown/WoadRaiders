using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Cut-stone surfaces for slab-built realms — procedural, so a realm carries
/// its whole look in its own .tscn with no texture files to ship or license.
/// Each surface is a StandardMaterial3D over two NoiseTexture2D layers (colour
/// and relief) driven by seeded FastNoiseLite, which Godot's serializer writes
/// out as ordinary sub-resources and regenerates on load.
///
/// Mapping is WORLD triplanar, and that is the load-bearing choice: a realm's
/// slabs range from a stair tread to a hall floor, and BoxMesh UVs stretch to
/// whatever they are told to cover — so per-mesh UVs would give every stone a
/// different grain size. Triplanar in world space fixes the texel density to
/// the WORLD instead, so one texture scale reads identically on a tread and on
/// a roof, and slabs meeting at a corner share a continuous grain.
///
/// Seeds are explicit and fixed: the realm regenerates byte-identically, which
/// the build pipeline's normalize-and-diff step depends on.
/// </summary>
public static class StoneSurfaces
{
    private const int TextureSize = 512;

    /// <summary>
    /// Dressed stone: mottled colour, shallow relief. <paramref name="grain"/>
    /// is how many world units one tile of the texture covers — smaller means
    /// finer stone. <paramref name="seed"/> keeps two surfaces from sharing the
    /// same blotches.
    /// </summary>
    public static StandardMaterial3D Cut(Color tint, float grain, int seed,
                                         float roughness = 0.92f, float relief = 1.4f) =>
        new()
        {
            AlbedoColor = tint,
            AlbedoTexture = Mottle(seed),
            NormalEnabled = true,
            NormalTexture = Relief(seed + 1),
            NormalScale = relief,
            Roughness = roughness,
            Metallic = 0f,
            // World triplanar: texel density belongs to the realm, not the slab.
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = Vector3.One / grain,
        };

    /// <summary>The colour layer — broad blotching, so a wall reads as many
    /// stones rather than one poured surface.</summary>
    private static NoiseTexture2D Mottle(int seed) => new()
    {
        Width = TextureSize,
        Height = TextureSize,
        Seamless = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Seed = seed,
            Frequency = 0.006f,
            FractalOctaves = 5,
            FractalGain = 0.52f,
        },
        // Damp the swing: stone varies, it does not strobe.
        ColorRamp = Ramp(new Color(0.62f, 0.62f, 0.62f), new Color(1.08f, 1.08f, 1.08f)),
    };

    /// <summary>The relief layer — tighter noise, read as a normal map, giving
    /// the pitted tooling that catches torchlight at a grazing angle.</summary>
    private static NoiseTexture2D Relief(int seed) => new()
    {
        Width = TextureSize,
        Height = TextureSize,
        Seamless = true,
        AsNormalMap = true,
        BumpStrength = 5.5f,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Seed = seed,
            Frequency = 0.018f,
            FractalOctaves = 4,
            FractalGain = 0.58f,
        },
    };

    private static Gradient Ramp(Color low, Color high) => new()
    {
        Offsets = new[] { 0f, 1f },
        Colors = new[] { low, high },
    };
}

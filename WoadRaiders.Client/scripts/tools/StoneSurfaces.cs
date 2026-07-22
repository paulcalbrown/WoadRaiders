using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Cut-stone surfaces for built realms — procedural, so a realm carries its
/// whole look in its own .tscn with no texture files to ship or license.
/// Each surface is a StandardMaterial3D over noise layers driven by seeded
/// FastNoiseLite, which Godot's serializer writes out as ordinary sub-resources
/// and regenerates on load.
///
/// Mapping is WORLD triplanar, and that is the load-bearing choice: the surfaces
/// a realm dresses range from a stair tread to a hall floor, so per-mesh UVs
/// would give every stone a different grain size. Triplanar in world space fixes
/// the texel density to the WORLD instead, so one texture scale reads
/// identically on a tread and on a roof, and two stones meeting at a corner
/// share a continuous grain.
///
/// THREE THINGS HERE ARE MEASURED RATHER THAN CHOSEN, and each was wrong before:
///
///   GRAIN. Texel density is <c>size × unitsPerMetre / grain</c> px/m. The first
///   Crypt ran grain 220–470 at 512², i.e. 27–58 px/m against a ~512 px/m
///   convention — ten to twenty times under, which is why its stone read as
///   smooth grey. The base layer now runs at <see cref="BaseGrain"/>.
///
///   THE SECOND SCALE. One layer cannot be both coarse blotching and fine
///   tooling. The detail layer therefore rides UV2 with its OWN world-triplanar
///   scale; left on UV1 it would reuse the base scale and add no new frequency
///   at all, which is the trap in Godot's naming.
///
///   THE NOISE ITSELF. Cellular cell-value under MANHATTAN distance is the one
///   configuration that reads as many separate stones rather than as poured
///   concrete — Manhattan is what makes the cells blocky. Coursed masonry it
///   cannot do at any setting, so courses live in GEOMETRY (see
///   CryptDesign.Masonry) and this supplies grain only.
///
/// Parallax is deliberately absent: Godot silently disables height mapping
/// whenever UV1 triplanar is on, so setting it would be dead weight that reads
/// like a working feature.
///
/// Seeds are explicit and fixed: the realm regenerates byte-identically, which
/// the build pipeline's normalize-and-diff step depends on.
/// </summary>
public static class StoneSurfaces
{
    private const int TextureSize = 512;

    /// <summary>World units one tile of the BASE layer covers. 160 u ≈ 6.7 m at
    /// the realm's 24 u/m, giving ~77 px/m at 512² — coarse on purpose, because
    /// the fine frequency is the detail layer's job.</summary>
    public const float BaseGrain = 160f;

    /// <summary>And the DETAIL layer's, an order finer: chisel marks and pitting.</summary>
    public const float DetailGrain = 18f;

    /// <summary>
    /// Dressed stone. <paramref name="tint"/> is the surface's own colour,
    /// <paramref name="seed"/> keeps two surfaces from sharing blotches, and
    /// <paramref name="relief"/> scales the normal map — raise it for rough
    /// drystone, drop it for a dressed ashlar face.
    /// </summary>
    public static StandardMaterial3D Cut(Color tint, int seed, float roughness = 0.92f, float relief = 1.4f,
                                         float grain = BaseGrain) =>
        new()
        {
            AlbedoColor = tint,
            AlbedoTexture = Mottle(seed),
            NormalEnabled = true,
            NormalTexture = Relief(seed + 1),
            NormalScale = relief,
            Roughness = roughness,
            Metallic = 0f,

            // Ambient occlusion is invisible at Godot's default here: it only
            // darkens INDIRECT light, and this realm is lit almost entirely by
            // direct flame. Letting it bite the direct term is what makes a
            // recessed joint read under a torch.
            AOEnabled = true,
            AOTexture = Mottle(seed + 2),
            AOTextureChannel = BaseMaterial3D.TextureChannel.Red,
            AOLightAffect = 0.4f,

            // World triplanar: texel density belongs to the realm, not the mesh.
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = Vector3.One / grain,
            // Sharpen the projection blend so each face commits to one axis.
            // The default averages three projections, which smears a normal map
            // wherever a surface is not axis-aligned — every vault and arch here.
            Uv1TriplanarSharpness = 4f,

            // The second scale, on its OWN triplanar projection.
            DetailEnabled = true,
            // NOTE the capitalisation: Godot's own C# binding generates
            // `Uv1Scale` but `UV2Scale`, so writing Uv2* by analogy does not
            // compile. Not a typo below.
            DetailUVLayer = BaseMaterial3D.DetailUV.UV2,
            DetailBlendMode = BaseMaterial3D.BlendModeEnum.Mul,
            DetailAlbedo = Mottle(seed + 3),
            DetailNormal = Relief(seed + 4),
            DetailMask = Mottle(seed + 5),
            UV2Triplanar = true,
            UV2WorldTriplanar = true,
            UV2Scale = Vector3.One / DetailGrain,

            // A chase camera looking down a long hall is the worst case for
            // isotropic mipmapping; anisotropic filtering is near-free on desktop.
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,

            // Vertex colour is how a single library piece becomes many stones:
            // every voussoir and drystone slab carries its own tint, which is
            // the variation a tiling texture cannot give.
            VertexColorUseAsAlbedo = true,
        };

    /// <summary>
    /// Photographed stone: a vendored CC0 PBR set (spec LOOK-001) under exactly
    /// the projection, detail layer and vertex tint the procedural surfaces use,
    /// so the two are interchangeable and a surface can be swapped either way
    /// without touching the design that places it.
    ///
    /// <paramref name="surface"/> names a directory under assets/crypt/pbr/, and
    /// the files inside follow one of two conventions because the two CC0 sources
    /// do:
    ///   Poly Haven ships `_arm` — AO in RED, roughness in GREEN, metallic in
    ///   BLUE, one texture doing three jobs and one fetch instead of three.
    ///   ambientCG ships them apart, as `_rough` and `_ao`.
    /// Both are handled rather than normalised, because rewriting a vendored file
    /// would break the checksum that is the whole point of recording provenance.
    ///
    /// The DETAIL layer stays procedural even here. A 2K photograph over 240
    /// world units is ~205 px/m, which is plenty for a wall seen across a room
    /// and nowhere near enough for one a raider is standing against — and no
    /// resolution fixes the other half of it, which is that a photograph tiles
    /// and stone does not. The detail layer and the per-stone vertex tint are
    /// what break the repeat.
    /// </summary>
    public static StandardMaterial3D Photographic(string surface, int seed, float grain = 240f,
                                                  float relief = 1.0f, Color? tint = null)
    {
        var dir = $"res://assets/crypt/pbr/{surface}";
        var material = Cut(tint ?? Colors.White, seed, relief: relief, grain: grain);

        material.AlbedoTexture = Load($"{dir}/{surface}_diff.jpg");
        material.NormalTexture = Load($"{dir}/{surface}_nor.jpg");

        if (Load($"{dir}/{surface}_arm.jpg") is { } arm)
        {
            material.AOTexture = arm;
            material.AOTextureChannel = BaseMaterial3D.TextureChannel.Red;
            material.RoughnessTexture = arm;
            material.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Green;
            material.MetallicTexture = arm;
            material.MetallicTextureChannel = BaseMaterial3D.TextureChannel.Blue;
            // The map carries the metallic answer, so the scalar has to be 1 or it
            // multiplies the whole channel to nothing. Stone answers zero anyway;
            // the rusted iron in the realm does not.
            material.Metallic = 1f;
        }
        else
        {
            material.AOTexture = Load($"{dir}/{surface}_ao.jpg");
            material.AOTextureChannel = BaseMaterial3D.TextureChannel.Red;
            material.RoughnessTexture = Load($"{dir}/{surface}_rough.jpg");
            material.RoughnessTextureChannel = BaseMaterial3D.TextureChannel.Red;
        }

        // Roughness now comes from the map, so the scalar must not scale it down.
        material.Roughness = 1f;
        return material;
    }

    /// <summary>
    /// A vendored texture, or null if it is not there. Null rather than throwing:
    /// the PBR sets are large binaries and a checkout that has not fetched them
    /// should still build a realm that looks like stone, on the procedural layers
    /// underneath. A missing texture is a duller Crypt, not a broken one.
    /// </summary>
    private static Texture2D? Load(string path) =>
        ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;

    /// <summary>
    /// The colour layer: flat-toned irregular blocks, each one stone. Cellular
    /// CELL VALUE under MANHATTAN distance — cell value gives each cell a single
    /// flat tone instead of a gradient, and Manhattan makes the cells rectilinear
    /// rather than organic blobs.
    /// </summary>
    private static NoiseTexture2D Mottle(int seed) => new()
    {
        Width = TextureSize,
        Height = TextureSize,
        Seamless = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue,
            CellularDistanceFunction = FastNoiseLite.CellularDistanceFunctionEnum.Manhattan,
            CellularJitter = 0.45f,
            Seed = seed,
            Frequency = 0.03f,
            FractalType = FastNoiseLite.FractalTypeEnum.None,
        },
        // Damp the swing: stone varies, it does not strobe.
        ColorRamp = Ramp(new Color(0.66f, 0.66f, 0.66f), new Color(1.10f, 1.10f, 1.10f)),
    };

    /// <summary>The relief layer — RIDGED fractal noise read as a normal map,
    /// giving the cracked, chiselled tooling that catches flame at a grazing
    /// angle. Ridged rather than smooth because stone breaks along lines.</summary>
    private static NoiseTexture2D Relief(int seed) => new()
    {
        Width = TextureSize,
        Height = TextureSize,
        Seamless = true,
        AsNormalMap = true,
        BumpStrength = 5.5f,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
            FractalType = FastNoiseLite.FractalTypeEnum.Ridged,
            Seed = seed,
            Frequency = 0.02f,
            FractalOctaves = 5,
            FractalGain = 0.55f,
        },
    };

    private static Gradient Ramp(Color low, Color high) => new()
    {
        Offsets = new[] { 0f, 1f },
        Colors = new[] { low, high },
    };
}

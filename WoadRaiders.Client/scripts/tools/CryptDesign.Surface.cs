using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The world the Crypt is sunk into — a starry sky, and the grass hillside the
/// whole complex was dug down through.
///
/// The realm has genuinely open chambers: the Minster's nave has no roof over
/// its vessel, and the Fault is a pit cut into daylight. Until now both looked
/// up into a flat near-black colour, which reads as nothing at all rather than
/// as night. A raider who can see the sky from the bottom of a crypt knows how
/// deep they are, and that is worth more than any amount of stonework.
///
/// THE SURFACE HAS HOLES IN IT, and that is the whole difficulty. A slab laid
/// over the realm would roof the two spaces that are supposed to be open — and
/// worse, it would give the chase camera a ceiling to duck under exactly where
/// the design promises it none (PLAY-001). So it is built as strips that step
/// around the openings, and every piece of it is PASSABLE: nobody can reach it,
/// it is pure backdrop, and keeping it out of the bake costs the navmesh and the
/// wire nothing.
/// </summary>
public sealed partial class CryptDesign
{
    /// <summary>
    /// Where the grass sits. The Minster's walls rise 820 from its floor, so the
    /// surface meets their heads: the church was dug down from here, and its
    /// wall tops are what is left level with the field.
    /// </summary>
    private const float SurfaceY = 820f;

    private const float SurfaceThick = 40f;

    /// <summary>
    /// The night sky, GENERATED rather than photographed: near-black with
    /// pinpoint stars.
    ///
    /// A real HDRI panorama was here first (Poly Haven's qwantani_night_puresky,
    /// CC0) and it was the wrong tool. A photograph of the night sky is mostly
    /// AIRGLOW — a broad, low, continuous luminance with the Milky Way as a wide
    /// bright band — and that glow leaked through every crack in the masonry as
    /// grey daylight. Crushing the exposure does not separate them, because it
    /// takes the stars down with the glow: they are the same image.
    ///
    /// Generated, the two are independent. The background is set to near-black
    /// and the stars are written as individual pixels well above 1.0, so they
    /// cross GlowHdrThreshold and bloom while the sky behind them contributes
    /// essentially nothing through a gap in a wall.
    ///
    /// Stars are distributed EVENLY ON THE SPHERE, not evenly on the image. An
    /// equirectangular map devotes as many pixels to the two poles as to the
    /// whole equator, so uniform sampling in image space heaps the entire
    /// starfield into two dense caps directly overhead and underfoot.
    /// </summary>
    private Texture2D Starfield()
    {
        // FINER, and FEWER. A star's apparent SIZE has three causes and only one
        // is the texture: the glow BLOOM off an over-bright pixel (the big one),
        // the neighbour-CROSS that was drawn on the brightest, and the texel
        // MAGNIFICATION of a coarse map. The first two are killed below; the map
        // goes to 4096 to halve the last, which is as far as it can without the
        // file becoming absurd — a star texture is one giant field of identical
        // background pixels stored raw, so 8192 Rgbah would be 268 MB on disk.
        // DENSITY is just the count, which was 5,200 (a snowstorm); 1,400 over
        // the whole sphere is a sky.
        const int W = 4096, H = 2048;
        const int Stars = 1400;

        // Rgbe9995: HDR in FOUR bytes a pixel, not the eight Rgbah costs, via a
        // shared exponent. A star still exceeds 1.0 and still blooms; the file is
        // 33 MB rather than 67. A byte format cannot hold values over 1.0 at all,
        // so the bloom would have nothing to work with.
        var image = Image.CreateEmpty(W, H, false, Image.Format.Rgbe9995);
        image.Fill(new Color(0.0015f, 0.0020f, 0.0045f));

        for (var i = 0; i < Stars; i++)
        {
            // Uniform on the sphere: longitude is flat, but latitude has to come
            // through acos or the poles crowd.
            var u = Hash(i, 0, 1201);
            var v = Mathf.Acos(1f - 2f * Hash(i, 1, 1203)) / Mathf.Pi;
            var x = Mathf.Clamp((int)(u * W), 0, W - 1);
            var y = Mathf.Clamp((int)(v * H), 0, H - 1);

            // A steep magnitude curve: mostly faint, a few genuinely bright. The
            // CEILING is what the glow reacts to — at 7.5 the brightest bloomed
            // into soft discs the size of a fist. 3.2 still crosses the HDR
            // threshold and twinkles, without spreading.
            var m = Hash(i, 2, 1205);
            var brightness = 0.30f + Mathf.Pow(m, 7f) * 2.9f;

            // Blue-white through to amber, the way real stars actually vary.
            var warm = Hash(i, 3, 1207);
            var colour = new Color(brightness * (0.82f + warm * 0.18f),
                                   brightness * (0.86f + warm * 0.10f),
                                   brightness * (1.00f - warm * 0.22f));
            // ONE texel. No neighbour-cross: on an 8K map a cross is a plus-sign
            // four texels wide, which was half of why the stars looked like
            // snowflakes. A single bright point plus a little bloom is a star.
            image.SetPixel(x, y, colour);
        }

        return _scene.SharedResource(Name, "night_sky", ImageTexture.CreateFromImage(image));
    }

    private void Surface()
    {
        var grass = StoneSurfaces.Photographic("grass", seed: 3301, grain: 260f, relief: 1.6f,
                                               tint: new Color(0.62f, 0.68f, 0.52f));
        var folder = _scene.DeclarePassable(_scene.Folder("Surface"));

        // The two openings the sky has to come through: the nave's vessel (the
        // aisles either side ARE roofed, so only the middle is open) and the
        // Fault's pit.
        var nave = Named("B2");
        var pit = Named("B4");

        // Strips across x, splitting in z only where a hole falls in that strip.
        // A general polygon-with-holes would be the clever answer and a much
        // worse one: two rectangles do not need a tessellator.
        const float W = -1600f, E = 8900f, N = -600f, S = 5600f;
        var naveN = nave.Z0 + 400f;   // the aisles' inner edge
        var naveS = nave.Z1 - 400f;

        void Slab(float x0, float z0, float x1, float z1, string name)
        {
            if (x1 - x0 < 1f || z1 - z0 < 1f)
                return;
            var node = BoxKit.Floor(_scene, Box(x0, SurfaceY - SurfaceThick, z0, x1, SurfaceY, z1),
                                    grass, name);
            node.Reparent(folder, keepGlobalTransform: false);
        }

        Slab(W, N, nave.X0, S, "Surface_West");
        Slab(nave.X0, N, nave.X1, naveN, "Surface_NaveNorth");
        Slab(nave.X0, naveS, nave.X1, S, "Surface_NaveSouth");
        Slab(nave.X1, N, pit.X0, S, "Surface_Middle");
        Slab(pit.X0, N, pit.X1, pit.Z0, "Surface_PitNorth");
        Slab(pit.X0, pit.Z1, pit.X1, S, "Surface_PitSouth");
        Slab(pit.X1, N, E, S, "Surface_East");
    }
}

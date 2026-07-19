using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's DESIGN — the second <see cref="IRealmDesign"/>, and an INDOOR
/// one: a sprawling burial complex carved downward into solid rock. Where the
/// Crag's wild default is open highland with plates carved into it, the
/// Crypt's wild default is the unexcavated rock MASS — a dark roof-height
/// plateau — and every room and gallery is a plate sunk into it, so walls
/// arise from the carving itself and the realm is sealed by construction.
/// The rock mass is not one flat slab: its top follows the rooms below (a
/// widened "envelope" of the same plates, raised by a roof height), so the
/// chase camera riding above it stays close to whichever hall the raider
/// walks.
///
/// The layout descends: entrance undercroft → a stepped stair (real treads,
/// quantized into the heightfield) → the Hall of the Dead (hub rotunda) →
/// north to an ossuary with a bone-crawl shortcut, south past a flooded
/// cloister to a noble family's crypt → the Processional east, broken mid-way
/// by a collapsed chasm (a charnel pit with its own ambush and a scree ramp
/// out) → a catacomb maze of narrow criss-cross galleries → a candle chapel —
/// then the deep stair, the antechamber of the kings, and the Mausoleum: the
/// boss's pillared court at the bottom of the world. Deterministic: same
/// numbers, same realm, every run (hash noise, no framework RNG).
///
/// Like CragDesign, this lives CLIENT-SIDE next to the scene builder: the
/// simulation-relevant parts (terrain, collision, markers) follow the bake
/// conventions, and everything else — the masonry, the dead, the candle seas —
/// is pure scenery the bake never needs to understand.
/// </summary>
public sealed partial class CryptDesign : IRealmDesign
{
    public string Name => "Crypt";

    /// <summary>Compose the realm: the dark, then the stone, then what stands
    /// in it, then the cast, then the dressing. Node order is scene order.</summary>
    public RealmScene Build()
    {
        var scene = new RealmScene();

        // The Crypt's own dark: black void, cold ambient, torch-fed fog
        // (CryptDesign.Scenery.cs).
        DressWithDark(scene);

        var field = BuildHeightField();
        // Crypt colours are region-aware (ossuary bone-dust, cloister moss,
        // mausoleum cold), so the palette reads position as well as height.
        scene.AddTerrain(field, CryptColour, CryptSurface());
        scene.AddSolids(Solids(field), Masonry());

        DressWithLights(scene);

        scene.SetPlayerSpawn(scene.OnGround(PlayerSpawn.X, PlayerSpawn.Z));
        foreach (var enemy in Enemies)
            scene.AddEnemy(enemy.Type, scene.OnGround(enemy.X, enemy.Z));
        scene.SetBossSpawn(scene.OnGround(BossSpawn.X, BossSpawn.Z));

        // ---- pure scenery, past here: nothing the simulation ever sees. ----
        DressWithMasonry(scene);
        DressWithTheDead(scene);
        DressWithRelics(scene);

        return scene;
    }

    private const float Cell = 40f;          // world units between height samples
    private const int W = 107, D = 101;      // samples: the realm is 4240 x 4000 world units
    private const int Seed = 269;

    /// <summary>How far the unexcavated rock roof rises above the rooms carved
    /// beneath it — the implied vault height of the complex.</summary>
    private const float RoofRise = 190f;

    // ------------------------------------------------------------- noise

    private static float Hash(int x, int z, int salt)
    {
        var h = unchecked((uint)(x * 374761393 + z * 668265263 + salt * 974711 + Seed * 144665));
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000; // 0..1
    }

    private static float ValueNoise(float x, float z, float wavelength, int salt)
    {
        var fx = x / wavelength;
        var fz = z / wavelength;
        var x0 = (int)MathF.Floor(fx);
        var z0 = (int)MathF.Floor(fz);
        var tx = fx - x0;
        var tz = fz - z0;
        tx = tx * tx * (3f - 2f * tx); // smoothstep the lattice lerp
        tz = tz * tz * (3f - 2f * tz);
        var n = float.Lerp(float.Lerp(Hash(x0, z0, salt), Hash(x0 + 1, z0, salt), tx),
                           float.Lerp(Hash(x0, z0 + 1, salt), Hash(x0 + 1, z0 + 1, salt), tx), tz);
        return n * 2f - 1f; // -1..1
    }

    private static float Fractal(float x, float z, float wavelength, int octaves, int salt)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (var o = 0; o < octaves; o++)
        {
            sum += ValueNoise(x, z, wavelength, salt + o * 131) * amp;
            norm += amp;
            wavelength *= 0.5f;
            amp *= 0.5f;
        }
        return sum / norm; // -1..1
    }

    // ------------------------------------------------------------- the stone
    // A plate is a carved room or gallery: a capsule (A→B) or disc (A==B)
    // whose floor runs Ha→Hb along its axis, fading over Blend into the rock.
    // Step > 0 quantizes the floor profile into real stair treads (each tread
    // lands on the sample grid, so the rise per tread must stay well under
    // SimConstants.StepHeight for the stairs to be climbable).

    private readonly record struct Plate(
        Vector2 A, Vector2 B, float R, float Ha, float Hb, float Blend, float Step = 0f);

    private static readonly Plate[] Rooms =
    {
        // The Entrance Undercroft — the spawn hall, lit by the collapsed shaft.
        new(new(400, 1800), new(640, 1800), 220f, 0f, 0f, 70f),
        // The Descent Stair: monumental treads down into the earth.
        new(new(760, 1800), new(1180, 1800), 110f, 0f, -72f, 60f, Step: 12f),
        // The Hall of the Dead — the hub rotunda.
        new(new(1450, 1800), new(1450, 1800), 290f, -72f, -72f, 70f),

        // North: the burial gallery, then the Ossuary.
        new(new(1450, 1560), new(1450, 980), 95f, -72f, -108f, 60f),
        new(new(1450, 680), new(1450, 680), 235f, -122f, -122f, 65f),
        // The Bone Crawl — a narrow shortcut looping from the ossuary to the
        // Processional's midpoint, west of the chasm.
        new(new(1660, 760), new(2000, 1150), 70f, -122f, -135f, 55f),
        new(new(2000, 1150), new(2270, 1660), 70f, -135f, -112f, 55f),

        // South: the gallery to the Flooded Cloister, and a noble family's
        // dead-end crypt beyond it.
        new(new(1450, 2040), new(1450, 2620), 95f, -72f, -108f, 60f),
        new(new(1450, 2920), new(1450, 2920), 255f, -122f, -122f, 65f),
        new(new(1230, 2980), new(1010, 3070), 75f, -122f, -123f, 55f),
        new(new(900, 3120), new(900, 3120), 135f, -124f, -124f, 60f),

        // East: the Processional — the grand pillared walk, broken mid-way.
        new(new(1750, 1800), new(2650, 1800), 120f, -72f, -140f, 65f),
        // The East Landing past the span.
        new(new(2820, 1800), new(2820, 1800), 170f, -140f, -140f, 65f),

        // The scree ramp out of the charnel pit, and the pocket linking it
        // back to the cloister — falls are detours, never graves.
        new(new(2400, 2150), new(2120, 2520), 90f, -285f, -118f, 60f),
        new(new(2120, 2520), new(1690, 2760), 80f, -118f, -122f, 55f),

        // The Catacomb Maze: narrow criss-cross galleries; the rock left
        // between them IS the maze.
        new(new(2960, 1440), new(3720, 1440), 80f, -156f, -160f, 55f),
        new(new(2960, 1800), new(3720, 1800), 80f, -158f, -162f, 55f),
        new(new(2960, 2160), new(3720, 2160), 80f, -160f, -164f, 55f),
        new(new(3060, 1340), new(3060, 2260), 80f, -157f, -161f, 55f),
        new(new(3360, 1340), new(3360, 2260), 80f, -159f, -163f, 55f),
        new(new(3660, 1340), new(3660, 2260), 80f, -161f, -165f, 55f),

        // The Candle Chapel north of the maze.
        new(new(3360, 1060), new(3360, 1060), 175f, -172f, -172f, 60f),
        new(new(3360, 1340), new(3360, 1160), 80f, -163f, -170f, 55f),

        // The Deep Stair: two stepped legs with a turn.
        new(new(3660, 2260), new(3660, 2760), 95f, -165f, -228f, 60f, Step: 12f),
        new(new(3660, 2760), new(3260, 2980), 95f, -228f, -246f, 60f, Step: 12f),

        // The Antechamber of the Kings, the gate mouth, and the Mausoleum —
        // the boss's pillared court at the bottom of the world. The dais is
        // TERRAIN (16 high — under StepHeight, so raiders step onto it), not a
        // solid: the boss must stand on real ground, not inside a box.
        new(new(3000, 3060), new(3000, 3060), 195f, -248f, -248f, 65f),
        new(new(2760, 3140), new(2620, 3180), 95f, -252f, -252f, 55f),
        new(new(2320, 3260), new(2320, 3260), 330f, -258f, -258f, 70f),
        new(new(2320, 3260), new(2320, 3260), 72f, -242f, -242f, 26f),
    };

    // Carves cut AFTER the rooms, so they break whatever they cross.
    private static readonly Plate[] Carves =
    {
        // The Charnel Chasm: the Processional's collapsed midpoint, a pit of
        // the nameless dead. Its floor is a real place (rogue ambush).
        new(new(2380, 1400), new(2380, 2200), 125f, -285f, -285f, 70f),
        // The cloister's pool basin: a shallow sunken bath under dark water.
        new(new(1450, 2920), new(1450, 2920), 120f, -140f, -140f, 50f),
    };

    /// <summary>Where the cloister's standing water lies — the plane the
    /// scenery floats at, a hair above the basin floor's rim.</summary>
    private const float PoolWaterLevel = -131f;

    /// <summary>Every framed doorway: where a portal frame stands in the
    /// scenery AND where its jambs block in the sim — one table so the stone a
    /// raider sees is the stone the server enforces. AlongX is the walker's
    /// direction of travel through the frame.</summary>
    private static readonly (float X, float Z, bool AlongX, float Width)[] Doorways =
    {
        (700, 1800, true, 180f),    // the undercroft door
        (1215, 1800, true, 190f),   // the stair's foot into the hub
        (1690, 1800, true, 200f),   // the hub's east exit
        (1450, 1510, false, 170f),  // the north gallery mouth
        (1450, 2090, false, 170f),  // the south gallery mouth
        (1450, 935, false, 170f),   // the ossuary door
        (1450, 2660, false, 180f),  // the cloister door
        (1055, 3055, true, 140f),   // the noble crypt door
        (2900, 1800, true, 170f),   // the landing into the maze
        (3360, 1250, false, 150f),  // the chapel walk
        (3660, 2330, false, 170f),  // the deep stair's head
        (2705, 3155, true, 180f),   // the mausoleum gate
    };

    private static (float H, float Weight) EvalPlate(Vector2 p, in Plate plate, float widen = 0f, float soften = 0f)
    {
        var (a, b, r, ha, hb, blend) = (plate.A, plate.B, plate.R + widen, plate.Ha, plate.Hb, plate.Blend + soften);
        var ab = b - a;
        var t = ab.LengthSquared() < 1f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        var h = ha + (hb - ha) * t;
        // Stair plates quantize the floor profile into treads. Quantizing the
        // PROFILE (not the blended result) keeps the treads flat across the
        // gallery's width while the ends still blend into their landings.
        if (plate.Step > 0f)
            h = MathF.Round(h / plate.Step) * plate.Step;
        var d = Vector2.Distance(p, a + ab * t);
        if (d <= r)
            return (h, 1f);
        if (d >= r + blend)
            return (h, 0f);
        var s = (d - r) / blend;
        return (h, 1f - s * s * (3f - 2f * s));
    }

    /// <summary>How far beyond a room's own blend its influence reaches into
    /// the roof envelope — what keeps the rock mass low over the halls and
    /// lets it swell between them.</summary>
    private const float EnvelopeWiden = 30f, EnvelopeSoften = 230f;

    /// <summary>How much of the point lies on PLAYED ground: 1 on a room or
    /// carve floor, 0 in unexcavated rock, in between on the carved walls.
    /// The palette keys the void-dark rock mass off this, so the flat roof
    /// tops go black no matter what absolute height the descent put them at.</summary>
    private static float PlayWeightAt(float x, float z)
    {
        var p = new Vector2(x, z);
        var w = 0f;
        foreach (var room in Rooms)
            w = MathF.Max(w, EvalPlate(p, room).Weight);
        foreach (var carve in Carves)
            w = MathF.Max(w, EvalPlate(p, carve).Weight);
        return w;
    }

    private static float HeightAt(float x, float z)
    {
        var p = new Vector2(x, z);

        // The unexcavated rock mass. Its base is a dark plateau well above the
        // entrance; near the complex it hugs the rooms' envelope + RoofRise,
        // so the implied vaults stay a constant height over descending floors.
        var plateau = 80f + Fractal(x, z, 700f, 3, 7) * 18f;
        float envSum = 0f, envW = 0f, envMax = 0f;
        float hSum = 0f, wSum = 0f, wMax = 0f;
        foreach (var room in Rooms)
        {
            var (eh, ew) = EvalPlate(p, room, EnvelopeWiden, EnvelopeSoften);
            if (ew > 0f)
            {
                envSum += eh * ew;
                envW += ew;
                envMax = MathF.Max(envMax, ew);
            }
            var (h, w) = EvalPlate(p, room);
            if (w > 0f)
            {
                hSum += h * w;
                wSum += w;
                wMax = MathF.Max(wMax, w);
            }
        }
        var rock = envW > 0f
            ? float.Lerp(plateau, envSum / envW + RoofRise, envMax)
            : plateau;

        var height = wSum > 0f ? float.Lerp(rock, hSum / wSum, wMax) : rock;

        // Carve the chasm and the pool through whatever stood there.
        var inPlay = wMax;
        foreach (var carve in Carves)
        {
            var (ch, cw) = EvalPlate(p, carve);
            height = float.Lerp(height, ch, cw);
            inPlay = MathF.Max(inPlay, cw);
        }

        // Organic detail: rubble-and-dust grain on the walked floors (gentle —
        // this is dressed stone, not moorland), and rough breaks on the rock.
        height += Fractal(x, z, 900f, 2, 23) * 3f;
        height += Fractal(x, z, 170f, 3, 41) * 7f * (1f - 0.85f * inPlay);

        // The border band rises into unbroken rock — the realm is sealed. The
        // grade must exceed RealmValidator's inching slope.
        var edge = MathF.Min(MathF.Min(x, 4240f - x), MathF.Min(z, 4000f - z));
        if (edge < 160f)
            height += (160f - edge) * 5f;

        return height;
    }

    /// <summary>Evaluate the layout math over the sample grid — the heightfield
    /// this realm's terrain mesh is built from. Heights are rounded to 3
    /// decimals so design, mesh, and bake all carry the identical value.</summary>
    private static HeightField BuildHeightField()
    {
        var heights = new float[W * D];
        for (var j = 0; j < D; j++)
            for (var i = 0; i < W; i++)
                heights[j * W + i] = MathF.Round(HeightAt(i * Cell, j * Cell), 3);
        return new HeightField(0f, 0f, Cell, W, D, heights);
    }

    // ------------------------------------------------------------- solids

    private static List<Aabb> Solids(HeightField field)
    {
        var solids = new List<Aabb>();

        // A square masonry pier standing on the floor: combat cover, and the
        // visual masonry's collision truth. Sunk 10 into the ground so detail
        // noise never floats it.
        void Pier(float x, float z, float half, float tall)
        {
            var ground = field.Sample(x, z);
            solids.Add(new Aabb(new Vector3(x - half, ground - 10f, z - half),
                                new Vector3(x + half, ground + tall, z + half)));
        }

        // A sarcophagus: a hip-high stone chest nobody steps over (45 tall,
        // far over StepHeight), long axis along X or Z.
        void Sarcophagus(float x, float z, bool alongX)
        {
            var ground = field.Sample(x, z);
            var (hx, hz) = alongX ? (55f, 26f) : (26f, 55f);
            solids.Add(new Aabb(new Vector3(x - hx, ground - 6f, z - hz),
                                new Vector3(x + hx, ground + 45f, z + hz)));
        }

        // The Broken Span: a stone deck with parapets over the charnel chasm.
        // The Processional DESCENDS across the span, so the deck steps down in
        // four flights (9 per step — under StepHeight both ways) and each end
        // meets its own rim's grade; a flat deck would leave the east remount
        // an unclimbable 25-unit ledge. The parapets ride each step, 22 tall —
        // chest-high walls bolts fly over.
        var deckSteps = new (float X0, float X1, float Top)[]
        {
            (2210, 2310, -108), (2310, 2405, -117), (2405, 2500, -126), (2500, 2590, -135),
        };
        foreach (var (x0, x1, top) in deckSteps)
        {
            solids.Add(new Aabb(new Vector3(x0, top - 16f, 1750), new Vector3(x1, top, 1850)));      // deck
            solids.Add(new Aabb(new Vector3(x0, top, 1750), new Vector3(x1, top + 22f, 1762)));      // north parapet
            solids.Add(new Aabb(new Vector3(x0, top, 1838), new Vector3(x1, top + 22f, 1850)));      // south parapet
        }

        // The Hall of the Dead: a ring of eight piers under the implied dome.
        for (var k = 0; k < 8; k++)
        {
            var ang = k * MathF.Tau / 8f + 0.39f;
            Pier(1450 + MathF.Cos(ang) * 205f, 1800 + MathF.Sin(ang) * 205f, 17f, 150f);
        }

        // The Processional's colonnade: pier pairs flanking the walk, stopping
        // short of the chasm.
        foreach (var px in new[] { 1900f, 2080f, 2260f })
        {
            Pier(px, 1712, 15f, 140f);
            Pier(px, 1888, 15f, 140f);
        }
        foreach (var px in new[] { 2560f, 2700f })
        {
            Pier(px, 1712, 15f, 140f);
            Pier(px, 1888, 15f, 140f);
        }

        // The cloister's pool colonnade: four piers around the water.
        foreach (var (cx, cz) in new[] { (1310f, 2780f), (1590f, 2780f), (1310f, 3060f), (1590f, 3060f) })
            Pier(cx, cz, 15f, 130f);

        // Sarcophagi: the hub's honored dead, the antechamber's kings, and the
        // noble crypt's family.
        Sarcophagus(1330, 1655, alongX: false);
        Sarcophagus(1570, 1655, alongX: false);
        Sarcophagus(1330, 1945, alongX: false);
        Sarcophagus(1570, 1945, alongX: false);
        Sarcophagus(2905, 2985, alongX: true);
        Sarcophagus(2905, 3135, alongX: true);
        Sarcophagus(3095, 2985, alongX: true);
        Sarcophagus(3095, 3135, alongX: true);
        Sarcophagus(860, 3050, alongX: true);
        Sarcophagus(860, 3190, alongX: true);

        // The Ossuary's bone altar — a low broad block at the room's heart.
        var altar = field.Sample(1450, 680);
        solids.Add(new Aabb(new Vector3(1400, altar - 6f, 640), new Vector3(1500, altar + 40f, 720)));

        // Doorway jambs: the portal frames' uprights block exactly where the
        // scenery stands them (the shared Doorways table).
        foreach (var (x, z, alongX, width) in Doorways)
        {
            var half = width / 2f;
            foreach (var side in new[] { -1f, 1f })
            {
                var (jx, jz) = alongX ? (x, z + side * half) : (x + side * half, z);
                var ground = field.Sample(jx, jz);
                solids.Add(new Aabb(new Vector3(jx - 10f, ground - 6f, jz - 10f),
                                    new Vector3(jx + 10f, ground + 116f, jz + 10f)));
            }
        }

        // The Mausoleum: a ring of twelve piers and gate piers at the mouth.
        // (The boss's dais is a low terrain plate, not a solid.)
        for (var k = 0; k < 12; k++)
        {
            var ang = k * MathF.Tau / 12f + 0.26f;
            Pier(2320 + MathF.Cos(ang) * 255f, 3260 + MathF.Sin(ang) * 255f, 18f, 170f);
        }
        Pier(2600, 3095, 20f, 160f);
        Pier(2640, 3265, 20f, 160f);

        return solids;
    }

    // ------------------------------------------------------------- the cast
    // Thematically the Crypt's garrison is the risen dead: minions are
    // skeleton warriors, rogues are crypt ghouls striking from niches and
    // pits, mages are the necromancer keepers of the deep halls.

    private static readonly (EnemyType Type, float X, float Z)[] Enemies =
    {
        // The undercroft: the first of the dead stir at the door.
        (EnemyType.Minion, 560, 1720), (EnemyType.Minion, 590, 1890),
        // The stair's foot: a ghoul waits under the last tread.
        (EnemyType.Rogue, 1230, 1830),
        // The Hall of the Dead: the hub watch.
        (EnemyType.Minion, 1380, 1720), (EnemyType.Minion, 1500, 1880),
        (EnemyType.Minion, 1450, 1620), (EnemyType.Mage, 1520, 1750),
        // The north gallery and the Ossuary.
        (EnemyType.Minion, 1450, 1350), (EnemyType.Minion, 1430, 1080),
        (EnemyType.Mage, 1370, 700), (EnemyType.Mage, 1540, 640), (EnemyType.Rogue, 1450, 800),
        // The bone crawl: a ghoul in the dark of the shortcut.
        (EnemyType.Rogue, 1950, 1090),
        // The south gallery, the cloister, and the noble crypt.
        (EnemyType.Minion, 1450, 2300), (EnemyType.Minion, 1470, 2550),
        (EnemyType.Rogue, 1330, 2870), (EnemyType.Rogue, 1560, 2990), (EnemyType.Mage, 1450, 3080),
        (EnemyType.Mage, 900, 3120), (EnemyType.Minion, 940, 3010),
        // The Processional and the span's west approach.
        (EnemyType.Minion, 1900, 1780), (EnemyType.Minion, 2100, 1830), (EnemyType.Mage, 2270, 1760),
        // The charnel pit: ghouls among the nameless dead.
        (EnemyType.Rogue, 2360, 1650), (EnemyType.Rogue, 2400, 1950),
        // The east landing and the catacomb maze.
        (EnemyType.Minion, 2830, 1730), (EnemyType.Minion, 2870, 1870),
        (EnemyType.Minion, 3210, 1440), (EnemyType.Rogue, 3360, 1720), (EnemyType.Minion, 3510, 2160),
        (EnemyType.Rogue, 3660, 1560),
        // The candle chapel: the keepers at their vigil.
        (EnemyType.Mage, 3300, 1020), (EnemyType.Mage, 3430, 1100),
        // The deep stair and the antechamber's honor guard.
        (EnemyType.Rogue, 3660, 2500),
        (EnemyType.Minion, 3140, 3060), (EnemyType.Minion, 2980, 3200), (EnemyType.Mage, 3000, 2950),
        // The mausoleum's threshold watch.
        (EnemyType.Rogue, 2500, 3240), (EnemyType.Rogue, 2450, 3330),
    };

    private static readonly (float X, float Z) PlayerSpawn = (480, 1800);
    private static readonly (float X, float Z) BossSpawn = (2320, 3260);

    // ------------------------------------------------------------- lights
    // Torch sconces line the processional routes; candles pool where the dead
    // were tended; soulfire burns cold green where the dead tend themselves.

    private static readonly (float X, float Z)[] TorchSpots =
    {
        (700, 1730), (700, 1870),                       // the undercroft door
        (900, 1720), (1080, 1880),                      // the descent stair
        (1265, 1700), (1265, 1900),                     // the hub mouth
        (1620, 1710), (1620, 1890),                     // the hub's east exit
        (1450, 1470), (1440, 1180), (1450, 900),        // the north gallery
        (1450, 2130), (1460, 2420), (1450, 2700),       // the south gallery
        (1180, 3010), (1030, 3090),                     // the noble crypt's walk
        (1840, 1740), (2020, 1860), (2200, 1740),       // the processional
        (2270, 1855), (2490, 1855),                     // the span's ends
        (2740, 1720), (2900, 1870),                     // the east landing
        (2980, 1500), (3210, 1860), (3420, 2100),       // the maze's main walk
        (3600, 1380), (3060, 2220),                     // maze corners
        (3360, 1230),                                   // the chapel walk
        (3660, 2400), (3640, 2680), (3450, 2900),       // the deep stair
        (2790, 3090), (2700, 3230),                     // the gate mouth
    };

    private static readonly (float X, float Z)[] CandleSpots =
    {
        (3280, 980), (3440, 1000), (3360, 1140), (3290, 1110), (3450, 1120),  // the candle chapel's sea
        (1340, 2830), (1560, 2830), (1450, 3040),       // the cloister's rim
        (830, 3120), (975, 3120),                       // the noble crypt
        (1390, 1740), (1510, 1860),                     // the hub's biers
    };

    private static readonly (float X, float Z)[] SoulfireSpots =
    {
        (1450, 560), (1340, 760), (1560, 760),          // the ossuary's cold watch
        (2340, 1500), (2420, 2100),                     // the charnel pit
        (2905, 3060), (3095, 3060),                     // the antechamber kings
        (2160, 3100), (2480, 3100), (2160, 3420), (2480, 3420),  // the mausoleum ring
        (2320, 3475),                                   // behind the throne
    };

    /// <summary>The collapsed shaft over the entrance — where the only daylight
    /// falls. The Scenery partial hangs the light shaft and dust here.</summary>
    private static readonly Vector3 ShaftMouth = new(520, 150, 1800);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// The Crag's DESIGN — the layout math that computes the realm: a "wild"
/// highland of crags is the default, and the playable realm is carved into it
/// as a chain of smooth-blended plates (discs and capsules with target
/// heights) — glen → dale → gorge bridge → switchback climb → rolling moor
/// with a standing-stone circle and an overlook spur → summit shoulder →
/// walled boss court. A deep gorge cuts the land in two; its floor is a real
/// place (rogue ambush) with a scree ramp back out. Deterministic: same
/// numbers, same realm, every run (hash noise, no framework RNG).
///
/// This lives CLIENT-SIDE, next to the scene builder that consumes it, so the
/// design can place ANYTHING Godot can express — the simulation-relevant parts
/// (terrain, collision, markers, braziers) follow the bake conventions, and
/// everything else (the boulder scatter, and whatever dressing comes later) is
/// pure scenery the bake never needs to understand. The served geometry JSON
/// is BAKED FROM the finished scene, never the other way around.
/// </summary>
public static class CragDesign
{
    public const float Cell = 40f;          // world units between height samples
    public const int W = 151, D = 161;      // samples: the realm is 6000 x 6400 world units
    private const int Seed = 77;

    public const string Name = "Crag";

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

    // ------------------------------------------------------------- the land
    // A plate is a smooth-blended playable area: a capsule (A→B) or disc
    // (A==B) whose height runs Ha→Hb along its axis, fading over Blend.

    private static readonly (Vector2 A, Vector2 B, float R, float Ha, float Hb, float Blend)[] Plates =
    {
        // The Glen — the spawn meadow, ringed by crags.
        (new(700, 700), new(700, 700), 420f, 0f, 0f, 80f),
        // The dale east: a narrowing walk toward the gorge.
        (new(1000, 700), new(2200, 900), 140f, 0f, 25f, 70f),
        (new(2450, 1000), new(2450, 1000), 260f, 30f, 30f, 70f),
        (new(2650, 1050), new(3050, 1100), 130f, 30f, 40f, 60f),
        // Gorge rims: flat landings either side of the bridge.
        (new(2980, 1100), new(2980, 1100), 200f, 40f, 40f, 60f),
        (new(3700, 1100), new(3700, 1100), 220f, 40f, 40f, 60f),
        // The scree ramp out of the gorge floor, and the pocket it climbs into.
        (new(3350, 700), new(2700, 550), 130f, -140f, 5f, 60f),
        (new(2500, 600), new(2500, 600), 170f, 10f, 10f, 60f),
        (new(2500, 600), new(2250, 750), 120f, 10f, 15f, 60f),
        // The switchbacks: two climbing legs and their turns.
        (new(3700, 1100), new(4600, 1250), 120f, 40f, 95f, 60f),
        (new(4750, 1350), new(4750, 1350), 150f, 98f, 98f, 60f),
        (new(4750, 1350), new(3900, 1650), 120f, 98f, 150f, 60f),
        (new(3750, 1750), new(3750, 1750), 160f, 150f, 150f, 60f),
        // A moor tongue over the switchbacks: a one-way jump-down shortcut.
        (new(3850, 1450), new(3850, 1450), 120f, 150f, 150f, 30f),
        // The Moor — a broad rolling plateau.
        (new(3750, 1750), new(3900, 2500), 200f, 150f, 152f, 70f),
        (new(3900, 2500), new(3900, 2500), 700f, 152f, 152f, 90f),
        (new(4800, 2900), new(4800, 2900), 560f, 155f, 155f, 90f),
        (new(3200, 3100), new(3200, 3100), 500f, 150f, 150f, 90f),
        (new(4300, 2700), new(4300, 2700), 240f, 154f, 154f, 60f), // the stone circle's floor
        // The overlook spur: a scenic mage nest on a dead-end ledge.
        (new(3000, 2800), new(2500, 2100), 130f, 150f, 180f, 60f),
        (new(2450, 1950), new(2450, 1950), 160f, 185f, 185f, 60f),
        // The summit shoulder: the last climb.
        (new(4800, 3200), new(5300, 3700), 130f, 155f, 205f, 60f),
        (new(5350, 3850), new(5350, 3850), 150f, 208f, 208f, 60f),
        (new(5350, 3850), new(4700, 4300), 130f, 208f, 258f, 60f),
        (new(4600, 4250), new(4600, 4250), 180f, 260f, 260f, 60f),
        (new(4460, 4400), new(4460, 4400), 120f, 261f, 261f, 60f), // the gate mouth
        // The Crag summit court — the boss's walled ring.
        (new(4200, 4750), new(4200, 4750), 380f, 262f, 262f, 60f),
    };

    // The gorge is carved AFTER the plates, so it cuts whatever it crosses.
    private static readonly (Vector2 A, Vector2 B, float R, float Floor, float Blend) Gorge =
        (new(3350, 500), new(3150, 2900), 170f, -140f, 90f);

    private static (float H, float Weight) EvalPlate(Vector2 p, Vector2 a, Vector2 b, float r, float ha, float hb, float blend)
    {
        var ab = b - a;
        var t = ab.LengthSquared() < 1f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        var d = Vector2.Distance(p, a + ab * t);
        var h = ha + (hb - ha) * t;
        if (d <= r)
            return (h, 1f);
        if (d >= r + blend)
            return (h, 0f);
        var s = (d - r) / blend;
        return (h, 1f - s * s * (3f - 2f * s));
    }

    public static float HeightAt(float x, float z)
    {
        var p = new Vector2(x, z);

        // The wild highland: high crags with broad ridge noise.
        var wild = 330f + Fractal(x, z, 900f, 4, 7) * 55f;

        // The playable plates, weight-blended where they overlap.
        float hSum = 0f, wSum = 0f, wMax = 0f;
        foreach (var (a, b, r, ha, hb, blend) in Plates)
        {
            var (h, w) = EvalPlate(p, a, b, r, ha, hb, blend);
            if (w <= 0f)
                continue;
            hSum += h * w;
            wSum += w;
            wMax = MathF.Max(wMax, w);
        }
        var height = wSum > 0f ? float.Lerp(wild, hSum / wSum, wMax) : wild;

        // Carve the gorge through whatever stood there.
        var (gh, gw) = EvalPlate(p, Gorge.A, Gorge.B, Gorge.R, Gorge.Floor, Gorge.Floor, Gorge.Blend);
        height = float.Lerp(height, gh, gw);
        var inPlay = MathF.Max(wMax, gw);

        // Organic detail: a broad gentle swell everywhere (what kills the tile
        // feel), plus rougher grain that fades on the walked ground.
        height += Fractal(x, z, 1500f, 2, 23) * 6f;
        height += Fractal(x, z, 260f, 3, 41) * 8f * (1f - 0.75f * inPlay);

        // The border band rises into an unclimbable rim — the realm is sealed.
        // The grade must exceed the worst-case per-cell step (RealmValidator's
        // inching slope), or the flood fill rightly reports a leak.
        var edge = MathF.Min(MathF.Min(x, 6000f - x), MathF.Min(z, 6400f - z));
        if (edge < 160f)
            height += (160f - edge) * 5f;

        return height;
    }

    /// <summary>The heightfield the terrain mesh is built from (and the bake will
    /// sample back). Heights rounded to 3 decimals so every stage carries the
    /// identical value.</summary>
    public static HeightField BakeField()
    {
        var heights = new float[W * D];
        for (var j = 0; j < D; j++)
            for (var i = 0; i < W; i++)
                heights[j * W + i] = MathF.Round(HeightAt(i * Cell, j * Cell), 3);
        return new HeightField(0f, 0f, Cell, W, D, heights);
    }

    // ------------------------------------------------------------- solids

    public static List<Aabb> Solids(HeightField field)
    {
        var solids = new List<Aabb>
        {
            // The gorge bridge: a stone deck with parapets. The deck top matches
            // the rims (40); the parapets rise 22 — too tall to walk over, low
            // enough that eye-height bolts fly across the gorge unimpeded.
            new(new Vector3(3040, 26, 1050), new Vector3(3640, 40, 1150)),  // deck
            new(new Vector3(3060, 40, 1050), new Vector3(3620, 62, 1062)),  // north parapet
            new(new Vector3(3060, 40, 1138), new Vector3(3620, 62, 1150)),  // south parapet
        };

        // The summit ramparts: a walled ring with one southern gate, on court
        // ground (~262). Wall tops at 352 — far beyond any step.
        const float wallBase = 252f, wallTop = 352f;
        solids.Add(new Aabb(new Vector3(3820, wallBase, 4370), new Vector3(4380, wallTop, 4410)));  // south, west of gate
        solids.Add(new Aabb(new Vector3(4540, wallBase, 4370), new Vector3(4580, wallTop, 4410)));  // south, east of gate
        solids.Add(new Aabb(new Vector3(3820, wallBase, 5090), new Vector3(4580, wallTop, 5130)));  // north
        solids.Add(new Aabb(new Vector3(3820, wallBase, 4410), new Vector3(3860, wallTop, 5090)));  // west
        solids.Add(new Aabb(new Vector3(4540, wallBase, 4410), new Vector3(4580, wallTop, 5090)));  // east

        // The standing stones on the moor: a ring of eight monoliths.
        var circle = new Vector2(4300, 2700);
        for (var k = 0; k < 8; k++)
        {
            var ang = k * MathF.Tau / 8f + 0.2f;
            var cx = circle.X + MathF.Cos(ang) * 180f;
            var cz = circle.Y + MathF.Sin(ang) * 180f;
            var ground = field.Sample(cx, cz);
            var half = 16f + (k % 3) * 3f; // slightly irregular monoliths
            solids.Add(new Aabb(new Vector3(cx - half, ground - 10f, cz - half),
                                new Vector3(cx + half, ground + 78f + (k % 2) * 10f, cz + half)));
        }
        return solids;
    }

    // ------------------------------------------------------------- the cast

    public static readonly (EnemyType Type, float X, float Z)[] Enemies =
    {
        // The glen and dale: minions guard the first walk.
        (EnemyType.Minion, 1500, 750), (EnemyType.Minion, 1900, 850), (EnemyType.Minion, 2350, 950),
        (EnemyType.Rogue, 2500, 1100),
        // The gorge floor: a rogue ambush for anyone who falls (or dares climb down).
        (EnemyType.Rogue, 3300, 1300), (EnemyType.Rogue, 3280, 1800), (EnemyType.Rogue, 3320, 2300),
        // The east landing and switchbacks: the bridgehead watch.
        (EnemyType.Minion, 3750, 1180), (EnemyType.Minion, 3850, 1100), (EnemyType.Mage, 3950, 1250),
        (EnemyType.Minion, 4400, 1220), (EnemyType.Minion, 4500, 1400),
        (EnemyType.Rogue, 4100, 1560), (EnemyType.Minion, 3850, 1700),
        // The moor: scattered packs across the rolling top.
        (EnemyType.Minion, 3850, 2150), (EnemyType.Minion, 4050, 2350), (EnemyType.Rogue, 3500, 2800),
        (EnemyType.Minion, 4600, 2600), (EnemyType.Rogue, 5000, 2950),
        // The stone circle: a warded camp.
        (EnemyType.Minion, 4200, 2600), (EnemyType.Minion, 4400, 2600),
        (EnemyType.Mage, 4300, 2820), (EnemyType.Mage, 4180, 2760),
        // The overlook spur: mages with the high ground.
        (EnemyType.Mage, 2520, 2050), (EnemyType.Mage, 2400, 1900), (EnemyType.Rogue, 2600, 2200),
        // The summit shoulder and the court's honor guard.
        (EnemyType.Minion, 5100, 3500), (EnemyType.Mage, 5320, 3750), (EnemyType.Minion, 5000, 4050),
        (EnemyType.Mage, 4050, 4600), (EnemyType.Mage, 4350, 4600),
        (EnemyType.Rogue, 4100, 4900), (EnemyType.Rogue, 4300, 4900),
    };

    public static readonly (float X, float Z)[] BrazierSpots =
    {
        (640, 640), (760, 640),                    // the spawn gate
        (900, 700), (1500, 780), (2100, 880), (2500, 1020), (2900, 1090),
        (3080, 1040), (3600, 1040),                // the bridge ends
        (3800, 1140), (4300, 1210), (4700, 1300), (4300, 1520), (3950, 1630),
        (3780, 1830), (3850, 2200),                // onto the moor
        (4130, 2530), (4470, 2530), (4130, 2870), (4470, 2870), // the stone circle
        (2450, 1950),                              // the overlook
        (2500, 600),                               // the gorge pocket
        (4900, 3300), (5250, 3600), (5150, 4000), (4800, 4200), // the summit climb
        (4420, 4440), (4500, 4440),                // inside the gate
        (3950, 4550), (4450, 4550), (3950, 4950), (4450, 4950), // the court ring
        (4100, 4820), (4300, 4820),                // flanking the throne
    };

    public static readonly (float X, float Z) PlayerSpawn = (700, 700);
    public static readonly (float X, float Z) BossSpawn = (4200, 4820);

    // ------------------------------------------------------------- scenery
    // Pure dressing — the whole point of building the scene FIRST: nothing
    // below exists in the served geometry at all. Add whatever reads well.

    /// <summary>Deterministic boulder scatter for the crag faces: position,
    /// overall size, yaw, and which rock variant. Rocks favour steep, wild
    /// ground (the slopes nobody walks), never the gorge depths or the rim.</summary>
    public static IEnumerable<(Vector3 Position, float Size, float Yaw, int Variant)> Boulders()
    {
        for (var gz = 200; gz < 6200; gz += 120)
        {
            for (var gx = 200; gx < 5800; gx += 120)
            {
                var x = gx + Hash(gx, gz, 611) * 90f - 45f;
                var z = gz + Hash(gx, gz, 613) * 90f - 45f;
                var h = HeightAt(x, z);
                if (h is < -100f or > 470f)
                    continue; // not in the gorge dark, not on the border rim

                // Slope from central differences: boulders live on rocky faces.
                var slopeX = (HeightAt(x + 25f, z) - HeightAt(x - 25f, z)) / 50f;
                var slopeZ = (HeightAt(x, z + 25f) - HeightAt(x, z - 25f)) / 50f;
                var slope = MathF.Sqrt(slopeX * slopeX + slopeZ * slopeZ);
                if (slope < 0.65f || Hash(gx, gz, 617) > 0.42f)
                    continue;

                var size = 9f + Hash(gx, gz, 619) * 21f;
                yield return (new Vector3(x, h - size * 0.25f, z),
                              size, Hash(gx, gz, 621) * MathF.Tau, (int)(Hash(gx, gz, 623) * 3f));
            }
        }
    }
}

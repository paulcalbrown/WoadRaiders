// Generates The Crag — the first open realm — as a matched pair of artifacts:
//
//   WoadRaiders.Client/maps/Crag.json   the simulation geometry the server
//                                       hosts (heightfield, solids, spawns,
//                                       props), pointing at the scene.
//   WoadRaiders.Client/maps/Crag.tscn   the realm's Godot scene, saved by
//                                       GODOT'S OWN SERIALIZER — exactly what
//                                       a naturally-authored scene looks like:
//                                       built-in nodes and resources only, a
//                                       REAL displaced terrain ArrayMesh, no
//                                       scripts, no metadata, nothing a
//                                       vanilla Godot editor doesn't already
//                                       understand.
//
// A .NET 10 file-based app (godot-mono and the client build are driven
// automatically):
//
//   dotnet run tools/GenerateRealm.cs
//
// The flow: compute and validate the realm → write the JSON → have Godot
// build the scene from it (tools/build_realm_scene.gd → RealmSceneBuilder,
// ResourceSaver.save) → normalize the serializer's random sub-resource ids so
// regeneration is deterministic → ROUND-TRIP VERIFY by baking the scene back
// through the standard hand-made pipeline (tools/bake_realm.gd samples the
// real terrain mesh) and comparing against the JSON. The scene you can hand-
// edit and the geometry the server simulates are proven to agree.
//
// The land is computed, not drawn: a "wild" highland of crags is the default,
// and the playable realm is carved into it as a chain of smooth-blended plates
// (discs and capsules with target heights) — glen → dale → gorge bridge →
// switchback climb → rolling moor with a standing-stone circle and an overlook
// spur → summit shoulder → walled boss court on the crag. A deep gorge cuts the
// land in two; its floor is a real place (rogue ambush) with a scree ramp back
// out, so a fall is a detour, never a grave. Verticality is the point: the
// route climbs ~260 units, ledges drop you back down one-way, cliffs are
// simply terrain too steep to walk.
//
// Validation runs the REAL simulation rules:
//   - a virtual raider walks the whole route with DungeonGeometry.Move at
//     player speed and must reach the boss court (and end up high);
//   - Core.RealmValidator proves every camp comfortably reachable, the borders
//     sealed even against slope-inching, and no reachable spot stranded;
//   - the JSON round-trips through DungeonGeometryFile.Parse.

#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using WoadRaiders.Core;

const float Cell = 40f;         // world units between height samples
const int W = 151, D = 161;     // samples: the realm is 6000 x 6400 world units
const int Seed = 77;            // deterministic: same seed, same realm, every run

// ------------------------------------------------------------- noise
// Hash-based value noise: deterministic across runs and machines (no
// framework RNG, no float seeding), smooth-lerped between lattice points.

static float Hash(int x, int z, int salt)
{
    var h = unchecked((uint)(x * 374761393 + z * 668265263 + salt * 974711 + Seed * 144665));
    h = (h ^ (h >> 13)) * 1274126177u;
    h ^= h >> 16;
    return (h & 0xFFFFFF) / (float)0x1000000; // 0..1
}

static float ValueNoise(float x, float z, float wavelength, int salt)
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

static float Fractal(float x, float z, float wavelength, int octaves, int salt)
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
// A plate is a smooth-blended playable area: a capsule (A→B) or disc (A==B)
// whose height runs Ha→Hb along its axis, fading to nothing over Blend.

var plates = new List<(Vector2 A, Vector2 B, float R, float Ha, float Hb, float Blend)>
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
var gorge = (A: new Vector2(3350, 500), B: new Vector2(3150, 2900), R: 170f, Floor: -140f, Blend: 90f);

static (float H, float Weight) EvalPlate(Vector2 p, Vector2 a, Vector2 b, float r, float ha, float hb, float blend)
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

float HeightAt(float x, float z)
{
    var p = new Vector2(x, z);

    // The wild highland: high crags with broad ridge noise.
    var wild = 330f + Fractal(x, z, 900f, 4, 7) * 55f;

    // The playable plates, weight-blended where they overlap.
    float hSum = 0f, wSum = 0f, wMax = 0f;
    foreach (var (a, b, r, ha, hb, blend) in plates)
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
    var (gh, gw) = EvalPlate(p, gorge.A, gorge.B, gorge.R, gorge.Floor, gorge.Floor, gorge.Blend);
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

// ------------------------------------------------------------- bake the field

// Heights are rounded to 3 decimals so the JSON, the scene's mesh vertices,
// and the wire all carry the IDENTICAL value — the scene-bake round trip below
// reproduces the sim geometry exactly.
var heights = new float[W * D];
for (var j = 0; j < D; j++)
    for (var i = 0; i < W; i++)
        heights[j * W + i] = MathF.Round(HeightAt(i * Cell, j * Cell), 3);
var field = new HeightField(0f, 0f, Cell, W, D, heights);

// ------------------------------------------------------------- solids

var solids = new List<Aabb>();

// The gorge bridge: a stone deck with parapets. The deck top matches the rims
// (40), so walking on is a flat step; the parapets rise 22 — too tall to walk
// over, low enough that eye-height bolts fly across the gorge unimpeded.
solids.Add(new Aabb(new Vector3(3040, 26, 1050), new Vector3(3640, 40, 1150)));  // deck
solids.Add(new Aabb(new Vector3(3060, 40, 1050), new Vector3(3620, 62, 1062)));  // north parapet
solids.Add(new Aabb(new Vector3(3060, 40, 1138), new Vector3(3620, 62, 1150)));  // south parapet

// The summit ramparts: a walled ring with one southern gate, on court ground
// (~262). Wall tops at 352 — far beyond any step.
const float WallBase = 252f, WallTop = 352f;
solids.Add(new Aabb(new Vector3(3820, WallBase, 4370), new Vector3(4380, WallTop, 4410)));  // south, west of gate
solids.Add(new Aabb(new Vector3(4540, WallBase, 4370), new Vector3(4580, WallTop, 4410)));  // south, east of gate
solids.Add(new Aabb(new Vector3(3820, WallBase, 5090), new Vector3(4580, WallTop, 5130)));  // north
solids.Add(new Aabb(new Vector3(3820, WallBase, 4410), new Vector3(3860, WallTop, 5090)));  // west
solids.Add(new Aabb(new Vector3(4540, WallBase, 4410), new Vector3(4580, WallTop, 5090)));  // east

// The standing stones on the moor: a ring of eight monoliths around the camp.
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

// ------------------------------------------------------------- cast & dressing

Vector3 OnGround(float x, float z) => new(x, field.Sample(x, z), z);

var playerSpawn = OnGround(700, 700);
var bossSpawn = OnGround(4200, 4820);

(EnemyType type, float x, float z)[] enemies =
[
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
];

(float x, float z)[] brazierSpots =
[
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
];
var props = brazierSpots.Select(b => new DungeonProp(PropType.Brazier, OnGround(b.x, b.z))).ToList();

// ------------------------------------------------------------- geometry & validation

var json = DungeonGeometryFile.ToJson(new DungeonGeometry(
    playerSpawn, solids,
    enemies.Select(e => new EnemySpawnPoint(OnGround(e.x, e.z), e.type)).ToList(),
    field)
{
    ScenePath = "res://maps/Crag.tscn", // the authored scene the client renders
    BossSpawn = bossSpawn,
    Props = props,
});
var geometry = DungeonGeometryFile.Parse(json); // validate what will actually be served

// The shared realm checks (Core.RealmValidator): comfortable reachability of
// every camp and the boss, sealed borders, no stranding pits.
var issues = RealmValidator.Validate(geometry);
if (issues.Count > 0)
    throw new InvalidOperationException("the realm fails validation:\n  - " + string.Join("\n  - ", issues));

// The route walk: a virtual raider walks the whole intended path with the REAL
// Move rules at player speed. Stalls fail the build; the summit must be high.
Vector2[] route =
[
    new(700, 700), new(1000, 700), new(1600, 800), new(2200, 900), new(2450, 1000),
    new(2650, 1050), new(3000, 1100), new(3340, 1100), new(3620, 1100), new(3700, 1100),
    new(4100, 1170), new(4600, 1250), new(4750, 1350), new(4300, 1500), new(3900, 1650),
    new(3750, 1750), new(3800, 2000), new(3900, 2500), new(4300, 2700), new(4800, 2900),
    new(4800, 3200), new(5300, 3700), new(5350, 3850), new(5000, 4100), new(4700, 4300),
    new(4560, 4300), new(4460, 4340), new(4460, 4450), new(4200, 4750), new(4200, 4820),
];
{
    var pos = geometry.SpawnPoint;
    var step = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
    foreach (var target in route)
    {
        var stall = 0;
        var guard = 0;
        while (Vector2.Distance(new Vector2(pos.X, pos.Z), target) > 30f)
        {
            var dir = Vector2.Normalize(target - new Vector2(pos.X, pos.Z));
            var next = geometry.Move(pos, new Vector3(dir.X, 0f, dir.Y) * step);
            stall = Vector3.Distance(next, pos) < 0.5f ? stall + 1 : 0;
            pos = next;
            if (stall > 30)
                throw new InvalidOperationException($"the route walker stalled heading to ({target.X},{target.Y}) from ({pos.X:0},{pos.Y:0},{pos.Z:0})");
            if (++guard > 5000)
                throw new InvalidOperationException($"the route walker wandered too long heading to ({target.X},{target.Y})");
        }
    }
    if (pos.Y < 250f)
        throw new InvalidOperationException($"the route walker ended at height {pos.Y:0} — the summit climb is broken");
    Console.WriteLine($"Route walk OK — the raider stands at ({pos.X:0}, {pos.Y:0}, {pos.Z:0}) before the boss.");
}

File.WriteAllText(Path.Combine("WoadRaiders.Client", "maps", "Crag.json"), json);

// ------------------------------------------------------------- the scene
// Godot builds and saves the .tscn itself (build_realm_scene.gd →
// RealmSceneBuilder → ResourceSaver), so the file is exactly what a naturally-
// authored scene looks like. Then the serializer's random sub-resource ids are
// normalized (regeneration stays deterministic), and the scene is baked BACK
// through the standard hand-made pipeline to prove it carries the same
// simulation geometry as the JSON.

Step("dotnet", "build WoadRaiders.Client/WoadRaiders.Client.csproj -v q --nologo",
    "building the client (the scene tools are C# in it)");
Step("godot-mono", "--headless --path WoadRaiders.Client -s res://tools/build_realm_scene.gd -- " +
    "res://maps/Crag.json res://maps/Crag.tscn", "building Crag.tscn with Godot's own serializer");

NormalizeSceneIds(Path.Combine("WoadRaiders.Client", "maps", "Crag.tscn"));

var roundtrip = Path.Combine("WoadRaiders.Client", "maps", "Crag.roundtrip.tmp.json");
if (File.Exists(roundtrip))
    File.Delete(roundtrip);
Step("godot-mono", "--headless --path WoadRaiders.Client -s res://tools/bake_realm.gd -- " +
    "res://maps/Crag.tscn res://maps/Crag.roundtrip.tmp.json", "baking the scene back (round-trip check)");
CompareRealms(geometry, DungeonGeometryFile.Load(roundtrip));
File.Delete(roundtrip);

var span = geometry.Bounds;
Console.WriteLine($"Wrote Crag.json + Crag.tscn (round trip verified): {W}x{D} terrain samples over " +
                  $"{(W - 1) * Cell:0}x{(D - 1) * Cell:0} units (heights {span.Min.Y:0}..{span.Max.Y:0}), " +
                  $"{solids.Count} solids, {props.Count} braziers, {enemies.Length} enemy camps + the boss.");

// ------------------------------------------------------------- helpers

static void Step(string exe, string args, string what)
{
    Console.WriteLine($"[{what}] {exe} {args}");
    var process = Process.Start(new ProcessStartInfo(exe, args)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    }) ?? throw new InvalidOperationException($"could not start {exe}");
    // Drain both pipes concurrently — a chatty stderr (Godot's) would otherwise
    // fill its buffer and deadlock a sequential read.
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    var output = stdout.Result + stderr.Result;
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{what} failed (exit {process.ExitCode}):\n{output}");
    foreach (var line in output.Split('\n'))
        if (line.Contains("built ") || line.Contains("baked ") || line.Contains("sampled "))
            Console.WriteLine("  " + line.Trim());
}

// ResourceSaver invents random identifiers on every save — sub-resource ids
// (e.g. "Image_kg5hl"), per-node unique_id stamps, and the scene uid. Rename
// the resource ids deterministically (Type_1, Type_2, ...) and drop the
// random-per-save attributes (both optional — the editor regenerates them on
// its next save), so regenerating an unchanged realm rewrites nothing. Ids
// are opaque references — any Godot reads the result identically.
static void NormalizeSceneIds(string path)
{
    var text = File.ReadAllText(path);
    text = Regex.Replace(text, @" uid=""uid://[^""]*""", "");
    text = Regex.Replace(text, @" unique_id=\d+", "");

    var counters = new Dictionary<string, int>();
    var renames = new Dictionary<string, string>();
    foreach (Match match in Regex.Matches(text, @"\[sub_resource type=""([^""]+)"" id=""([^""]+)""\]"))
    {
        var type = match.Groups[1].Value;
        var old = match.Groups[2].Value;
        counters[type] = counters.GetValueOrDefault(type) + 1;
        renames[old] = $"{type}_{counters[type]}";
    }
    var builder = new StringBuilder(text);
    foreach (var (old, fresh) in renames)
        builder.Replace($"\"{old}\"", $"\"{fresh}\"");
    File.WriteAllText(path, builder.ToString());
}

// The baked scene must carry the same simulation geometry as the JSON.
static void CompareRealms(DungeonGeometry expected, DungeonGeometry baked)
{
    static string Key(Aabb s) =>
        $"{s.Min.X:0.##},{s.Min.Y:0.##},{s.Min.Z:0.##}:{s.Max.X:0.##},{s.Max.Y:0.##},{s.Max.Z:0.##}";
    static string SpawnKey(EnemySpawnPoint e) =>
        $"{e.Type}:{e.Position.X:0.##},{e.Position.Y:0.##},{e.Position.Z:0.##}";
    static string PropKey(DungeonProp p) =>
        $"{p.Type}:{p.Position.X:0.##},{p.Position.Y:0.##},{p.Position.Z:0.##}";

    void Demand(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException($"round-trip mismatch: {what} — the scene and the JSON disagree");
    }

    var a = expected.Terrain!;
    var b = baked.Terrain ?? throw new InvalidOperationException("round-trip mismatch: the baked scene has no terrain");
    Demand(a.Width == b.Width && a.Depth == b.Depth && MathF.Abs(a.CellSize - b.CellSize) < 0.001f
           && MathF.Abs(a.OriginX - b.OriginX) < 0.001f && MathF.Abs(a.OriginZ - b.OriginZ) < 0.001f,
        $"terrain grid ({a.Width}x{a.Depth}@{a.CellSize} vs {b.Width}x{b.Depth}@{b.CellSize})");
    var worst = 0f;
    for (var i = 0; i < a.Heights.Count; i++)
        worst = MathF.Max(worst, MathF.Abs(a.Heights[i] - b.Heights[i]));
    Demand(worst <= 0.002f, $"terrain heights (largest sample difference {worst})");

    Demand(Vector3.Distance(expected.SpawnPoint, baked.SpawnPoint) < 0.01f, "player spawn");
    Demand(expected.BossSpawn is { } eb && baked.BossSpawn is { } bb && Vector3.Distance(eb, bb) < 0.01f, "boss spawn");
    Demand(expected.Solids.Select(Key).OrderBy(k => k).SequenceEqual(baked.Solids.Select(Key).OrderBy(k => k)),
        $"solids ({expected.Solids.Count} vs {baked.Solids.Count})");
    Demand(expected.EnemySpawns.Select(SpawnKey).OrderBy(k => k)
            .SequenceEqual(baked.EnemySpawns.Select(SpawnKey).OrderBy(k => k)),
        $"enemy spawns ({expected.EnemySpawns.Count} vs {baked.EnemySpawns.Count})");
    Demand(expected.Props.Select(PropKey).OrderBy(k => k).SequenceEqual(baked.Props.Select(PropKey).OrderBy(k => k)),
        $"props ({expected.Props.Count} vs {baked.Props.Count})");
    Console.WriteLine("Round trip OK — the scene bakes back to the exact geometry the server hosts.");
}

// Generates The Crag — the first open realm — as ONE file, the authored-format
// Godot scene:
//
//   WoadRaiders.Client/maps/Crag.tscn   BUILT-IN Godot nodes and resources
//                                       ONLY — no scripts of any kind, so the
//                                       scene opens whole in any Godot editor
//                                       with nothing to build first. The
//                                       terrain is a subdivided PlaneMesh
//                                       displaced by a heightmap Image through
//                                       a ShaderMaterial (visible in editor
//                                       and game alike); the simulation's
//                                       heightfield rides as plain metadata on
//                                       the scene root; braziers, the dusk sky
//                                       and sun, stone visuals + collision,
//                                       and the spawn/enemy/boss markers are
//                                       ordinary nodes.
//
// A .NET 10 file-based app:
//
//   dotnet run tools/GenerateRealm.cs
//
// The .tscn IS the map: the server parses it directly through the shared
// scene-to-geometry pipeline (Core.DungeonSceneFile reads the root metadata —
// the same code this tool validates its output with below), so there is no
// JSON to export or keep in step. Hand-edit the scene freely; check it
// afterwards with tools/ValidateRealm.cs — the same bar hand-made realms pass.
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
// The tool validates its own output with the REAL simulation rules:
//   - a virtual raider walks the whole route with DungeonGeometry.Move at
//     player speed and must reach the boss court (and end up high);
//   - Core.RealmValidator proves every camp comfortably reachable, the borders
//     sealed even against slope-inching, and no reachable spot stranded;
//   - the JSON round-trips through DungeonGeometryFile.Parse.

#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Globalization;
using System.Numerics;
using System.Text;
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

// Heights are rounded to 3 decimals so the .tscn (text floats), the JSON, and
// the wire all carry the IDENTICAL value — re-exporting the scene reproduces
// the sim geometry bit for bit.
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

// ------------------------------------------------------------- write TSCN
// The same data as an authored-format Godot scene. Sub-resource recipes
// (flame stack, stone material) follow the conventions the old dungeon
// generators established, at world scale.

string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

// A DirectionalLight3D basis from YXZ Euler degrees (pitch around X, yaw
// around Y), written row-major as .tscn Transform3D expects: M = Ry * Rx.
string DirTransform(float pitchDeg, float yawDeg)
{
    var p = pitchDeg * MathF.PI / 180f;
    var y = yawDeg * MathF.PI / 180f;
    float cp = MathF.Cos(p), sp = MathF.Sin(p), cy = MathF.Cos(y), sy = MathF.Sin(y);
    float[] m = { cy, sy * sp, sy * cp, 0f, cp, -sp, -sy, cy * sp, cy * cp };
    return $"Transform3D({string.Join(", ", m.Select(F))}, 0, 0, 0)";
}

string At(float x, float yy, float z) => $"Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(x)}, {F(yy)}, {F(z)})";

// The terrain shader: displaces the subdivided plane by the heightmap (one
// texel per height sample, fetched exactly — no filtering), shades the land by
// height and steepness (the same biome banding the client's fallback renderer
// uses), and breaks it up with a cheap value noise. Pure built-in Godot: a
// Shader resource, not a script.
const string TerrainShaderCode = """
shader_type spatial;

// The land, displaced by the same heightfield the simulation walks (the sim
// copy rides as metadata on the scene root). The Terrain node must stay
// translation-only: model space is treated as world-aligned.
uniform sampler2D heightmap : filter_nearest;
uniform vec2 field_origin = vec2(0.0, 0.0);
uniform float cell_size = 40.0;

varying vec3 biome;

float height_at(ivec2 cell) {
	ivec2 size = textureSize(heightmap, 0);
	cell = clamp(cell, ivec2(0), size - 1);
	return texelFetch(heightmap, cell, 0).r;
}

void vertex() {
	vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	ivec2 cell = ivec2(round((world.xz - field_origin) / cell_size));
	float h = height_at(cell);
	VERTEX.y = h - MODEL_MATRIX[3].y;

	float hw = height_at(cell + ivec2(-1, 0));
	float he = height_at(cell + ivec2(1, 0));
	float hn = height_at(cell + ivec2(0, -1));
	float hs = height_at(cell + ivec2(0, 1));
	vec3 normal = normalize(vec3(hw - he, 2.0 * cell_size, hn - hs));
	NORMAL = normal;

	// The dusk-lit biome bands: gorge stone, glen grass, heather moor, pale
	// upland, bare crag — with steep ground shedding its soil into rock.
	vec3 gorge = vec3(0.14, 0.13, 0.15);
	vec3 glen = vec3(0.22, 0.32, 0.16);
	vec3 moor = vec3(0.30, 0.27, 0.18);
	vec3 upland = vec3(0.33, 0.33, 0.23);
	vec3 crag = vec3(0.33, 0.32, 0.35);
	vec3 by_height =
		h < -60.0 ? gorge :
		h < 20.0 ? mix(gorge, glen, (h + 60.0) / 80.0) :
		h < 140.0 ? mix(glen, moor, (h - 20.0) / 120.0) :
		h < 280.0 ? mix(moor, upland, (h - 140.0) / 140.0) :
		h < 420.0 ? mix(upland, crag, (h - 280.0) / 140.0) : crag;
	float rockiness = clamp((0.80 - normal.y) / 0.35, 0.0, 1.0);
	biome = mix(by_height, crag * 0.9, rockiness);
}

float noise_hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(noise_hash(i), noise_hash(i + vec2(1.0, 0.0)), f.x),
			mix(noise_hash(i + vec2(0.0, 1.0)), noise_hash(i + vec2(1.0, 1.0)), f.x), f.y);
}

void fragment() {
	vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float grain = value_noise(world.xz * 0.025) * 0.6 + value_noise(world.xz * 0.1) * 0.4;
	ALBEDO = biome * mix(0.82, 1.12, grain);
	ROUGHNESS = 1.0;
}
""";

var subCount = 25 + solids.Count * 2; // fixed recipes + a BoxMesh and BoxShape3D per solid

var scene = new StringBuilder();
scene.AppendLine($"[gd_scene load_steps={1 + subCount} format=3]");
scene.AppendLine();

// --- the sky and light rig
scene.AppendLine("""
[sub_resource type="ProceduralSkyMaterial" id="sky_mat"]
sky_top_color = Color(0.09, 0.12, 0.22, 1)
sky_horizon_color = Color(0.46, 0.28, 0.22, 1)
ground_bottom_color = Color(0.05, 0.05, 0.07, 1)
ground_horizon_color = Color(0.3, 0.2, 0.17, 1)
sun_angle_max = 30.0
sun_curve = 0.6

[sub_resource type="Sky" id="sky"]
sky_material = SubResource("sky_mat")

[sub_resource type="Environment" id="env"]
background_mode = 2
sky = SubResource("sky")
ambient_light_source = 3
ambient_light_energy = 0.55
fog_enabled = true
fog_light_color = Color(0.23, 0.2, 0.24, 1)
fog_density = 0.00016
fog_sky_affect = 0.25
""");

// --- the brazier bowl and its flame (the proven torch-flame recipe, at world scale)
scene.AppendLine("""
[sub_resource type="StandardMaterial3D" id="bowl_mat"]
albedo_color = Color(0.1, 0.09, 0.09, 1)
roughness = 0.9

[sub_resource type="CylinderMesh" id="bowl"]
material = SubResource("bowl_mat")
top_radius = 15.0
bottom_radius = 9.0
height = 26.0
radial_segments = 10

[sub_resource type="Gradient" id="flame_grad"]
offsets = PackedFloat32Array(0, 0.45, 1)
colors = PackedColorArray(0.9, 0.16, 0.04, 1, 0.72, 0.06, 0.02, 1, 0.35, 0.01, 0.005, 0)

[sub_resource type="GradientTexture1D" id="flame_ramp"]
gradient = SubResource("flame_grad")

[sub_resource type="Curve" id="flame_curve"]
_data = [Vector2(0, 1), 0.0, 0.0, 0, 0, Vector2(0.5, 0.4), 0.0, 0.0, 0, 0, Vector2(1, 0), 0.0, 0.0, 0, 0]
point_count = 3

[sub_resource type="CurveTexture" id="flame_scale"]
curve = SubResource("flame_curve")

[sub_resource type="Gradient" id="dot_grad"]
offsets = PackedFloat32Array(0, 0.6, 1)
colors = PackedColorArray(1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0)

[sub_resource type="GradientTexture2D" id="flame_dot"]
gradient = SubResource("dot_grad")
width = 32
height = 32
fill = 1
fill_from = Vector2(0.5, 0.5)
fill_to = Vector2(0.5, 0)

[sub_resource type="ParticleProcessMaterial" id="flame_process"]
emission_shape = 1
emission_sphere_radius = 3.0
direction = Vector3(0, 1, 0)
spread = 5.0
gravity = Vector3(0, 6, 0)
initial_velocity_min = 34.0
initial_velocity_max = 52.0
scale_min = 9.0
scale_max = 15.0
scale_curve = SubResource("flame_scale")
color = Color(1, 1, 1, 1)
color_ramp = SubResource("flame_ramp")

[sub_resource type="StandardMaterial3D" id="flame_mat"]
shading_mode = 0
transparency = 1
billboard_mode = 3
billboard_keep_scale = true
vertex_color_use_as_albedo = true
albedo_texture = SubResource("flame_dot")

[sub_resource type="QuadMesh" id="flame_mesh"]
material = SubResource("flame_mat")
size = Vector2(1, 1)
""");

// --- the terrain: a heightmap Image displacing a subdivided PlaneMesh through
// --- a ShaderMaterial — all built-in resources, no scripts. The shader also
// --- shades the land by height and steepness (the biome banding) and breaks
// --- it up with a cheap value noise, mirroring the client's fallback look.
var heightBytes = new byte[heights.Length * 4];
Buffer.BlockCopy(heights, 0, heightBytes, 0, heightBytes.Length); // little-endian floats, row-major
scene.AppendLine("""[sub_resource type="Image" id="hmap_img"]""");
scene.AppendLine("data = {");
scene.AppendLine($"\"data\": PackedByteArray({string.Join(", ", heightBytes)}),");
scene.AppendLine("\"format\": \"RFloat\",");
scene.AppendLine($"\"height\": {D},");
scene.AppendLine("\"mipmaps\": false,");
scene.AppendLine($"\"width\": {W}");
scene.AppendLine("}");
scene.AppendLine();
scene.AppendLine("""
[sub_resource type="ImageTexture" id="hmap_tex"]
image = SubResource("hmap_img")
""");
scene.AppendLine("""[sub_resource type="Shader" id="terrain_shader"]""");
scene.AppendLine("code = \"" + TerrainShaderCode + "\"");
scene.AppendLine();
scene.AppendLine("""[sub_resource type="ShaderMaterial" id="terrain_mat"]""");
scene.AppendLine("shader = SubResource(\"terrain_shader\")");
scene.AppendLine("shader_parameter/heightmap = SubResource(\"hmap_tex\")");
scene.AppendLine("shader_parameter/field_origin = Vector2(0, 0)");
scene.AppendLine($"shader_parameter/cell_size = {F(Cell)}");
scene.AppendLine();
scene.AppendLine($"[sub_resource type=\"PlaneMesh\" id=\"terrain_plane\"]");
scene.AppendLine("material = SubResource(\"terrain_mat\")");
scene.AppendLine($"size = Vector2({F((W - 1) * Cell)}, {F((D - 1) * Cell)})");
scene.AppendLine($"subdivide_width = {W - 2}"); // subdivisions + 2 = vertices → one per height sample
scene.AppendLine($"subdivide_depth = {D - 2}");
scene.AppendLine();

// --- weathered stone for the solids (world-triplanar noise, like the client's)
scene.AppendLine("""
[sub_resource type="FastNoiseLite" id="stone_noise_a"]
noise_type = 3
seed = 3
frequency = 0.05

[sub_resource type="Gradient" id="stone_ramp"]
offsets = PackedFloat32Array(0, 1)
colors = PackedColorArray(0.21, 0.2, 0.22, 1, 0.38, 0.37, 0.41, 1)

[sub_resource type="NoiseTexture2D" id="stone_albedo"]
seamless = true
color_ramp = SubResource("stone_ramp")
noise = SubResource("stone_noise_a")

[sub_resource type="FastNoiseLite" id="stone_noise_n"]
noise_type = 3
seed = 4
frequency = 0.08

[sub_resource type="NoiseTexture2D" id="stone_normal"]
seamless = true
as_normal_map = true
bump_strength = 3.0
noise = SubResource("stone_noise_n")

[sub_resource type="StandardMaterial3D" id="stone_mat"]
albedo_texture = SubResource("stone_albedo")
normal_enabled = true
normal_texture = SubResource("stone_normal")
roughness = 0.95
uv1_triplanar = true
uv1_world_triplanar = true
uv1_scale = Vector3(0.012, 0.012, 0.012)
""");

// --- a visual box and a collision shape per solid
for (var i = 0; i < solids.Count; i++)
{
    var size = solids[i].Size;
    scene.AppendLine($"[sub_resource type=\"BoxMesh\" id=\"box_{i}\"]");
    scene.AppendLine("material = SubResource(\"stone_mat\")");
    scene.AppendLine($"size = Vector3({F(size.X)}, {F(size.Y)}, {F(size.Z)})");
    scene.AppendLine();
    scene.AppendLine($"[sub_resource type=\"BoxShape3D\" id=\"shape_{i}\"]");
    scene.AppendLine($"size = Vector3({F(size.X)}, {F(size.Y)}, {F(size.Z)})");
    scene.AppendLine();
}

// --- nodes
// The root carries the SIMULATION heightfield as plain metadata — the built-in
// way to store data on a node, readable in any editor's inspector, and what
// the scene-to-geometry pipeline (Core.DungeonSceneFile) reads to host the map.
scene.AppendLine("""[node name="Crag" type="Node3D"]""");
scene.AppendLine("metadata/terrain_origin_x = 0.0");
scene.AppendLine("metadata/terrain_origin_z = 0.0");
scene.AppendLine($"metadata/terrain_cell_size = {F(Cell)}");
scene.AppendLine($"metadata/terrain_width = {W}");
scene.AppendLine($"metadata/terrain_depth = {D}");
scene.AppendLine($"metadata/terrain_heights = PackedFloat32Array({string.Join(", ", heights.Select(F))})");
scene.AppendLine();
scene.AppendLine("""
[node name="Environment" type="WorldEnvironment" parent="."]
environment = SubResource("env")
""");
scene.AppendLine("""[node name="Sun" type="DirectionalLight3D" parent="."]""");
scene.AppendLine($"transform = {DirTransform(-26f, -40f)}");
scene.AppendLine("""
light_color = Color(1, 0.8, 0.58, 1)
light_energy = 1.05
shadow_enabled = true
directional_shadow_max_distance = 2400.0
""");
scene.AppendLine("""[node name="Fill" type="DirectionalLight3D" parent="."]""");
scene.AppendLine($"transform = {DirTransform(-32f, 145f)}");
scene.AppendLine("""
light_color = Color(0.55, 0.62, 0.85, 1)
light_energy = 0.18
""");

// The terrain visual: the shader-displaced plane, centred over the field. Its
// bounding box must be told about the displacement (a GPU-displaced mesh keeps
// its flat AABB and would be frustum-culled wrongly otherwise); "no_fade"
// keeps the occlusion fader off the land. The node must stay translation-only
// — the shader treats its model space as world-aligned.
var lowest = heights.Min();
var highest = heights.Max();
scene.AppendLine("""[node name="Terrain" type="MeshInstance3D" parent="." groups=["no_fade"]]""");
scene.AppendLine($"transform = {At((W - 1) * Cell / 2f, 0, (D - 1) * Cell / 2f)}");
scene.AppendLine("mesh = SubResource(\"terrain_plane\")");
scene.AppendLine($"custom_aabb = AABB({F(-(W - 1) * Cell / 2f)}, {F(lowest - 10f)}, {F(-(D - 1) * Cell / 2f)}, " +
                 $"{F((W - 1) * Cell)}, {F(highest - lowest + 20f)}, {F((D - 1) * Cell)})");
scene.AppendLine();

// Solid visuals and their matching collision (the collision is the sim truth
// the exporter reads back; keep the pairs in step when hand-editing).
scene.AppendLine("""[node name="SolidVisuals" type="Node3D" parent="."]""");
scene.AppendLine();
scene.AppendLine("""[node name="Static" type="StaticBody3D" parent="."]""");
scene.AppendLine();
for (var i = 0; i < solids.Count; i++)
{
    var c = solids[i].Center;
    scene.AppendLine($"[node name=\"Solid_{i}\" type=\"MeshInstance3D\" parent=\"SolidVisuals\"]");
    scene.AppendLine($"transform = {At(c.X, c.Y, c.Z)}");
    scene.AppendLine($"mesh = SubResource(\"box_{i}\")");
    scene.AppendLine();
    scene.AppendLine($"[node name=\"Col_{i}\" type=\"CollisionShape3D\" parent=\"Static\"]");
    scene.AppendLine($"transform = {At(c.X, c.Y, c.Z)}");
    scene.AppendLine($"shape = SubResource(\"shape_{i}\")");
    scene.AppendLine();
}

// Braziers: real nodes (bowl + ember light + flame), each in the "brazier"
// group so the export tool carries them into the sim geometry as props.
scene.AppendLine("""[node name="Braziers" type="Node3D" parent="."]""");
scene.AppendLine();
for (var i = 0; i < props.Count; i++)
{
    var p = props[i].Position;
    scene.AppendLine($"[node name=\"Brazier{i}\" type=\"Node3D\" parent=\"Braziers\" groups=[\"brazier\"]]");
    scene.AppendLine($"transform = {At(p.X, p.Y, p.Z)}");
    scene.AppendLine();
    scene.AppendLine($"[node name=\"Bowl\" type=\"MeshInstance3D\" parent=\"Braziers/Brazier{i}\"]");
    scene.AppendLine($"transform = {At(0, 13, 0)}");
    scene.AppendLine("mesh = SubResource(\"bowl\")");
    scene.AppendLine();
    scene.AppendLine($"[node name=\"Ember\" type=\"OmniLight3D\" parent=\"Braziers/Brazier{i}\"]");
    scene.AppendLine($"transform = {At(0, 46, 0)}");
    scene.AppendLine("light_color = Color(1, 0.62, 0.3, 1)");
    scene.AppendLine("light_energy = 6.0");
    scene.AppendLine("omni_range = 380.0");
    scene.AppendLine();
    scene.AppendLine($"[node name=\"Flame\" type=\"GPUParticles3D\" parent=\"Braziers/Brazier{i}\"]");
    scene.AppendLine($"transform = {At(0, 28, 0)}");
    scene.AppendLine("amount = 18");
    scene.AppendLine("lifetime = 0.6");
    scene.AppendLine("randomness = 0.4");
    scene.AppendLine("preprocess = 0.6");
    scene.AppendLine("process_material = SubResource(\"flame_process\")");
    scene.AppendLine("draw_pass_1 = SubResource(\"flame_mesh\")");
    scene.AppendLine();
}

// Markers: the sim cast, in the exporter's naming convention.
scene.AppendLine("""[node name="PlayerSpawn" type="Marker3D" parent="."]""");
scene.AppendLine($"transform = {At(playerSpawn.X, playerSpawn.Y, playerSpawn.Z)}");
scene.AppendLine();
for (var i = 0; i < enemies.Length; i++)
{
    var e = enemies[i];
    var suffix = e.type switch { EnemyType.Rogue => "_Rogue", EnemyType.Mage => "_Mage", _ => "" };
    var pos = OnGround(e.x, e.z);
    scene.AppendLine($"[node name=\"EnemySpawn{i}{suffix}\" type=\"Marker3D\" parent=\".\"]");
    scene.AppendLine($"transform = {At(pos.X, pos.Y, pos.Z)}");
    scene.AppendLine();
}
scene.AppendLine("""[node name="BossSpawn" type="Marker3D" parent="."]""");
scene.AppendLine($"transform = {At(bossSpawn.X, bossSpawn.Y, bossSpawn.Z)}");

var scenePath = Path.Combine("WoadRaiders.Client", "maps", "Crag.tscn");
File.WriteAllText(scenePath, scene.ToString());

// ------------------------------------------------------------- validation
// Read the scene BACK through the shared scene-to-geometry pipeline — the
// exact code the server uses to host it — and validate what actually parsed,
// not what this tool intended to write.

var geometry = DungeonSceneFile.Load(scenePath);
if (geometry.Terrain is null || geometry.EnemySpawns.Count != enemies.Length
    || geometry.Solids.Count != solids.Count || geometry.Props.Count != props.Count
    || geometry.BossSpawn is null)
    throw new InvalidOperationException(
        $"the scene did not parse back whole: terrain={geometry.Terrain is not null}, " +
        $"{geometry.EnemySpawns.Count}/{enemies.Length} camps, {geometry.Solids.Count}/{solids.Count} solids, " +
        $"{geometry.Props.Count}/{props.Count} props, boss={geometry.BossSpawn is not null}");

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

var span = geometry.Bounds;
Console.WriteLine($"Wrote Crag.tscn (parsed back through DungeonSceneFile): {W}x{D} terrain samples over " +
                  $"{(W - 1) * Cell:0}x{(D - 1) * Cell:0} units (heights {span.Min.Y:0}..{span.Max.Y:0}), " +
                  $"{solids.Count} solids, {props.Count} braziers, {enemies.Length} enemy camps + the boss.");

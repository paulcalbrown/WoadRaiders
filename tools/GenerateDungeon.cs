// Generates The Cairn — the second dungeon — as a matched pair of files:
// an authored-quality Godot scene (floors, walls, torches with flickering
// lights and flame particles, a lighting rig) and the server's collision
// geometry JSON. A .NET 10 file-based app:
//
//   dotnet run tools/GenerateDungeon.cs
//
// writes WoadRaiders.Client/maps/Cairn.tscn and Cairn.json.
//
// The layout is computed, not drawn: a central boss rotunda ringed by four
// pillars, four radial arms out to a wide ring corridor, stair-stepped
// diagonal passages to four burial chambers, and a western entrance hall.
// Every convention (80-unit tile grid, scale-20 Visuals node, edge-centred
// collision slabs, torch light/flame placement, the flicker animation, the
// WorldEnvironment values) is lifted verbatim from the hand-evolved
// Barrow.tscn so the two dungeons read as one world.
//
// The tool validates its own output: the JSON round-trips through
// DungeonGeometryFile.Parse, and a flood fill proves the boss and every
// enemy spawn are reachable from the player spawn.

#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Globalization;
using System.Numerics;
using System.Text;
using WoadRaiders.Core;

const int Grid = 45;            // tiles per side; world is Grid * 80 units square
const float Tile = 80f;         // world units per tile
const float KayTile = 4f;       // KayKit units per tile (Visuals node is scale 20)
const int Cx = 22, Cz = 22;     // the rotunda's centre tile

// ---------------------------------------------------------------- layout

// One place decides what is open floor; everything else derives from it.
bool Open(int x, int z)
{
    if (x < 1 || z < 1 || x >= Grid - 1 || z >= Grid - 1)
        return false;
    float dx = x - Cx, dz = z - Cz;
    var r = MathF.Sqrt(dx * dx + dz * dz);

    if (r <= 5.2f)
        return true;                                     // the boss rotunda
    if (r >= 8.4f && r <= 11.6f)
        return true;                                     // the ring corridor
    if (MathF.Abs(dx) <= 1 && MathF.Abs(dz) >= 4 && MathF.Abs(dz) <= 9)
        return true;                                     // north/south arms
    if (MathF.Abs(dz) <= 1 && MathF.Abs(dx) >= 4 && MathF.Abs(dx) <= 9)
        return true;                                     // east/west arms
    if (MathF.Abs(MathF.Abs(dx) - MathF.Abs(dz)) <= 1 && r >= 10.5f && r <= 16f)
        return true;                                     // diagonal passages
    foreach (var (chx, chz) in ChamberCentres())
        if (Math.Abs(x - chx) <= 3 && Math.Abs(z - chz) <= 3)
            return true;                                 // the burial chambers
    if (x >= 3 && x <= 14 && Math.Abs(z - Cz) <= 1)
        return true;                                     // the entrance hall
    return false;
}

static (int, int)[] ChamberCentres() =>
    [(Cx + 11, Cz + 11), (Cx - 11, Cz + 11), (Cx + 11, Cz - 11), (Cx - 11, Cz - 11)];

// Free-standing set dressing with collision, all on open tiles.
(int x, int z)[] pillars = [(Cx - 3, Cz - 3), (Cx + 3, Cz - 3), (Cx - 3, Cz + 3), (Cx + 3, Cz + 3)];
var columns = ChamberCentres().SelectMany(c => new[] { (c.Item1 - 2, c.Item2 - 2), (c.Item1 + 2, c.Item2 + 2) }).ToArray();

// The cast: spawn at the west door, boss enthroned at the centre.
var playerSpawn = (x: 5, z: Cz);
var bossSpawn = (x: Cx, z: Cz);
(EnemyType type, int x, int z)[] enemies =
[
    // Minions hold the entrance, the arms, and the ring's cardinal reaches.
    (EnemyType.Minion, 6, 22), (EnemyType.Minion, 8, 21), (EnemyType.Minion, 10, 23), (EnemyType.Minion, 12, 22),
    (EnemyType.Minion, 13, 18), (EnemyType.Minion, 13, 26),
    (EnemyType.Minion, 21, 15), (EnemyType.Minion, 23, 16),
    (EnemyType.Minion, 21, 28), (EnemyType.Minion, 23, 29),
    (EnemyType.Minion, 28, 21), (EnemyType.Minion, 29, 23),
    (EnemyType.Minion, 18, 13), (EnemyType.Minion, 26, 13),
    (EnemyType.Minion, 18, 31), (EnemyType.Minion, 26, 31),
    // Rogues lurk in the burial chambers.
    (EnemyType.Rogue, 32, 33), (EnemyType.Rogue, 34, 33), (EnemyType.Rogue, 10, 33), (EnemyType.Rogue, 12, 33),
    (EnemyType.Rogue, 32, 11), (EnemyType.Rogue, 34, 11), (EnemyType.Rogue, 10, 11), (EnemyType.Rogue, 12, 11),
    // Mages command the ring, zapping down its long sightlines.
    (EnemyType.Mage, 22, 12), (EnemyType.Mage, 22, 32), (EnemyType.Mage, 32, 22),
    (EnemyType.Mage, 29, 29), (EnemyType.Mage, 15, 15), (EnemyType.Mage, 15, 29),
];

// ------------------------------------------------------------- validation

void MustBeOpen(string what, int x, int z)
{
    if (!Open(x, z))
        throw new InvalidOperationException($"{what} at tile ({x},{z}) is inside solid rock — adjust the layout");
}

MustBeOpen("player spawn", playerSpawn.x, playerSpawn.z);
MustBeOpen("boss spawn", bossSpawn.x, bossSpawn.z);
foreach (var (type, x, z) in enemies)
    MustBeOpen($"{type} spawn", x, z);
foreach (var (x, z) in pillars.Concat(columns))
    MustBeOpen("decor", x, z);

// Flood fill from the player spawn: every spawn must be walkable-to.
var reachable = new bool[Grid, Grid];
var frontier = new Queue<(int x, int z)>();
reachable[playerSpawn.x, playerSpawn.z] = true;
frontier.Enqueue(playerSpawn);
while (frontier.Count > 0)
{
    var (fx, fz) = frontier.Dequeue();
    foreach (var (nx, nz) in new[] { (fx + 1, fz), (fx - 1, fz), (fx, fz + 1), (fx, fz - 1) })
    {
        if (nx < 0 || nz < 0 || nx >= Grid || nz >= Grid || reachable[nx, nz] || !Open(nx, nz))
            continue;
        reachable[nx, nz] = true;
        frontier.Enqueue((nx, nz));
    }
}
if (!reachable[bossSpawn.x, bossSpawn.z])
    throw new InvalidOperationException("the boss chamber is sealed off from the spawn");
foreach (var (type, x, z) in enemies)
    if (!reachable[x, z])
        throw new InvalidOperationException($"the {type} at ({x},{z}) is sealed off from the spawn");

// -------------------------------------------------------------- geometry

string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
float WorldC(int t) => t * Tile + Tile / 2f; // tile centre, world units
float KayC(int t) => t * KayTile + KayTile / 2f;

var solids = new List<Aabb>();
var rng = new Random(1229); // deterministic: same layout, same dungeon, every run

// Wall edges: one wall per boundary between an open tile and rock. Keyed so a
// shared edge is emitted exactly once. Horizontal edges run E-W at z = ez*80;
// vertical edges run N-S at x = ex*80.
var hEdges = new SortedSet<(int x, int ez)>();
var vEdges = new SortedSet<(int ex, int z)>();
for (var x = 0; x < Grid; x++)
{
    for (var z = 0; z < Grid; z++)
    {
        if (!Open(x, z))
            continue;
        if (!Open(x, z - 1)) hEdges.Add((x, z));
        if (!Open(x, z + 1)) hEdges.Add((x, z + 1));
        if (!Open(x - 1, z)) vEdges.Add((x, z));
        if (!Open(x + 1, z)) vEdges.Add((x + 1, z));
    }
}

foreach (var (x, ez) in hEdges)
    solids.Add(new Aabb(new Vector3(x * Tile, 0, ez * Tile - 10), new Vector3(x * Tile + Tile, Tile, ez * Tile + 10)));
foreach (var (ex, z) in vEdges)
    solids.Add(new Aabb(new Vector3(ex * Tile - 10, 0, z * Tile), new Vector3(ex * Tile + 10, Tile, z * Tile + Tile)));
foreach (var (x, z) in pillars)
    solids.Add(new Aabb(new Vector3(WorldC(x) - 15, 0, WorldC(z) - 15), new Vector3(WorldC(x) + 15, 80, WorldC(z) + 15)));
foreach (var (x, z) in columns)
    solids.Add(new Aabb(new Vector3(WorldC(x) - 8, 0, WorldC(z) - 8), new Vector3(WorldC(x) + 8, 28, WorldC(z) + 8)));

// Torches: spaced organically along the walls — a hash picks candidate tiles,
// a greedy min-distance pass keeps them from clumping.
var torches = new List<(int x, int z, char side)>();
for (var z = 0; z < Grid; z++)
{
    for (var x = 0; x < Grid; x++)
    {
        if (!Open(x, z) || (x * 7 + z * 13) % 9 != 0)
            continue;
        var side = !Open(x, z - 1) ? 'N' : !Open(x, z + 1) ? 'S' : !Open(x - 1, z) ? 'W' : !Open(x + 1, z) ? 'E' : ' ';
        if (side == ' ')
            continue;
        if (torches.Any(t => Math.Abs(t.x - x) + Math.Abs(t.z - z) < 4))
            continue;
        torches.Add((x, z, side));
    }
}

// ------------------------------------------------------------- write JSON

var geometry = new DungeonGeometry(
    new Vector3(WorldC(playerSpawn.x), 0, WorldC(playerSpawn.z)),
    solids,
    enemies.Select(e => new EnemySpawnPoint(new Vector3(WorldC(e.x), 0, WorldC(e.z)), e.type)).ToList())
{
    ScenePath = "res://maps/Cairn.tscn",
    BossSpawn = new Vector3(WorldC(bossSpawn.x), 0, WorldC(bossSpawn.z)),
};
var json = DungeonGeometryFile.ToJson(geometry);
DungeonGeometryFile.Parse(json); // must survive its own round trip
File.WriteAllText(Path.Combine("WoadRaiders.Client", "maps", "Cairn.json"), json);

// ------------------------------------------------------------- write TSCN

var ext = new StringBuilder();
ext.AppendLine("""[ext_resource type="PackedScene" path="res://addons/kaykit_dungeon_remastered/Assets/gltf/floor_tile_large.gltf.glb" id="1_floor"]""");
ext.AppendLine("""[ext_resource type="PackedScene" path="res://addons/kaykit_dungeon_remastered/Assets/gltf/floor_dirt_large_rocky.gltf.glb" id="2_floorvar"]""");
ext.AppendLine("""[ext_resource type="PackedScene" path="res://addons/kaykit_dungeon_remastered/Assets/gltf/wall.gltf.glb" id="3_wall"]""");
ext.AppendLine("""[ext_resource type="PackedScene" path="res://addons/kaykit_dungeon_remastered/Assets/gltf/pillar_decorated.gltf.glb" id="4_pillar"]""");
ext.AppendLine("""[ext_resource type="PackedScene" path="res://addons/kaykit_dungeon_remastered/Assets/gltf/torch_mounted.gltf.glb" id="5_torch"]""");
ext.AppendLine("""[ext_resource type="PackedScene" path="res://addons/kaykit_dungeon_remastered/Assets/gltf/column.gltf.glb" id="6_column"]""");
const int ExtCount = 6;

// Sub-resources: collision shapes, the flame particle stack, the flicker
// animation, its library, and the environment — values lifted from the Barrow.
var subs = new StringBuilder();
subs.AppendLine("""
[sub_resource type="BoxShape3D" id="shape_ns"]
size = Vector3(80, 80, 20)

[sub_resource type="BoxShape3D" id="shape_ew"]
size = Vector3(20, 80, 80)

[sub_resource type="BoxShape3D" id="shape_pillar"]
size = Vector3(30, 80, 30)

[sub_resource type="BoxShape3D" id="shape_column"]
size = Vector3(16, 28, 16)

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

// The flicker: one energy track per torch light, phase-scrambled per torch.
subs.AppendLine("""[sub_resource type="Animation" id="flicker_anim"]""");
subs.AppendLine("resource_name = \"flicker\"");
subs.AppendLine("length = 2.4");
subs.AppendLine("loop_mode = 1");
for (var i = 0; i < torches.Count; i++)
{
    var values = string.Join(", ", Enumerable.Range(0, 8)
        .Select(_ => F(5f + (float)rng.NextDouble() * 3.5f)));
    subs.AppendLine($"tracks/{i}/type = \"value\"");
    subs.AppendLine($"tracks/{i}/imported = false");
    subs.AppendLine($"tracks/{i}/enabled = true");
    subs.AppendLine($"tracks/{i}/path = NodePath(\"Lights/TorchLight{i}:light_energy\")");
    subs.AppendLine($"tracks/{i}/interp = 1");
    subs.AppendLine($"tracks/{i}/loop_wrap = true");
    subs.AppendLine($"tracks/{i}/keys = {{");
    subs.AppendLine("\"times\": PackedFloat32Array(0, 0.3, 0.6, 0.9, 1.2, 1.5, 1.8, 2.1),");
    subs.AppendLine("\"transitions\": PackedFloat32Array(1, 1, 1, 1, 1, 1, 1, 1),");
    subs.AppendLine("\"update\": 0,");
    subs.AppendLine($"\"values\": [{values}]");
    subs.AppendLine("}");
}
subs.AppendLine();
subs.AppendLine("""
[sub_resource type="AnimationLibrary" id="flicker_lib"]
_data = {
"flicker": SubResource("flicker_anim")
}

[sub_resource type="Environment" id="dungeon_env"]
background_mode = 1
background_color = Color(0.015, 0.015, 0.025, 1)
ambient_light_source = 2
ambient_light_color = Color(0.28, 0.3, 0.48, 1)
ambient_light_energy = 0.12
fog_enabled = true
fog_light_color = Color(0.03, 0.03, 0.05, 1)
fog_density = 0.0005
""");
const int SubCount = 13 + 3; // shapes+flame stack+env(13) + flicker anim + library + one spare slot Godot tolerates

var nodes = new StringBuilder();
nodes.AppendLine("""[node name="Cairn" type="Node3D"]""");
nodes.AppendLine();
nodes.AppendLine("""
[node name="Environment" type="WorldEnvironment" parent="."]
environment = SubResource("dungeon_env")

[node name="KeyLight" type="DirectionalLight3D" parent="."]
transform = Transform3D(0.643, 0.628, -0.439, 0, 0.574, 0.819, 0.766, -0.527, 0.369, 0, 0, 0)
light_color = Color(0.7, 0.78, 1, 1)
light_energy = 0.28
shadow_enabled = true

[node name="FillLight" type="DirectionalLight3D" parent="."]
transform = Transform3D(-0.643, -0.324, 0.694, 0, 0.906, 0.423, -0.766, 0.272, -0.583, 0, 0, 0)
light_energy = 0.08

[node name="Static" type="StaticBody3D" parent="."]
""");

// Collision shapes mirror the JSON solids 1:1.
foreach (var (x, ez) in hEdges)
    nodes.AppendLine($"[node name=\"ColN_{x}_{ez}\" type=\"CollisionShape3D\" parent=\"Static\"]\n" +
                     $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(x * Tile + 40)}, 40, {F(ez * Tile)})\n" +
                     "shape = SubResource(\"shape_ns\")\n");
foreach (var (ex, z) in vEdges)
    nodes.AppendLine($"[node name=\"ColW_{ex}_{z}\" type=\"CollisionShape3D\" parent=\"Static\"]\n" +
                     $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(ex * Tile)}, 40, {F(z * Tile + 40)})\n" +
                     "shape = SubResource(\"shape_ew\")\n");
foreach (var (x, z) in pillars)
    nodes.AppendLine($"[node name=\"ColPillar_{x}_{z}\" type=\"CollisionShape3D\" parent=\"Static\"]\n" +
                     $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(WorldC(x))}, 40, {F(WorldC(z))})\n" +
                     "shape = SubResource(\"shape_pillar\")\n");
foreach (var (x, z) in columns)
    nodes.AppendLine($"[node name=\"ColColumn_{x}_{z}\" type=\"CollisionShape3D\" parent=\"Static\"]\n" +
                     $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(WorldC(x))}, 14, {F(WorldC(z))})\n" +
                     "shape = SubResource(\"shape_column\")\n");

// Visuals live in KayKit space under a scale-20 holder, like the Barrow.
nodes.AppendLine("[node name=\"Visuals\" type=\"Node3D\" parent=\".\"]");
nodes.AppendLine("transform = Transform3D(20, 0, 0, 0, 20, 0, 0, 0, 20, 0, 0, 0)");
nodes.AppendLine();

var floors = 0;
for (var z = 0; z < Grid; z++)
{
    for (var x = 0; x < Grid; x++)
    {
        if (!Open(x, z))
            continue;
        floors++;
        var res = rng.Next(9) == 0 ? "2_floorvar" : "1_floor";
        nodes.AppendLine($"[node name=\"Floor_{x}_{z}\" parent=\"Visuals\" instance=ExtResource(\"{res}\")]\n" +
                         $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(KayC(x))}, 0, {F(KayC(z))})\n");
    }
}
foreach (var (x, ez) in hEdges)
    nodes.AppendLine($"[node name=\"WallN_{x}_{ez}\" parent=\"Visuals\" instance=ExtResource(\"3_wall\")]\n" +
                     $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(KayC(x))}, 0, {F(ez * KayTile)})\n");
foreach (var (ex, z) in vEdges)
    nodes.AppendLine($"[node name=\"WallW_{ex}_{z}\" parent=\"Visuals\" instance=ExtResource(\"3_wall\")]\n" +
                     $"transform = Transform3D(0, 0, 1, 0, 1, 0, -1, 0, 0, {F(ex * KayTile)}, -0.002, {F(KayC(z))})\n");
foreach (var (x, z) in pillars)
    nodes.AppendLine($"[node name=\"Pillar_{x}_{z}\" parent=\"Visuals\" instance=ExtResource(\"4_pillar\")]\n" +
                     $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(KayC(x))}, 0, {F(KayC(z))})\n");
foreach (var (x, z) in columns)
    nodes.AppendLine($"[node name=\"Column_{x}_{z}\" parent=\"Visuals\" instance=ExtResource(\"6_column\")]\n" +
                     $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(KayC(x))}, 0, {F(KayC(z))})\n");

// Torch props (KayKit space) with their lights and flames (world space).
var torchProps = new StringBuilder();
var lights = new StringBuilder();
var flames = new StringBuilder();
lights.AppendLine("[node name=\"Lights\" type=\"Node3D\" parent=\".\"]");
lights.AppendLine();
flames.AppendLine("[node name=\"Flames\" type=\"Node3D\" parent=\".\"]");
flames.AppendLine();
for (var i = 0; i < torches.Count; i++)
{
    var (x, z, side) = torches[i];
    // The prop hugs its wall, half a KayKit unit into the room, 2.2 up.
    var (px, pz, basis) = side switch
    {
        'N' => (KayC(x), z * KayTile + 0.5f, "1, 0, 0, 0, 1, 0, 0, 0, 1"),
        'S' => (KayC(x), (z + 1) * KayTile - 0.5f, "-1, 0, 0, 0, 1, 0, 0, 0, -1"),
        'W' => (x * KayTile + 0.5f, KayC(z), "0, 0, -1, 0, 1, 0, 1, 0, 0"),
        _ => ((x + 1) * KayTile - 0.5f, KayC(z), "0, 0, 1, 0, 1, 0, -1, 0, 0"),
    };
    torchProps.AppendLine($"[node name=\"Torch{i}\" parent=\"Visuals\" instance=ExtResource(\"5_torch\")]\n" +
                          $"transform = Transform3D({basis}, {F(px)}, 2.2, {F(pz)})\n");

    // Light and flame sit 20 world units into the room from the wall plane.
    var (lx, lz) = side switch
    {
        'N' => (WorldC(x), z * Tile + 20),
        'S' => (WorldC(x), (z + 1) * Tile - 20),
        'W' => (x * Tile + 20, WorldC(z)),
        _ => ((x + 1) * Tile - 20, WorldC(z)),
    };
    lights.AppendLine($"[node name=\"TorchLight{i}\" type=\"OmniLight3D\" parent=\"Lights\"]\n" +
                      $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(lx)}, 58, {F(lz)})\n" +
                      "light_color = Color(1, 0.62, 0.32, 1)\n" +
                      "light_energy = 7.0\n" +
                      "omni_range = 440.0\n");
    flames.AppendLine($"[node name=\"Flame{i}\" type=\"GPUParticles3D\" parent=\"Flames\"]\n" +
                      $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(lx)}, 56, {F(lz)})\n" +
                      "amount = 18\n" +
                      "lifetime = 0.6\n" +
                      "randomness = 0.4\n" +
                      "preprocess = 0.6\n" +
                      "process_material = SubResource(\"flame_process\")\n" +
                      "draw_pass_1 = SubResource(\"flame_mesh\")\n");
}

var tail = new StringBuilder();
tail.AppendLine("""
[node name="TorchFlicker" type="AnimationPlayer" parent="."]
libraries = {
"": SubResource("flicker_lib")
}
autoplay = "flicker"
""");
tail.AppendLine($"[node name=\"PlayerSpawn\" type=\"Marker3D\" parent=\".\"]\n" +
                $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(WorldC(playerSpawn.x))}, 0, {F(WorldC(playerSpawn.z))})\n");
tail.AppendLine($"[node name=\"BossSpawn\" type=\"Marker3D\" parent=\".\"]\n" +
                $"transform = Transform3D(1, 0, 0, 0, 1, 0, 0, 0, 1, {F(WorldC(bossSpawn.x))}, 0, {F(WorldC(bossSpawn.z))})");

var scene = new StringBuilder();
scene.AppendLine($"[gd_scene load_steps={1 + ExtCount + SubCount} format=3]");
scene.AppendLine();
scene.Append(ext).AppendLine();
scene.Append(subs).AppendLine();
scene.Append(nodes);
scene.Append(torchProps);
scene.Append(lights);
scene.Append(flames);
scene.Append(tail);
File.WriteAllText(Path.Combine("WoadRaiders.Client", "maps", "Cairn.tscn"), scene.ToString());

Console.WriteLine($"Wrote Cairn.json ({solids.Count} solids, {enemies.Length} enemy spawns + boss) " +
                  $"and Cairn.tscn ({floors} floor tiles, {hEdges.Count + vEdges.Count} walls, {torches.Count} torches).");

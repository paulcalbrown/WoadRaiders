// Generates The Crag — the first open realm. The SCENE comes first, and the
// served geometry is baked from it:
//
//   1. WoadRaiders.Client/scripts/tools/CragDesign.cs computes the realm (the
//      layout math: terrain plates, noise, solids, the cast, the scenery) and
//      RealmSceneBuilder builds the full Godot scene from it — with the WHOLE
//      engine available: any meshes, materials, particles, or asset kits. The
//      scene is saved by Godot's own ResourceSaver, so Crag.tscn is exactly
//      what a naturally-authored scene looks like: built-in nodes only, a real
//      displaced terrain mesh, free-form scenery (the boulder fields), no
//      scripts, no metadata.
//   2. The STANDARD hand-made pipeline (tools/bake_realm.gd) then bakes
//      Crag.json out of that scene — sampling the real terrain mesh, reading
//      the collision boxes, markers, and brazier group — the same way any
//      Blender-sculpted realm becomes a hostable map. The JSON is derived
//      output; nothing is ever generated FROM it.
//
// This tool orchestrates and validates the chain. A .NET 10 file-based app
// (godot-mono and the client build are driven automatically):
//
//   dotnet run tools/GenerateRealm.cs
//
// Validation runs the REAL simulation rules against the BAKED geometry — the
// very bytes the server will host:
//   - a virtual raider walks the whole route with DungeonGeometry.Move at
//     player speed and must reach the boss court (and end up high);
//   - Core.RealmValidator proves every camp comfortably reachable, the borders
//     sealed even against slope-inching, and no reachable spot stranded.
//
// To reshape the realm, edit CragDesign.cs (and the route below if the path
// itself moves), then rerun this tool. Hand-edits to Crag.tscn are equally
// legitimate — re-run the bake + ValidateRealm afterwards, like any hand-made
// realm.

#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using WoadRaiders.Core;

var scenePath = Path.Combine("WoadRaiders.Client", "maps", "Crag.tscn");
var jsonPath = Path.Combine("WoadRaiders.Client", "maps", "Crag.json");

// ------------------------------------------------------------- build the scene

Step("dotnet", "build WoadRaiders.Client/WoadRaiders.Client.csproj -v q --nologo",
    "building the client (the design and scene tools are C# in it)");
Step("godot-mono", "--headless --path WoadRaiders.Client -s res://tools/build_realm_scene.gd -- " +
    "res://maps/Crag.tscn", "building Crag.tscn from the design with Godot's own serializer");

NormalizeSceneIds(scenePath);

// ------------------------------------------------------------- bake the geometry

Step("godot-mono", "--headless --path WoadRaiders.Client -s res://tools/bake_realm.gd -- " +
    "res://maps/Crag.tscn res://maps/Crag.json", "baking the served geometry FROM the scene");

// ------------------------------------------------------------- validate what will be served

var geometry = DungeonGeometryFile.Load(jsonPath);

// The shared realm checks (Core.RealmValidator): comfortable reachability of
// every camp and the boss, sealed borders, no stranding pits.
var issues = RealmValidator.Validate(geometry);
if (issues.Count > 0)
    throw new InvalidOperationException("the baked realm fails validation:\n  - " + string.Join("\n  - ", issues));

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

var terrain = geometry.Terrain!;
var span = geometry.Bounds;
Console.WriteLine($"Wrote Crag.tscn (the design) and Crag.json (baked from it, validated): " +
                  $"{terrain.Width}x{terrain.Depth} terrain over {(terrain.Width - 1) * terrain.CellSize:0}x" +
                  $"{(terrain.Depth - 1) * terrain.CellSize:0} units (heights {span.Min.Y:0}..{span.Max.Y:0}), " +
                  $"{geometry.Solids.Count} solids, {geometry.Props.Count} braziers, " +
                  $"{geometry.EnemySpawns.Count} enemy camps + the boss.");

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
        if (line.Contains("built ") || line.Contains("baked ") || line.Contains("sampled ") || line.Contains("scattered "))
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

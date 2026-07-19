// Generates a designed realm — The Crag is the first. The SCENE comes first,
// and the served geometry is baked from it:
//
//   1. A realm DESIGN (any IRealmDesign in WoadRaiders.Client/scripts/tools —
//      CragDesign.cs is the first) composes the realm, and RealmSceneBuilder
//      runs it to build the full Godot scene — with the WHOLE engine
//      available: any meshes, materials, particles, or asset kits. The scene
//      is saved by Godot's own ResourceSaver, so the .tscn is exactly what a
//      naturally-authored scene looks like: built-in nodes only, a real
//      displaced terrain mesh, free-form scenery (the boulder fields), no
//      scripts, no metadata.
//   2. The STANDARD hand-made pipeline (tools/bake_realm.gd) then bakes the
//      .json out of that scene — sampling the real terrain mesh, reading the
//      collision boxes and markers — the same way any
//      Blender-sculpted realm becomes a hostable map. The JSON is derived
//      output; nothing is ever generated FROM it.
//
// This tool orchestrates and validates the chain. A .NET 10 file-based app
// (godot-mono and the client build are driven automatically):
//
//   dotnet run tools/GenerateRealm.cs [realm]      # default: Crag
//
// To add a realm: write an IRealmDesign, list it in RealmDesigns, and (if it
// has an intended path worth proving traversable) add a route below.
//
// Validation runs the REAL simulation rules against the BAKED geometry — the
// very bytes the server will host:
//   - a virtual raider walks the whole route with DungeonGeometry.Move at
//     player speed and must reach the boss court (and end up high);
//   - Core.RealmValidator proves every camp comfortably reachable, the borders
//     sealed even against slope-inching, and no reachable spot stranded.
//
// To reshape a realm, edit its design (and its route below if the path itself
// moves), then rerun this tool. Hand-edits to the .tscn are equally legitimate
// — re-run the bake + ValidateRealm afterwards, like any hand-made realm.

#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using WoadRaiders.Core;

var realm = args.Length > 0 ? args[0] : "Crag";
var scenePath = Path.Combine("WoadRaiders.Client", "maps", $"{realm}.tscn");
var jsonPath = Path.Combine("WoadRaiders.Client", "maps", $"{realm}.json");

// ------------------------------------------------------------- build the scene

Step("dotnet", "build WoadRaiders.Client/WoadRaiders.Client.csproj -v q --nologo",
    "building the client (the designs and scene tools are C# in it)");
// Designs may instance imported asset kits (res://assets/**.glb/.gltf); those
// only load once Godot's editor import has produced its cache. Tolerant of the
// exit code: --import has a history of nonzero-on-success (godot#83449), and a
// genuinely missing import fails the scene build right after, loudly.
TolerantStep("godot-mono", "--headless --path WoadRaiders.Client --import",
    "importing assets (the kits a design may instance)");
Step("godot-mono", "--headless --path WoadRaiders.Client -s res://tools/build_realm_scene.gd -- " +
    $"res://maps/{realm}.tscn {realm}", $"building {realm}.tscn from its design with Godot's own serializer");

NormalizeSceneIds(scenePath);

// ------------------------------------------------------------- bake the geometry

Step("godot-mono", "--headless --path WoadRaiders.Client -s res://tools/bake_realm.gd -- " +
    $"res://maps/{realm}.tscn res://maps/{realm}.json", "baking the served geometry FROM the scene");

// ------------------------------------------------------------- validate what will be served

var geometry = DungeonGeometryFile.Load(jsonPath);

// The shared realm checks (Core.RealmValidator): comfortable reachability of
// every camp and the boss, sealed borders, no stranding pits.
var issues = RealmValidator.Validate(geometry);
if (issues.Count > 0)
    throw new InvalidOperationException("the baked realm fails validation:\n  - " + string.Join("\n  - ", issues));

// The route walk: a virtual raider walks the whole intended path with the REAL
// Move rules at player speed. Stalls fail the build, and the walker must END in
// the realm's final height band — the Crag's climb must gain height, the
// Crypt's descent must lose it. Realms with no route here skip the walk —
// RealmValidator's reachability proof stands on its own, so a new realm owes
// this only if it has an intended path.
var routes = new Dictionary<string, (Vector2[] Path, float MinFinalHeight, float MaxFinalHeight)>(StringComparer.OrdinalIgnoreCase)
{
    ["Crag"] = ([
    new(700, 700), new(1000, 700), new(1600, 800), new(2200, 900), new(2450, 1000),
    new(2650, 1050), new(3000, 1100), new(3340, 1100), new(3620, 1100), new(3700, 1100),
    new(4100, 1170), new(4600, 1250), new(4750, 1350), new(4300, 1500), new(3900, 1650),
    new(3750, 1750), new(3800, 2000), new(3900, 2500), new(4300, 2700), new(4800, 2900),
    new(4800, 3200), new(5300, 3700), new(5350, 3850), new(5000, 4100), new(4700, 4300),
    new(4560, 4300), new(4460, 4340), new(4460, 4450), new(4200, 4750), new(4200, 4820),
    ], 250f, float.MaxValue),

    // The Crypt: undercroft → stair → hub → processional → the span → the maze
    // → deep stair → antechamber → the Mausoleum. Ends DEEP or the descent is
    // broken.
    ["Crypt"] = ([
    new(480, 1800), new(760, 1800), new(1180, 1800), new(1450, 1800),
    new(1750, 1800), new(2100, 1800), new(2380, 1800), new(2650, 1800),
    new(2820, 1800), new(3060, 1800), new(3360, 1800), new(3660, 1800),
    new(3660, 2160), new(3660, 2500), new(3660, 2760), new(3260, 2980),
    new(3000, 3060), new(2760, 3140), new(2620, 3180), new(2320, 3260),
    ], float.MinValue, -230f),
};
if (!routes.TryGetValue(realm, out var route))
{
    Console.WriteLine($"No route configured for {realm} — skipping the walk; RealmValidator's reachability proof stands.");
}
else
{
    var pos = geometry.SpawnPoint;
    var step = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
    foreach (var target in route.Path)
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
    if (pos.Y < route.MinFinalHeight)
        throw new InvalidOperationException($"the route walker ended at height {pos.Y:0}, below the expected " +
                                            $"{route.MinFinalHeight:0} — the climb is broken");
    if (pos.Y > route.MaxFinalHeight)
        throw new InvalidOperationException($"the route walker ended at height {pos.Y:0}, above the expected " +
                                            $"{route.MaxFinalHeight:0} — the descent is broken");
    Console.WriteLine($"Route walk OK — the raider stands at ({pos.X:0}, {pos.Y:0}, {pos.Z:0}) before the boss.");
}

var span = geometry.Bounds;
var terrainSummary = geometry.Terrain is { } terrain
    ? $"{terrain.Width}x{terrain.Depth} terrain over {(terrain.Width - 1) * terrain.CellSize:0}x" +
      $"{(terrain.Depth - 1) * terrain.CellSize:0} units (heights {span.Min.Y:0}..{span.Max.Y:0})"
    : "no terrain";
Console.WriteLine($"Wrote {realm}.tscn (the design) and {realm}.json (baked from it, validated): " +
                  $"{terrainSummary}, {geometry.Solids.Count} solids, " +
                  $"{geometry.EnemySpawns.Count} enemy camps" +
                  $"{(geometry.BossSpawn is not null ? " + the boss" : "")}.");

// ------------------------------------------------------------- helpers

static void TolerantStep(string exe, string args, string what)
{
    try
    {
        Step(exe, args, what);
    }
    catch (InvalidOperationException e)
    {
        Console.WriteLine($"  (tolerated: {e.Message.Split('\n')[0]})");
    }
}

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
        if (line.Contains("built ") || line.Contains("baked ") || line.Contains("sampled ")
            || line.Contains("scattered ") || line.Contains("warning:"))
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

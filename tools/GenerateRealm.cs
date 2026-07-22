// Generates a designed realm — The Crag is the first. The SCENE comes first,
// and the served geometry is baked from it:
//
//   1. A realm DESIGN (any IRealmDesign in WoadRaiders.Client/scripts/tools —
//      CragDesign.cs is the first) composes the realm, and RealmSceneBuilder
//      runs it to build the full Godot scene — with the WHOLE engine
//      available: any meshes, materials, particles, or asset kits. The scene
//      is saved by Godot's own ResourceSaver, so the .tscn is exactly what a
//      naturally-authored scene looks like: built-in nodes only, cut stone,
//      free-form scenery (the boulder fields), no scripts, no metadata.
//   2. The STANDARD hand-made pipeline (tools/bake_realm.gd) then bakes the
//      .json out of that scene — taking the triangles of every mesh the realm
//      is modelled from, plus the spawn markers — the same way any
//      hand-built realm becomes a hostable map. The JSON is derived output;
//      nothing is ever generated FROM it.
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
//   - a virtual raider walks the whole route with RealmGeometry.Move at
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

var definition = RealmDefinitionFile.Load(jsonPath);

// ------------------------------------------------------------- bake the navmesh
// The navmesh is a BUILD ARTIFACT, not something either end works out at run
// time. That is what lets a client play its own copy of a realm instead of
// waiting for the server to send one: identical bytes because they are the SAME
// bytes, rather than because two machines were trusted to bake alike. It also
// spares the server a Recast bake per map at startup.
//
// Character width only. The boss moves on a wider mesh, but only the SERVER
// moves the boss, so that one stays server-side and is baked there.
if (definition.Soup is { } navSoup)
{
    var navPath = Path.ChangeExtension(jsonPath, ".navmesh");
    File.WriteAllBytes(navPath, NavMeshBuilder.Serialize(NavMeshBuilder.BuildMeshData(navSoup)));
    Console.WriteLine($"  baked {Path.GetFileName(navPath)}: {new FileInfo(navPath).Length / 1024} KB");
}

// The shared realm checks (Core.RealmValidator): comfortable reachability of
// every camp and the boss, sealed borders, no stranding pits.
var issues = RealmValidator.Validate(definition);
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
    // The Crag: gate court → stairs → the processional → stairs → the high
    // ward → the causeway → the boss court and its dais. Ends HIGH or the
    // ascent is broken.
    ["Crag"] = ([
    new(700, 2000), new(1150, 2000), new(1600, 2000), new(2100, 2000),
    new(2650, 2000), new(3100, 2000), new(3400, 2000), new(3400, 2600),
    new(3400, 3000), new(3400, 3400), new(3400, 3650),
    ], 250f, float.MaxValue),

    // The Crypt v2 (docs/realms/crypt.md): the Broken Porch → the Minster
    // nave → the Stair of the Dead → the Ossuary → the Cut → the Fault,
    // revealed from the shelf → down the west stair and across the span → the
    // east landing → the deep stair → the Deep Gallery → the Descent → the
    // Forecourt → the Passage → the Chamber of the Wheel. Ends DEEP or the
    // descent is broken.
    //
    // Stated in WORLD units, because v2 is authored in them: v1 drew its plan
    // small and multiplied by 3.16, which meant this route had to know the
    // scale factor and would silently point at where the realm used to be if
    // the two ever drifted.
    ["Crypt"] = ([
    new(200, 2200), new(760, 2200), new(1200, 2200), new(2000, 2200),
    new(2900, 2200), new(3320, 2200), new(3700, 2200), new(4400, 2180),
    new(5200, 2160), new(5400, 2160), new(5640, 2120),
    new(5600, 1800), new(5600, 1500), new(5760, 1280),
    new(6200, 1280), new(6800, 1280), new(7120, 1280),
    new(7120, 1700), new(7120, 2200), new(7120, 2700), new(7100, 3100),
    new(6400, 3200), new(5400, 3200), new(5000, 3200),
    new(4840, 3260), new(4600, 3280), new(4300, 3280),
    new(3900, 3280), new(3600, 3280), new(3200, 3280), new(2900, 3300),
    ], float.MinValue, -860f),
};
if (!routes.TryGetValue(realm, out var route))
{
    Console.WriteLine($"No route configured for {realm} — skipping the walk; RealmValidator's reachability proof stands.");
}
else
{
    // The walk runs on the very movement geometry the SERVER will bake and
    // move on — the realm's navmesh over its baked soup.
    if (definition.Soup is not { } soup)
        throw new InvalidOperationException("the baked realm has no geometry soup — nothing to walk on");
    var movement = new RealmGeometry(NavMeshBuilder.Build(soup), soup, definition.SpawnPoint);
    var pos = definition.SpawnPoint;
    var step = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
    foreach (var target in route.Path)
    {
        var stall = 0;
        var guard = 0;
        while (Vector2.Distance(new Vector2(pos.X, pos.Z), target) > 30f)
        {
            var dir = Vector2.Normalize(target - new Vector2(pos.X, pos.Z));
            var next = movement.Move(pos, new Vector3(dir.X, 0f, dir.Y) * step);
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

var span = definition.Bounds;
var soupSummary = definition.Soup is { } builtSoup
    ? $"{builtSoup.Triangles.Length / 3} triangles over " +
      $"{span.Max.X - span.Min.X:0}x{span.Max.Z - span.Min.Z:0} units (heights {span.Min.Y:0}..{span.Max.Y:0})"
    : "no geometry";
Console.WriteLine($"Wrote {realm}.tscn (the design) and {realm}.json (baked from it, validated): " +
                  $"{soupSummary}, {definition.EnemySpawns.Count} enemy camps" +
                  $"{(definition.BossSpawn is not null ? " + the boss" : "")}.");

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
        // "no_collide" is here deliberately: the bake counts aloud what it was
        // told to wave through, and a realm quietly excusing more and more of
        // itself from collision is invisible unless that count reaches a human.
        // Swallowing it here would have silenced the only symptom there is.
        if (line.Contains("built ") || line.Contains("baked ") || line.Contains("sampled ")
            || line.Contains("no_collide") || line.Contains("scattered ") || line.Contains("warning:"))
            Console.WriteLine("  " + line.Trim());
}

// ResourceSaver invents random identifiers on every save — sub-resource ids
// (e.g. "Image_kg5hl"), per-node unique_id stamps, and the scene uid. Rename
// the resource ids deterministically (Type_1, Type_2, ...) and drop the
// random-per-save attributes (both optional — the editor regenerates them on
// its next save), so regenerating an unchanged realm rewrites nothing. Ids
// are opaque references — any Godot reads the result identically.
//
// EXT-resource ids are normalized too, and the reason is worth stating because
// it looks unnecessary: an ext_resource pointing at an IMPORTED asset already
// gets a stable id, derived from the uid its .import file pins. One pointing at
// a resource the build itself wrote — a realm's sculpted mesh library — has no
// such uid, so Godot mints a fresh random suffix on every save and the scene
// stops regenerating identically. Measured on 4.7 with
// WoadRaiders.Client/tools/probe_extresource.gd. Normalizing both kinds means
// the guarantee rests on this function rather than on which flavour of resource
// a realm happens to reference.
static void NormalizeSceneIds(string path)
{
    var text = File.ReadAllText(path);
    text = Regex.Replace(text, @" uid=""uid://[^""]*""", "");
    text = Regex.Replace(text, @" unique_id=\d+", "");

    var counters = new Dictionary<string, int>();
    var renames = new Dictionary<string, string>();

    // Document order is the ordering, for both kinds: Godot writes these blocks
    // in a stable order, which is what makes the numbering reproducible.
    // Distinct prefixes keep the two namespaces from ever colliding.
    Rename(@"\[ext_resource type=""([^""]+)"" [^\]]*id=""([^""]+)""\]", "ext");
    Rename(@"\[sub_resource type=""([^""]+)"" id=""([^""]+)""\]", "");

    void Rename(string pattern, string kind)
    {
        foreach (Match match in Regex.Matches(text, pattern))
        {
            var type = match.Groups[1].Value;
            var key = kind + type;
            counters[key] = counters.GetValueOrDefault(key) + 1;
            renames[match.Groups[2].Value] = $"{type}_{kind}{counters[key]}";
        }
    }

    var builder = new StringBuilder(text);
    foreach (var (old, fresh) in renames)
        builder.Replace($"\"{old}\"", $"\"{fresh}\"");
    File.WriteAllText(path, builder.ToString());
}

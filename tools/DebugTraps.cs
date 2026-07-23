// Find geometry a player can get INTO but not back OUT of — a one-way pocket, in
// the sim's own terms and at the sim's own speed.
//
// Two earlier tries taught this one its shape:
//
//   1. DebugStuck.cs (deleted) sampled arbitrary XZ columns. Walkability is the
//      NAVMESH, though — off it, Move returns unchanged and GroundHeight falls
//      back to 0f — so it flagged every wall-top and empty sky column and called
//      43% of the world a trap.
//
//   2. This file's first version flooded with the real Move but stepped 40 units
//      at a time. A single 40-unit Move near a walkable edge triggers a ledge
//      drop into the thin off-mesh gap the navmesh leaves against every wall
//      (eroded a body radius in), where Move then can't snap — a "trap" no
//      player can reach. Reproduced and disproved: 260 of 261 realistic falls
//      off the Fault span landed on navigable floor and walked out; ZERO froze.
//      The 40-unit step was measuring itself, not a player.
//
// So this walks every flood edge as REAL TICKS — PlayerMoveSpeed * TickDelta per
// step, the exact call GameWorld.Step makes — held for a fixed count, the way a
// player holds a direction. That walks to the navmesh edge and stops, or falls
// onto real floor, but never teleports into the erosion fringe. And "can you get
// back" is asked of the actual pathfinder (TryFindPath), which routes stairs and
// doorways properly — the return trip needs no drops, only walking and climbing.
//
// A trap is then exactly: a cell a player can reach by moving from spawn, from
// which the pathfinder finds no walk back to spawn.
//
//   dotnet run tools/DebugTraps.cs -c Release  [-- <map.json>]
#:project C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false
using System.Numerics;
using WoadRaiders.Core;

var path = args.Length > 0 ? args[0]
    : "C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Client/maps/Crypt.json";
var realm = RealmDefinitionFile.Load(path);
var soup = realm.Soup!;
var nav = NavMeshBuilder.BuildMeshData(soup);
var mv = new RealmGeometry(soup, realm.SpawnPoint, (SimConstants.CharacterRadius, NavMeshBuilder.ToNavMesh(nav)));
var spawn = realm.SpawnPoint;

var step = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;  // one tick's walk
const float Cell = 40f, YBand = 40f;
// Hold a direction long enough to clear a cell even diagonally (40 x √2 ≈ 57),
// so the flood advances; a wall or edge stops the sub-walk early on its own.
var ticksPerEdge = (int)MathF.Ceiling(Cell * 1.5f / step);

(int, int, int) Key(Vector3 p) =>
    ((int)MathF.Round(p.X / Cell), (int)MathF.Round(p.Z / Cell), (int)MathF.Round(p.Y / YBand));

// Hold `unit` for a spell of real ticks — Move resolves each against the mesh,
// so this walks to a wall/edge and stops, or drops onto floor, like a player.
Vector3 Walk(Vector3 from, Vector3 unit)
{
    var p = from;
    for (var t = 0; t < ticksPerEdge; t++)
    {
        var n = mv.Move(p, unit * step);
        if (Vector3.Distance(n, p) < 0.05f) break;
        p = n;
    }
    return p;
}

var units = new Vector3[8];
for (var d = 0; d < 8; d++)
{
    var a = MathF.Tau * d / 8f;
    units[d] = new Vector3(MathF.Cos(a), 0f, MathF.Sin(a));
}

if (Vector3.Distance(Walk(spawn, Vector3.UnitX), spawn) < 1f &&
    Vector3.Distance(Walk(spawn, Vector3.UnitZ), spawn) < 1f)
    Console.WriteLine("WARNING: the spawn itself cannot move — the flood will find nothing.");

// Forward flood from spawn, recording every edge — so "can you get back" is
// answered by the SAME sim moves, not a navmesh planner. TryFindPath cannot
// climb a stair Recast left off the mesh, but a realistic Walk can (TryStepUp
// per tread), and the pit's climb-out is exactly such a stair. Only the sim's
// own edges tell the truth about escaping it.
var rep = new Dictionary<(int, int, int), Vector3> { [Key(spawn)] = spawn };
var reverse = new Dictionary<(int, int, int), List<(int, int, int)>>();
var queue = new Queue<(int, int, int)>();
queue.Enqueue(Key(spawn));
const int Cap = 300_000;
while (queue.Count > 0)
{
    var cell = queue.Dequeue();
    var from = rep[cell];
    foreach (var unit in units)
    {
        var to = Walk(from, unit);
        if (Vector3.Distance(to, from) < Cell * 0.35f) continue; // clamped — a wall
        var k = Key(to);
        if (k == cell) continue;
        // From 'cell' you can step to 'k' — so 'cell' is a way INTO 'k'.
        (reverse.TryGetValue(k, out var preds) ? preds : reverse[k] = new()).Add(cell);
        if (rep.ContainsKey(k)) continue;
        rep[k] = to;
        if (rep.Count > Cap) { Console.WriteLine($"WARNING: hit the {Cap} cell cap."); queue.Clear(); break; }
        queue.Enqueue(k);
    }
}

// Which cells can get BACK: reverse-flood from spawn along the recorded edges.
// A cell reachable forward but not marked here is a genuine one-way pocket —
// and because the edges are realistic ticks, a climbable stair counts as a way
// out even when the navmesh does not connect it.
var escapes = new HashSet<(int, int, int)> { Key(spawn) };
var back = new Queue<(int, int, int)>();
back.Enqueue(Key(spawn));
while (back.Count > 0)
{
    if (reverse.TryGetValue(back.Dequeue(), out var preds))
        foreach (var p in preds)
            if (escapes.Add(p)) back.Enqueue(p);
}

// OPENNESS FILTER — the last false-positive source. The navmesh is eroded a
// body radius off every wall, leaving a thin off-mesh band against the stone. A
// flood that walks into a wall rests its representative point in that band,
// where movement is degenerate: it "can't get back" only because it can barely
// move at all. A real player never stops there — they slide along the wall. So a
// cell counts as a trap only if a player standing in it can move FREELY (most
// directions give real travel) yet still cannot return. That is a room you can
// enter and not leave; a wall sliver is not.
int Openness(Vector3 at)
{
    var n = 0;
    foreach (var unit in units)
        if (Vector3.Distance(mv.Move(at, unit * step), at) > step * 0.5f) n++;
    return n;
}

var slivers = 0;
var traps = new List<Vector3>();
foreach (var (k, p) in rep)
{
    if (escapes.Contains(k)) continue;
    if (Openness(p) >= 5) traps.Add(p);   // genuinely standable, yet one-way
    else slivers++;                        // wall-contact fringe — not a trap
}

Console.WriteLine($"\nreachable from spawn:  {rep.Count} cells (walking and falling)");
Console.WriteLine($"of those, can return:  {escapes.Count}");
Console.WriteLine($"wall-sliver fringe:     {slivers} (against a wall, ignored)");
Console.WriteLine($"ONE-WAY TRAPS:         {traps.Count}  (open floor, reachable, no way back)\n");

var clusters = new List<(Vector3 Centre, int Count)>();
foreach (var t in traps)
{
    var hit = false;
    for (var i = 0; i < clusters.Count; i++)
        if (Vector3.Distance(clusters[i].Centre, t) < 240f)
        {
            clusters[i] = (clusters[i].Centre, clusters[i].Count + 1);
            hit = true;
            break;
        }
    if (!hit) clusters.Add((t, 1));
}

if (clusters.Count == 0)
    Console.WriteLine("  none — every place a player can reach, they can walk back from.");
foreach (var c in clusters.OrderByDescending(c => c.Count).Take(30))
    Console.WriteLine($"  ({c.Centre.X,6:0}, {c.Centre.Y,6:0}, {c.Centre.Z,6:0})  {c.Count,4} cell(s)   {Where(c.Centre)}");

string Where(Vector3 p) => (p.X, p.Z, p.Y) switch
{
    ( < 800, _, > -80) => "the Broken Porch / nave mouth",
    ( < 3040, _, > -80) => "the Minster nave",
    ( >= 3600, < 2880, > -260) => "the Ossuary",
    ( >= 5680, < 2880, < -700) => "the Fault pit",
    ( >= 5520, < 2880, _) => "the Fault (shelf / span / landings)",
    ( >= 4960, > 2880, > -620) => "the Deep Gallery",
    ( < 4880, > 3400, _) => "the Cubiculum / creepway",
    ( >= 4160, < 4720, > -760) => "the Forecourt",
    ( < 3520, > 2880, _) => "the Chamber of the Wheel",
    _ => "a connector",
};

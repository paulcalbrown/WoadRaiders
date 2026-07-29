// How the chase camera RIDES, measured — not how it frames a posed still. The
// clip and bounce a player reports are dynamics: a reel that pops frame to
// frame, a ground clamp that jumps a wall top, a near plane inside a pillar
// the fader spared. So walk the realm's own routes at run speed, advance the
// Core ChaseCamera solver at 60 fps exactly as GameScreen does, and score
// every frame from OUTSIDE the solver:
//
//   clip risk — a cross of soup probes around the camera position at the
//               near-frustum corner radius (and a tighter "touching stone");
//   sight     — the eye-to-camera line the player actually looks down;
//   bounce    — frame-to-frame position deltas: percentiles, pops, and
//               direction reversals (oscillation, not travel).
//
//   dotnet run tools/MeasureCameraRide.cs -c Release
#:project C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false
using System.Numerics;
using WoadRaiders.Core;

const float FrameDt = 1f / 60f;
const float BodyHeight = 22f;      // GameScreen.BodyHeight: feet -> followed body centre
const float ClipRadius = 12f;      // the original Near=8 corner reach — stricter than today's
                                   // ~7.4 (Near 4, FOV 62), kept so runs stay comparable
const float TouchRadius = 3f;      // practically inside the stone
const float PopThreshold = 25f;    // units in one frame; 220 u/s of running is ~3.7
const float WaypointReach = 25f;

var realm = RealmDefinitionFile.Load("C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Client/maps/Crypt.json");
var soup = realm.Soup!;
var movement = new RealmGeometry(soup, realm.SpawnPoint,
    (SimConstants.CharacterRadius, NavMeshBuilder.ToNavMesh(NavMeshBuilder.BuildMeshData(soup))));

// ---- Routes: the farthest reachable stand from spawn, and a second one far
// from the first — between them they walk the spine of the realm.
var candidates = new List<(Vector3 At, float PathLength, List<Vector3> Way)>();
for (var x = soup.BoundsMin.X; x <= soup.BoundsMax.X; x += 48f)
for (var z = soup.BoundsMin.Z; z <= soup.BoundsMax.Z; z += 48f)
{
    var groundY = movement.GroundHeight(new Vector3(x, soup.BoundsMax.Y, z));
    if (groundY < soup.BoundsMin.Y - 1f)
        continue;
    var stand = new Vector3(x, groundY, z);
    var way = new List<Vector3>();
    if (!movement.TryFindPath(realm.SpawnPoint, stand, way) || way.Count == 0 ||
        Vector3.Distance(way[^1], stand) > 40f)
        continue;
    var length = 0f;
    for (var i = 1; i < way.Count; i++)
        length += Vector3.Distance(way[i - 1], way[i]);
    candidates.Add((stand, length, way));
}
var first = candidates.OrderByDescending(c => c.PathLength).First();
var second = candidates.Where(c => Vector3.Distance(c.At, first.At) > 1500f)
                       .OrderByDescending(c => c.PathLength).First();
Console.WriteLine($"routes: {candidates.Count} reachable stands; " +
                  $"deepest {first.PathLength:0} to ({first.At.X:0},{first.At.Y:0},{first.At.Z:0}), " +
                  $"second {second.PathLength:0} to ({second.At.X:0},{second.At.Y:0},{second.At.Z:0})\n");

Score("route: spawn -> deepest", WalkRoute(first.Way));
Score("route: spawn -> second", WalkRoute(second.Way));
Score("circle: mid-route chamber", Circle(first.Way[first.Way.Count / 2], seconds: 8f));

// ---- The mover: 30 Hz ticks along the waypoints via the real Move (stairs,
// slides and refusals all come from the bake), with a stall read as a link
// crossing and emulated at the sim's fall speed. Yields feet at every tick.
IEnumerable<Vector3> WalkRoute(List<Vector3> way)
{
    var pos = way[0];
    var next = 1;
    var stalled = 0;
    var falling = false;
    for (var tick = 0; tick < 60 * 30 && next < way.Count; tick++)
    {
        var to = way[next] - pos;
        if (new Vector3(to.X, 0f, to.Z).Length() < WaypointReach && MathF.Abs(to.Y) < 60f ||
            to.Length() < WaypointReach)
        {
            next++;
            falling = false;
            continue;
        }
        if (falling)
        {
            // A crossing the surface walk cannot make: ride toward the far end
            // at the sim's LinkFallSpeed, the way a LinkTraversal arc would.
            pos += Vector3.Normalize(to) * MathF.Min(SimConstants.LinkFallSpeed * SimConstants.TickDelta, to.Length());
            yield return pos;
            continue;
        }
        var dir = Vector3.Normalize(new Vector3(to.X, 0f, to.Z));
        var moved = movement.Move(pos, dir * (SimConstants.PlayerMoveSpeed * SimConstants.TickDelta));
        stalled = Vector3.Distance(moved, pos) < 1f ? stalled + 1 : 0;
        pos = moved;
        if (stalled >= 10)
        {
            falling = true;
            stalled = 0;
        }
        yield return pos;
    }
}

// Circling a fixed point — the strafe-around-an-enemy shot, where the yaw
// swing and the fit hunt hardest for somewhere to stand.
IEnumerable<Vector3> Circle(Vector3 centre, float seconds)
{
    var pos = movement.Move(centre, Vector3.Zero);
    for (var tick = 0f; tick < seconds * SimConstants.TickRate; tick++)
    {
        var radial = new Vector3(pos.X - centre.X, 0f, pos.Z - centre.Z);
        if (radial.Length() < 40f)
            radial = new Vector3(1f, 0f, 0f);
        var tangent = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, radial));
        var inward = radial.Length() > 90f ? -Vector3.Normalize(radial) * 0.4f : Vector3.Zero;
        pos = movement.Move(pos, Vector3.Normalize(tangent + inward) * (SimConstants.PlayerMoveSpeed * SimConstants.TickDelta));
        yield return pos;
    }
}

// ---- Drive the solver exactly as GameScreen does — body centre at 60 fps,
// two render frames per tick, interpolated like LocalPlayer's render position —
// and score what a player would see.
void Score(string name, IEnumerable<Vector3> feetPerTick)
{
    var camera = new ChaseCamera { Geometry = movement };
    var deltas = new List<float>();
    int frames = 0, pops = 0, blocked = 0, clip = 0, touch = 0, reversals = 0, clipFree = 0;
    float maxDelta = 0f, maxRise = 0f;
    Vector3 prevPos = default, prevStep = default;
    var started = false;
    Vector3? prevFeet = null;

    foreach (var feet in feetPerTick)
    {
        var from = prevFeet ?? feet;
        prevFeet = feet;
        for (var sub = 1; sub <= 2; sub++)
        {
            var body = Vector3.Lerp(from, feet, sub / 2f) + Vector3.UnitY * BodyHeight;
            var frame = camera.Follow(body, FrameDt);
            frames++;

            var boom = frame.Position - frame.LookTarget;
            var flank = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, boom is { X: 0f, Z: 0f } ? Vector3.UnitX : boom));
            var axes = new[] { Vector3.Normalize(boom), flank, Vector3.UnitY };
            if (axes.Any(a => soup.SegmentHits(frame.Position - a * ClipRadius, frame.Position + a * ClipRadius)))
            {
                clip++;
                if (boom.Length() > 125f) // not the pinned-at-min-boom concession: a real hole
                    clipFree++;
            }
            if (axes.Any(a => soup.SegmentHits(frame.Position - a * TouchRadius, frame.Position + a * TouchRadius)))
                touch++;
            if (!movement.HasLineOfSight(frame.LookTarget, frame.Position))
                blocked++;

            if (started)
            {
                var step = frame.Position - prevPos;
                var d = step.Length();
                deltas.Add(d);
                maxDelta = MathF.Max(maxDelta, d);
                maxRise = MathF.Max(maxRise, MathF.Abs(step.Y));
                if (d > PopThreshold)
                {
                    pops++;
                    if (d > 60f) // the teleport-scale ones: say where and from what
                        Console.WriteLine($"  POP {d,6:0} @f{frames}: cam ({prevPos.X:0},{prevPos.Y:0},{prevPos.Z:0}) -> " +
                                          $"({frame.Position.X:0},{frame.Position.Y:0},{frame.Position.Z:0})  " +
                                          $"boom {Vector3.Distance(prevPos, frame.LookTarget):0}->{boom.Length():0}  " +
                                          $"eye ({frame.LookTarget.X:0},{frame.LookTarget.Y:0},{frame.LookTarget.Z:0})");
                }
                if (d > 2f && prevStep.Length() > 2f && Vector3.Dot(step, prevStep) < 0f)
                    reversals++;
                prevStep = step;
            }
            prevPos = frame.Position;
            started = true;
        }
    }

    deltas.Sort();
    float At(double p) => deltas.Count == 0 ? 0f : deltas[Math.Min(deltas.Count - 1, (int)(p * deltas.Count))];
    Console.WriteLine($"{name}: {frames} frames");
    Console.WriteLine($"  bounce: median {At(0.5):0.0}  p95 {At(0.95):0.0}  p99 {At(0.99):0.0}  max {maxDelta:0.0}  " +
                      $"pops(>{PopThreshold:0}) {pops}  reversals {reversals}  max vertical step {maxRise:0.0}");
    Console.WriteLine($"  clip:   near-plane risk {Pct(clip)} (unpinned {clipFree})  touching stone {Pct(touch)}  sight line blocked {Pct(blocked)}\n");
    string Pct(int n) => $"{n} ({100.0 * n / Math.Max(1, frames):0.0}%)";
}

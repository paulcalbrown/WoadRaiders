using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace WoadRaiders.Core;

/// <summary>
/// Playability checks for a built realm, judged on the very navmesh the server
/// will move on — shared by the realm generator and the hand-made-map pipeline
/// (tools/ValidateRealm.cs), so every realm passes the same bar before it is
/// served:
///
///   - the boss and every enemy camp are reachable from the spawn (a complete
///     planned route, drops and boardings included — not a partial best-effort);
///   - nowhere the spawn can reach is stranded: from EVERY polygon the spawn
///     can reach — walks and link crossings alike — the boss can still be
///     reached (falls are detours, never graves). This is a flood-fill PROOF
///     over the polygon-and-link graph, not a sampling. Movement is
///     navmesh-only, so that graph is exactly the set of places a mover can
///     ever stand, and a trap too small for any sample grid cannot hide in it;
///   - every DECLARED flight (<see cref="RealmDefinition.Stairs"/>) walks both
///     ways on plain mesh: no stall, and no baked-link crossing. Regular
///     traversal must never ride links, and no reachability proof can see a
///     stair that only descends — a one-way flight strands nobody, which is
///     exactly how the Fault's east flight shipped sealed behind a parapet.
///     Only a climb test catches that, so every stair now carries one.
///
/// Sealed borders come free in a built realm: beyond the soup there is no
/// ground at all, so movement simply refuses the void — there is no infinite
/// plane left to inch across.
/// </summary>
public static class RealmValidator
{
    /// <summary>How close (XZ) a route's end must land to its goal to count as arrival.</summary>
    public const float ArrivalTolerance = 40f;

    /// <summary>FindNearestPoly box anchoring the spawn and boss onto the mesh.</summary>
    private static readonly RcVec3f AnchorExtents = new(24f, 48f, 24f);

    /// <summary>
    /// Validate a realm. Returns problems found (empty = the realm is sound).
    /// A map without geometry is not a realm and fails outright.
    /// </summary>
    public static IReadOnlyList<string> Validate(RealmDefinition realm)
    {
        var issues = new List<string>();
        if (realm.Soup is not { } soup)
        {
            issues.Add("the map has no geometry soup — RealmValidator checks built realms");
            return issues;
        }
        if (realm.BossSpawn is not { } boss)
        {
            issues.Add("the realm has no boss — a realm exists to be raided");
            return issues;
        }

        var mesh = NavMeshBuilder.Build(soup);
        var nav = new RealmGeometry(mesh, soup, realm.SpawnPoint);

        if (!Reaches(nav, realm.SpawnPoint, boss))
            issues.Add($"the boss court at ({boss.X:0},{boss.Z:0}) is not reachable from the spawn");
        foreach (var camp in realm.EnemySpawns)
            if (!Reaches(nav, realm.SpawnPoint, camp.Position))
                issues.Add($"the {camp.Type} camp at ({camp.Position.X:0},{camp.Position.Z:0}) " +
                           "is not reachable from the spawn");

        // The stranding proof: flood the runtime polygon graph — walkable
        // adjacency plus the baked links, which carry their one-way-ness —
        // forward from the spawn, and backward from the boss over the
        // reversed graph. Every polygon the spawn can reach must sit in both
        // sets; each connected clump that cannot reach the boss is reported
        // once, by its centre. Polygons the spawn cannot reach are not the
        // realm's problem (a roof is scenery, not a trap).
        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();
        if (TryAnchor(query, filter, realm.SpawnPoint, out var spawnRef) &&
            TryAnchor(query, filter, boss, out var bossRef))
        {
            var (forward, reverse, centres) = BuildLinkGraph(mesh);
            var stranded = Flood(forward, spawnRef);
            stranded.ExceptWith(Flood(reverse, bossRef));
            stranded.RemoveWhere(r => !centres.ContainsKey(r)); // link polys are edges, not places
            foreach (var (centre, size) in Clumps(stranded, forward, reverse, centres))
                issues.Add($"({centre.X:0},{centre.Z:0}) is reachable but stranded — " +
                           $"the boss cannot be reached from it ({size} polygons)");
        }

        for (var i = 0; i < realm.Stairs.Count; i++)
        {
            WalkFlight(nav, realm.Stairs[i].Foot, realm.Stairs[i].Head, $"flight {i} climbing", issues);
            WalkFlight(nav, realm.Stairs[i].Head, realm.Stairs[i].Foot, $"flight {i} descending", issues);
        }

        return issues;
    }

    /// <summary>
    /// Walk a declared flight end to end through the REAL simulation — the
    /// same GameWorld a raider moves in, unit push toward the goal — and
    /// report a flight that boards a baked link (regular traversal must be
    /// mesh-walkable; a crossing here means the geometry failed to join and
    /// the bake healed it with a fall) or stalls short (a sealed mouth).
    /// </summary>
    private static void WalkFlight(RealmGeometry nav, Vector3 start, Vector3 goal, string what, List<string> issues)
    {
        var world = new GameWorld { Geometry = nav };
        var walker = world.AddPlayer(1, "flight-walker");
        walker.Position = start with { Y = nav.GroundHeight(start + new Vector3(0f, 30f, 0f)) };

        var dx = goal.X - start.X;
        var dz = goal.Z - start.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        var budget = (int)(4f * distance / (SimConstants.PlayerMoveSpeed * SimConstants.TickDelta)) + 90;
        uint seq = 0;
        for (var t = 0; t < budget; t++)
        {
            var toGoal = new Vector3(goal.X - walker.Position.X, 0f, goal.Z - walker.Position.Z);
            // Press all the way in before judging: at ArrivalTolerance a
            // shallow flight is legitimately still a body-height up its
            // own grade, and stopping there would misread it as a stall.
            if (toGoal.Length() <= 10f)
                break;
            var dir = Vector3.Normalize(toGoal);
            world.SetInput(1, new PlayerInput { MoveX = dir.X, MoveZ = dir.Z, Sequence = ++seq });
            world.Step();
            if (walker.Link.Active)
            {
                issues.Add($"the {what} run rides a baked link at " +
                           $"({walker.Link.From.X:0},{walker.Link.From.Y:0},{walker.Link.From.Z:0}) — " +
                           "regular traversal must be mesh-walkable; the geometry fails to join there");
                return;
            }
        }

        var endDx = walker.Position.X - goal.X;
        var endDz = walker.Position.Z - goal.Z;
        if (endDx * endDx + endDz * endDz > ArrivalTolerance * ArrivalTolerance ||
            MathF.Abs(walker.Position.Y - goal.Y) > 30f)
            issues.Add($"the {what} run stalls at " +
                       $"({walker.Position.X:0},{walker.Position.Y:0},{walker.Position.Z:0}) — " +
                       $"it never arrives at ({goal.X:0},{goal.Y:0},{goal.Z:0})");
    }

    /// <summary>A complete route exists — the planner's last waypoint arrives at the goal.</summary>
    private static bool Reaches(RealmGeometry nav, Vector3 from, Vector3 to)
    {
        var waypoints = new List<Vector3>();
        if (!nav.TryFindPath(from, to, waypoints) || waypoints.Count == 0)
            return false;
        var end = waypoints[^1];
        var dx = end.X - to.X;
        var dz = end.Z - to.Z;
        return dx * dx + dz * dz <= ArrivalTolerance * ArrivalTolerance;
    }

    private static bool TryAnchor(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 at, out long polyRef)
    {
        var status = query.FindNearestPoly(new RcVec3f(at.X, at.Y, at.Z), AnchorExtents, filter,
                                           out polyRef, out _, out _);
        return status.Succeeded() && polyRef != 0;
    }

    /// <summary>
    /// The runtime polygon graph, read straight off the mesh's link chains so
    /// it is exactly what MoveAlongSurface and the path planner traverse:
    /// shared edges both ways, off-mesh links only the way they were baked.
    /// Ground polys (not the link stubs) also get a centroid, for reporting.
    /// </summary>
    private static (Dictionary<long, List<long>> forward, Dictionary<long, List<long>> reverse,
                    Dictionary<long, Vector3> centres) BuildLinkGraph(DtNavMesh mesh)
    {
        var forward = new Dictionary<long, List<long>>();
        var reverse = new Dictionary<long, List<long>>();
        var centres = new Dictionary<long, Vector3>();
        for (var t = 0; t < mesh.GetMaxTiles(); t++)
        {
            var tile = mesh.GetTile(t);
            if (tile?.data?.header is null)
                continue;
            // GetTileRef IS the tile's poly-ref base (poly index 0); OR-ing in
            // the poly index yields the same refs the link chains carry.
            var baseRef = mesh.GetTileRef(tile);
            for (var p = 0; p < tile.data.header.polyCount; p++)
            {
                var poly = tile.data.polys[p];
                var from = baseRef | (long)p;

                if (poly.GetPolyType() == 0)
                {
                    var centre = Vector3.Zero;
                    for (var k = 0; k < poly.vertCount; k++)
                        centre += new Vector3(tile.data.verts[poly.verts[k] * 3],
                                              tile.data.verts[poly.verts[k] * 3 + 1],
                                              tile.data.verts[poly.verts[k] * 3 + 2]);
                    centres[from] = centre / poly.vertCount;
                }

                for (var i = poly.firstLink; i != DtDetour.DT_NULL_LINK; i = tile.links[i].next)
                {
                    var to = tile.links[i].refs;
                    if (to == 0)
                        continue;
                    Add(forward, from, to);
                    Add(reverse, to, from);
                }
            }
        }
        return (forward, reverse, centres);

        static void Add(Dictionary<long, List<long>> graph, long from, long to)
        {
            if (!graph.TryGetValue(from, out var list))
                graph[from] = list = new List<long>();
            list.Add(to);
        }
    }

    private static HashSet<long> Flood(Dictionary<long, List<long>> edges, long start)
    {
        var seen = new HashSet<long> { start };
        var frontier = new Stack<long>();
        frontier.Push(start);
        while (frontier.Count > 0)
            if (edges.TryGetValue(frontier.Pop(), out var next))
                foreach (var r in next)
                    if (seen.Add(r))
                        frontier.Push(r);
        return seen;
    }

    /// <summary>
    /// Group stranded polygons into connected clumps (adjacency in either
    /// direction) so a sunken yard reads as ONE finding with a centre and a
    /// size, not a report per polygon.
    /// </summary>
    private static IEnumerable<(Vector3 centre, int size)> Clumps(
        HashSet<long> stranded,
        Dictionary<long, List<long>> forward, Dictionary<long, List<long>> reverse,
        Dictionary<long, Vector3> centres)
    {
        var left = new HashSet<long>(stranded);
        while (left.Count > 0)
        {
            long seed = 0;
            foreach (var r in left) { seed = r; break; }
            left.Remove(seed);

            var clump = new List<long>();
            var frontier = new Stack<long>();
            frontier.Push(seed);
            while (frontier.Count > 0)
            {
                var at = frontier.Pop();
                clump.Add(at);
                if (forward.TryGetValue(at, out var outward))
                    foreach (var r in outward)
                        if (left.Remove(r))
                            frontier.Push(r);
                if (reverse.TryGetValue(at, out var inward))
                    foreach (var r in inward)
                        if (left.Remove(r))
                            frontier.Push(r);
            }

            var centre = Vector3.Zero;
            foreach (var r in clump)
                centre += centres[r];
            yield return (centre / clump.Count, clump.Count);
        }
    }
}

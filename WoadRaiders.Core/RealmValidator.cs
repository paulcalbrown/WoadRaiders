using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Playability checks for a built realm, judged on the very navmesh the server
/// will move on — shared by the realm generator and the hand-made-map pipeline
/// (tools/ValidateRealm.cs), so every realm passes the same bar before it is
/// served:
///
///   - the boss and every enemy camp are reachable from the spawn (a complete
///     planned route, drops and boardings included — not a partial best-effort);
///   - nowhere the spawn can reach is stranded: from every such spot the boss
///     can still be reached (falls are detours, never graves).
///
/// Sealed borders come free in a built realm: beyond the soup there is no
/// ground at all, so movement simply refuses the void — there is no infinite
/// plane left to inch across.
/// </summary>
public static class RealmValidator
{
    /// <summary>How close (XZ) a route's end must land to its goal to count as arrival.</summary>
    public const float ArrivalTolerance = 40f;

    /// <summary>XZ spacing of the stranding sweep's sample grid.</summary>
    private const float StrandingSampleSpacing = 200f;

    /// <summary>
    /// Validate a realm. Returns problems found (empty = the realm is sound).
    /// A map without geometry is not a realm and fails outright.
    /// </summary>
    public static IReadOnlyList<string> Validate(DungeonGeometry realm)
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

        var nav = new NavMeshGeometry(NavMeshBuilder.Build(soup), soup, realm.SpawnPoint);

        if (!Reaches(nav, realm.SpawnPoint, boss))
            issues.Add($"the boss court at ({boss.X:0},{boss.Z:0}) is not reachable from the spawn");
        foreach (var camp in realm.EnemySpawns)
            if (!Reaches(nav, realm.SpawnPoint, camp.Position))
                issues.Add($"the {camp.Type} camp at ({camp.Position.X:0},{camp.Position.Z:0}) " +
                           "is not reachable from the spawn");

        // The stranding sweep: everywhere the spawn can reach — drops included —
        // must still reach the boss. Spots the spawn CANNOT reach are not the
        // realm's problem (a roof is scenery, not a trap). Distinct landings
        // are reported once each, since many samples resolve to the same
        // ground.
        var landing = new List<Vector3>();
        var seen = new HashSet<(int, int)>();
        for (var x = soup.BoundsMin.X; x <= soup.BoundsMax.X; x += StrandingSampleSpacing)
            for (var z = soup.BoundsMin.Z; z <= soup.BoundsMax.Z; z += StrandingSampleSpacing)
            {
                if (soup.FloorHeightAt(x, z) is not { } y)
                    continue;

                // Judge where a raider would ACTUALLY end up walking at this
                // sample, not the sample itself. A chamber's floor slab runs on
                // underneath its own walls and pillars, and Recast rasterizes a
                // slab as a hollow shell — so the floor sealed inside masonry
                // survives as a walkable ISLAND, cut off from everything. Asked
                // about such a cell directly, the sweep reports a dead end that
                // no raider could ever be standing in. Pathing to it instead
                // leaves the walker on the nearest ground it can truly reach,
                // and if THAT cannot reach the boss, the trap is real.
                landing.Clear();
                if (!nav.TryFindPath(realm.SpawnPoint, new Vector3(x, y, z), landing) || landing.Count == 0)
                    continue;
                var stand = landing[^1];
                if (!Reaches(nav, stand, boss) &&
                    seen.Add(((int)(stand.X / StrandingSampleSpacing), (int)(stand.Z / StrandingSampleSpacing))))
                    issues.Add($"({stand.X:0},{stand.Z:0}) is reachable but stranded — " +
                               "the boss cannot be reached from it");
            }

        return issues;
    }

    /// <summary>A complete route exists — the planner's last waypoint arrives at the goal.</summary>
    private static bool Reaches(NavMeshGeometry nav, Vector3 from, Vector3 to)
    {
        var waypoints = new List<Vector3>();
        if (!nav.TryFindPath(from, to, waypoints) || waypoints.Count == 0)
            return false;
        var end = waypoints[^1];
        var dx = end.X - to.X;
        var dz = end.Z - to.Z;
        return dx * dx + dz * dz <= ArrivalTolerance * ArrivalTolerance;
    }
}

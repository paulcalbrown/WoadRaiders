using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Playability checks for an open realm (a terrain-bearing map), shared by the
/// realm generator and the hand-made-map pipeline (tools/ValidateRealm.cs), so
/// every realm — computed or authored in the Godot editor — passes the same
/// bar before it is served:
///
///   - the boss and every enemy camp are COMFORTABLY reachable from the spawn
///     (gentle grades, not cliff-inching);
///   - the borders are sealed even against a determined slope-incher;
///   - nowhere anyone can possibly get to is stranded — from every reachable
///     spot, the boss court can still be reached by comfortable walking plus
///     drops (falls are detours, never graves).
///
/// The model mirrors the simulation's movement rules at flood-fill granularity:
/// cell-to-cell ground candidates are the terrain sample plus any solid top at
/// that point (bridge decks), minus candidates a wall denies (parapets,
/// ramparts); rises are capped per cell, drops are always allowed.
/// </summary>
public static class RealmValidator
{
    /// <summary>
    /// The steepest grade (rise/run) counted as comfortable walking. Real
    /// players climb up to ~StepHeight per tick-step (~2.45); requiring routes
    /// to stay under this much gentler grade leaves a wide margin.
    /// </summary>
    public const float ComfortableClimbSlope = 1.125f;

    /// <summary>
    /// The steepest grade any mover could conceivably beat by inching up in
    /// minimal diagonal tick-steps (~3.47 at worst) — anything steeper is a
    /// hard wall for everyone. Borders must beat THIS grade to count as sealed.
    /// </summary>
    public const float InchingClimbSlope = 3.5f;

    /// <summary>
    /// Validate an open realm. Returns problems found (empty = the realm is
    /// sound). A map without terrain is not an open realm and fails outright.
    /// </summary>
    public static IReadOnlyList<string> Validate(DungeonGeometry realm)
    {
        var issues = new List<string>();
        if (realm.Terrain is not { } terrain)
        {
            issues.Add("the map has no terrain heightfield — RealmValidator checks open realms");
            return issues;
        }

        int w = terrain.Width, d = terrain.Depth;
        var cell = terrain.CellSize;
        var comfortable = ComfortableClimbSlope * cell;
        var inching = InchingClimbSlope * cell;

        // Per-cell candidate grounds: terrain sample + solid tops at that point,
        // minus any candidate a wall denies — the sim's walk rules at cell grain.
        var grounds = new List<float>[w * d];
        for (var j = 0; j < d; j++)
            for (var i = 0; i < w; i++)
                grounds[j * w + i] = GroundsAt(realm, terrain, i, j);

        (int i, int j) CellOf(Vector3 p) => (
            Math.Clamp((int)MathF.Round((p.X - terrain.OriginX) / cell), 0, w - 1),
            Math.Clamp((int)MathF.Round((p.Z - terrain.OriginZ) / cell), 0, d - 1));

        HashSet<(int i, int j, int l)> Flood((int i, int j) start, float climbLimit)
        {
            var seen = new HashSet<(int, int, int)>();
            var frontier = new Queue<(int i, int j, int l)>();
            for (var l = 0; l < grounds[start.j * w + start.i].Count; l++)
            {
                seen.Add((start.i, start.j, l));
                frontier.Enqueue((start.i, start.j, l));
            }
            while (frontier.Count > 0)
            {
                var (i, j, l) = frontier.Dequeue();
                var g = grounds[j * w + i][l];
                foreach (var (ni, nj) in new[] { (i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1) })
                {
                    if (ni < 0 || nj < 0 || ni >= w || nj >= d)
                        continue;
                    var candidates = grounds[nj * w + ni];
                    for (var nl = 0; nl < candidates.Count; nl++)
                        if (candidates[nl] - g <= climbLimit && seen.Add((ni, nj, nl)))
                            frontier.Enqueue((ni, nj, nl));
                }
            }
            return seen;
        }

        var spawnCell = CellOf(realm.SpawnPoint);
        var easy = Flood(spawnCell, comfortable);
        var anywhere = Flood(spawnCell, inching);

        bool EasyReach(Vector3 p)
        {
            var (i, j) = CellOf(p);
            for (var l = 0; l < grounds[j * w + i].Count; l++)
                if (easy.Contains((i, j, l)))
                    return true;
            return false;
        }

        // Every destination must be a comfortable walk from the spawn.
        if (realm.BossSpawn is { } boss && !EasyReach(boss))
            issues.Add("the boss is not comfortably reachable from the spawn");
        for (var m = 0; m < realm.EnemySpawns.Count; m++)
        {
            var marker = realm.EnemySpawns[m];
            if (!EasyReach(marker.Position))
                issues.Add($"the {marker.Type} camp at ({marker.Position.X:0}, {marker.Position.Z:0}) " +
                           "is not comfortably reachable from the spawn");
        }

        // Sealed borders: even an inching climber never reaches the outer two rings.
        var leaks = 0;
        (int, int) firstLeak = default;
        foreach (var (i, j, _) in anywhere)
        {
            if (i < 2 || j < 2 || i >= w - 2 || j >= d - 2)
            {
                if (leaks++ == 0)
                    firstLeak = (i, j);
            }
        }
        if (leaks > 0)
            issues.Add($"the realm leaks at its border ({leaks} reachable rim cells; first near " +
                       $"({terrain.OriginX + firstLeak.Item1 * cell:0}, {terrain.OriginZ + firstLeak.Item2 * cell:0}))");

        // No stranding: from every node anyone can possibly reach, the goal (the
        // boss court, or the spawn on a bossless map) must still be reachable by
        // comfortable walking — where any drop qualifies automatically.
        var goalCell = realm.BossSpawn is { } b ? CellOf(b) : spawnCell;
        var canReachGoal = new HashSet<(int, int, int)>();
        {
            var frontier = new Queue<(int i, int j, int l)>();
            for (var l = 0; l < grounds[goalCell.j * w + goalCell.i].Count; l++)
            {
                canReachGoal.Add((goalCell.i, goalCell.j, l));
                frontier.Enqueue((goalCell.i, goalCell.j, l));
            }
            while (frontier.Count > 0)
            {
                var (i, j, l) = frontier.Dequeue();
                var gv = grounds[j * w + i][l];
                foreach (var (ni, nj) in new[] { (i + 1, j), (i - 1, j), (i, j + 1), (i, j - 1) })
                {
                    if (ni < 0 || nj < 0 || ni >= w || nj >= d)
                        continue;
                    var candidates = grounds[nj * w + ni];
                    for (var nl = 0; nl < candidates.Count; nl++)
                        if (gv - candidates[nl] <= comfortable && canReachGoal.Add((ni, nj, nl)))
                            frontier.Enqueue((ni, nj, nl));
                }
            }
        }
        var stranded = 0;
        (int i, int j, int l) firstStranded = default;
        foreach (var node in anywhere)
        {
            if (!canReachGoal.Contains(node))
            {
                if (stranded++ == 0)
                    firstStranded = node;
            }
        }
        if (stranded > 0)
        {
            var g = grounds[firstStranded.j * w + firstStranded.i][firstStranded.l];
            issues.Add($"{stranded} reachable spots are stranding pits (first near " +
                       $"({terrain.OriginX + firstStranded.i * cell:0}, {terrain.OriginZ + firstStranded.j * cell:0}) " +
                       $"at height {g:0}) — anyone who drops in can never return");
        }

        return issues;
    }

    private static List<float> GroundsAt(DungeonGeometry realm, HeightField terrain, int i, int j)
    {
        var x = terrain.OriginX + i * terrain.CellSize;
        var z = terrain.OriginZ + j * terrain.CellSize;
        var list = new List<float> { terrain.At(i, j) };
        foreach (var s in realm.Solids)
            if (x >= s.Min.X && x <= s.Max.X && z >= s.Min.Z && z <= s.Max.Z && s.Max.Y > list[0])
                list.Add(s.Max.Y);
        list.RemoveAll(g => realm.Solids.Any(s =>
            x >= s.Min.X && x <= s.Max.X && z >= s.Min.Z && z <= s.Max.Z &&
            s.Min.Y < g + SimConstants.CharacterHeight - 1f && s.Max.Y > g + SimConstants.StepHeight + 1f));
        return list;
    }
}

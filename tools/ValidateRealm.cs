// Checks a map JSON with the same rules every shipping realm passes — run it
// after exporting a hand-made .tscn realm (see WoadRaiders.Client/tools/
// export_dungeon.gd), before serving the map. A .NET 10 file-based app:
//
//   dotnet run tools/ValidateRealm.cs WoadRaiders.Client/maps/MyRealm.json
//
// Realm maps (with terrain) get the full Core.RealmValidator treatment:
// every camp and the boss comfortably reachable from the spawn, borders
// sealed even against slope-inching, no stranding pits. Flat dungeon maps
// pass trivially (their geometry has no terrain to leak or strand).
//
// It can also prove two exports carry the same simulation geometry — e.g.
// that re-exporting Crag.tscn reproduces the generator's Crag.json:
//
//   dotnet run tools/ValidateRealm.cs a.json --compare b.json
//
// Exit code 0 = valid (and matching, when comparing).

#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Numerics;
using WoadRaiders.Core;

if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "--compare"))
{
    Console.Error.WriteLine("usage: dotnet run tools/ValidateRealm.cs <map.json> [--compare <other.json>]");
    return 2;
}

var map = DungeonGeometryFile.Load(args[0]);
Console.WriteLine($"[{Path.GetFileName(args[0])}] scene={map.ScenePath ?? "(none)"}, " +
                  $"{(map.Terrain is { } t ? $"terrain {t.Width}x{t.Depth} @ {t.CellSize}" : "no terrain")}, " +
                  $"{map.Solids.Count} solids, {map.EnemySpawns.Count} enemy spawns" +
                  $"{(map.BossSpawn is not null ? " + boss" : "")}, {map.Props.Count} props");

var ok = true;
if (map.Terrain is null)
{
    Console.WriteLine("no terrain — a flat dungeon map; realm checks don't apply.");
}
else
{
    var issues = RealmValidator.Validate(map);
    foreach (var issue in issues)
        Console.WriteLine($"  ISSUE  {issue}");
    Console.WriteLine(issues.Count == 0 ? "realm checks: ALL PASS" : $"realm checks: {issues.Count} ISSUE(S)");
    ok = issues.Count == 0;
}

if (args.Length == 3)
{
    var other = DungeonGeometryFile.Load(args[2]);
    Console.WriteLine($"comparing against [{Path.GetFileName(args[2])}] ...");
    var same = true;

    void Check(string what, bool match, string detail = "")
    {
        Console.WriteLine($"  {(match ? "MATCH " : "DIFFER")} {what}{(match || detail.Length == 0 ? "" : $" ({detail})")}");
        same &= match;
    }

    Check("spawn", Vector3.Distance(map.SpawnPoint, other.SpawnPoint) < 0.01f,
        $"{map.SpawnPoint} vs {other.SpawnPoint}");
    Check("boss", map.BossSpawn is null == other.BossSpawn is null
                  && (map.BossSpawn is not { } b || Vector3.Distance(b, other.BossSpawn!.Value) < 0.01f));

    if (map.Terrain is { } mine && other.Terrain is { } theirs)
    {
        var dims = mine.Width == theirs.Width && mine.Depth == theirs.Depth
                   && MathF.Abs(mine.CellSize - theirs.CellSize) < 0.001f
                   && MathF.Abs(mine.OriginX - theirs.OriginX) < 0.001f
                   && MathF.Abs(mine.OriginZ - theirs.OriginZ) < 0.001f;
        Check("terrain grid", dims,
            $"{mine.Width}x{mine.Depth}@{mine.CellSize} vs {theirs.Width}x{theirs.Depth}@{theirs.CellSize}");
        if (dims)
        {
            var worst = 0f;
            for (var i = 0; i < mine.Heights.Count; i++)
                worst = MathF.Max(worst, MathF.Abs(mine.Heights[i] - theirs.Heights[i]));
            Check("terrain heights", worst < 0.002f, $"largest sample difference {worst}");
        }
    }
    else
    {
        Check("terrain presence", map.Terrain is null == other.Terrain is null);
    }

    // Solids compare as sets (tree order vs generator order may differ).
    string SolidKey(Aabb s) =>
        $"{s.Min.X:0.##},{s.Min.Y:0.##},{s.Min.Z:0.##}:{s.Max.X:0.##},{s.Max.Y:0.##},{s.Max.Z:0.##}";
    var mySolids = map.Solids.Select(SolidKey).OrderBy(k => k).ToArray();
    var otherSolids = other.Solids.Select(SolidKey).OrderBy(k => k).ToArray();
    Check("solids", mySolids.SequenceEqual(otherSolids),
        $"{map.Solids.Count} vs {other.Solids.Count}");

    string SpawnKey(EnemySpawnPoint e) =>
        $"{e.Type}:{e.Position.X:0.##},{e.Position.Y:0.##},{e.Position.Z:0.##}";
    Check("enemy spawns", map.EnemySpawns.Select(SpawnKey).OrderBy(k => k)
            .SequenceEqual(other.EnemySpawns.Select(SpawnKey).OrderBy(k => k)),
        $"{map.EnemySpawns.Count} vs {other.EnemySpawns.Count}");

    string PropKey(DungeonProp p) =>
        $"{p.Type}:{p.Position.X:0.##},{p.Position.Y:0.##},{p.Position.Z:0.##}";
    Check("props", map.Props.Select(PropKey).OrderBy(k => k)
            .SequenceEqual(other.Props.Select(PropKey).OrderBy(k => k)),
        $"{map.Props.Count} vs {other.Props.Count}");

    Console.WriteLine(same ? "compare: GEOMETRY IDENTICAL" : "compare: GEOMETRY DIFFERS");
    ok &= same;
}

Console.WriteLine(ok ? "[valid]" : "[INVALID]");
return ok ? 0 : 1;

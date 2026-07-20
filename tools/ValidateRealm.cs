// Checks a map — a Godot .tscn realm scene (parsed by the shared
// Core.RealmSceneFile pipeline, exactly as the server will) or a classic
// geometry .json — with the same rules every shipping realm passes. Run it
// after hand-editing or hand-making a realm, before serving the map.
// A .NET 10 file-based app:
//
//   dotnet run tools/ValidateRealm.cs WoadRaiders.Client/maps/MyRealm.tscn
//
// Built realms (with a geometry soup) get the full Core.RealmValidator
// treatment on their baked navmesh: every camp and the boss reachable from
// the spawn by a complete route, nowhere reachable stranded. Flat test maps
// pass trivially (no geometry to leak or strand).
//
// It can also prove two maps carry the same simulation geometry (formats may
// differ — e.g. a baked JSON against the scene it came from):
//
//   dotnet run tools/ValidateRealm.cs a.tscn --compare b.json
//
// Exit code 0 = valid (and matching, when comparing).

#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Numerics;
using WoadRaiders.Core;

if (args.Length is not (1 or 3) || (args.Length == 3 && args[1] != "--compare"))
{
    Console.Error.WriteLine("usage: dotnet run tools/ValidateRealm.cs <map.tscn|map.json> [--compare <other>]");
    return 2;
}

var map = MapLoader.Load(args[0]);
Console.WriteLine($"[{Path.GetFileName(args[0])}] scene={map.ScenePath ?? "(none)"}, " +
                  $"{(map.Soup is { } t ? $"{t.Triangles.Length / 3} triangles" : "no geometry")}, " +
                  $"{map.EnemySpawns.Count} enemy spawns" +
                  $"{(map.BossSpawn is not null ? " + boss" : "")}");

var ok = true;
if (map.Soup is null)
{
    Console.WriteLine("no geometry — a flat test map; realm checks don't apply.");
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
    var other = MapLoader.Load(args[2]);
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

    if (map.Soup is { } mine && other.Soup is { } theirs)
    {
        var shape = mine.Triangles.Length == theirs.Triangles.Length;

        Check("soup shape", shape,
            $"{mine.Triangles.Length / 3} tris vs {theirs.Triangles.Length / 3}");
        if (shape)
        {
            // Triangles compare as sets: tree order vs generator order may
            // differ, but every stone must be the same stone.
            string TriKey(TriangleSoup soup, int t)
            {
                var keys = new string[3];
                for (var k = 0; k < 3; k++)
                {
                    var v = soup.Triangles[t * 3 + k] * 3;
                    keys[k] = $"{soup.Vertices[v]:0.##},{soup.Vertices[v + 1]:0.##},{soup.Vertices[v + 2]:0.##}";
                }
                Array.Sort(keys);
                return string.Join(";", keys);
            }
            var myTris = Enumerable.Range(0, mine.Triangles.Length / 3).Select(t => TriKey(mine, t)).OrderBy(k => k);
            var otherTris = Enumerable.Range(0, theirs.Triangles.Length / 3).Select(t => TriKey(theirs, t)).OrderBy(k => k);
            Check("soup triangles", myTris.SequenceEqual(otherTris));
        }
    }
    else
    {
        Check("soup presence", map.Soup is null == other.Soup is null);
    }

    string SpawnKey(EnemySpawnPoint e) =>
        $"{e.Type}:{e.Position.X:0.##},{e.Position.Y:0.##},{e.Position.Z:0.##}";
    Check("enemy spawns", map.EnemySpawns.Select(SpawnKey).OrderBy(k => k)
            .SequenceEqual(other.EnemySpawns.Select(SpawnKey).OrderBy(k => k)),
        $"{map.EnemySpawns.Count} vs {other.EnemySpawns.Count}");

    Console.WriteLine(same ? "compare: GEOMETRY IDENTICAL" : "compare: GEOMETRY DIFFERS");
    ok &= same;
}

Console.WriteLine(ok ? "[valid]" : "[INVALID]");
return ok ? 0 : 1;

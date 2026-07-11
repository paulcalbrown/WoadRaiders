using System.Numerics;
using System.Text.Json;

namespace WoadRaiders.Core;

/// <summary>
/// JSON (de)serialization for <see cref="DungeonGeometry"/> — the interchange
/// format between the Godot editor export tool (tools/export_dungeon.gd) and the
/// headless server. Engine-free so it is unit-testable and usable anywhere.
///
/// Shape:
/// {
///   "spawn": [x, y, z],
///   "solids": [ { "min": [x,y,z], "max": [x,y,z] }, ... ],
///   "enemySpawns": [ [x,y,z], ... ]
/// }
/// </summary>
public static class DungeonGeometryFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static DungeonGeometry Load(string path) => Parse(File.ReadAllText(path));

    public static DungeonGeometry Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<GeometryDoc>(json, Options)
                  ?? throw new InvalidDataException("empty dungeon geometry document");

        var solids = new List<Aabb>();
        foreach (var b in doc.Solids ?? Array.Empty<BoxDoc>())
            solids.Add(new Aabb(ToVec(b.Min, "solid.min"), ToVec(b.Max, "solid.max")));

        var spawns = new List<Vector3>();
        foreach (var s in doc.EnemySpawns ?? Array.Empty<float[]>())
            spawns.Add(ToVec(s, "enemySpawn"));

        return new DungeonGeometry(ToVec(doc.Spawn, "spawn"), solids, spawns);
    }

    public static string ToJson(DungeonGeometry g)
    {
        var doc = new GeometryDoc
        {
            Spawn = new[] { g.SpawnPoint.X, g.SpawnPoint.Y, g.SpawnPoint.Z },
            Solids = g.Solids.Select(s => new BoxDoc
            {
                Min = new[] { s.Min.X, s.Min.Y, s.Min.Z },
                Max = new[] { s.Max.X, s.Max.Y, s.Max.Z },
            }).ToArray(),
            EnemySpawns = g.EnemySpawns.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
        };
        return JsonSerializer.Serialize(doc, Options);
    }

    private static Vector3 ToVec(float[]? xyz, string what) =>
        xyz is { Length: 3 }
            ? new Vector3(xyz[0], xyz[1], xyz[2])
            : throw new InvalidDataException($"'{what}' must be a [x, y, z] array");

    private sealed class GeometryDoc
    {
        public float[]? Spawn { get; set; }
        public BoxDoc[]? Solids { get; set; }
        public float[][]? EnemySpawns { get; set; }
    }

    private sealed class BoxDoc
    {
        public float[]? Min { get; set; }
        public float[]? Max { get; set; }
    }
}

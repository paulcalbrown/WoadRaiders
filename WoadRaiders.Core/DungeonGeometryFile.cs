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
///   "scene": "res://maps/YourMap.tscn",          (optional — authored visuals)
///   "spawn": [x, y, z],
///   "solids": [ { "min": [x,y,z], "max": [x,y,z] }, ... ],
///   "enemySpawns": [ [x,y,z], ... ],
///   "enemySpawnTypes": [ 0, 1, 2, ... ],         (optional — parallel to enemySpawns;
///                                                 EnemyType values, missing → all Minion)
///   "bossSpawn": [x, y, z]                       (optional — the map's boss)
/// }
/// </summary>
public static class DungeonGeometryFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static DungeonGeometry Load(string path) => Parse(File.ReadAllText(path));

    public static DungeonGeometry Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<GeometryDoc>(json, Options)
                  ?? throw new InvalidDataException("empty dungeon geometry document");

        var solids = new List<Aabb>();
        foreach (var b in doc.Solids ?? Array.Empty<BoxDoc>())
            solids.Add(new Aabb(ToVec(b.Min, "solid.min"), ToVec(b.Max, "solid.max")));

        var positions = doc.EnemySpawns ?? Array.Empty<float[]>();
        var types = doc.EnemySpawnTypes;
        if (types is not null && types.Length != positions.Length)
            throw new InvalidDataException("'enemySpawnTypes' must be parallel to 'enemySpawns'");

        var spawns = new List<EnemySpawnPoint>(positions.Length);
        for (var i = 0; i < positions.Length; i++)
        {
            // Validate the RAW int (casting first would wrap modulo 256 past the
            // check), and reject Boss here too: bosses are only ever authored via
            // 'bossSpawn' — a boss-typed marker would silently break the server's
            // population accounting and boss-respawn tracking.
            var raw = types?[i] ?? (int)EnemyType.Minion;
            if (raw < 0 || raw >= (int)EnemyType.Boss)
                throw new InvalidDataException(
                    $"'enemySpawnTypes[{i}]' = {raw} is invalid (0..{(int)EnemyType.Boss - 1}; bosses use 'bossSpawn')");
            spawns.Add(new EnemySpawnPoint(ToVec(positions[i], "enemySpawn"), (EnemyType)raw));
        }

        return new DungeonGeometry(ToVec(doc.Spawn, "spawn"), solids, spawns)
        {
            ScenePath = string.IsNullOrWhiteSpace(doc.Scene) ? null : doc.Scene,
            BossSpawn = doc.BossSpawn is null ? null : ToVec(doc.BossSpawn, "bossSpawn"),
        };
    }

    public static string ToJson(DungeonGeometry g)
    {
        var doc = new GeometryDoc
        {
            Scene = g.ScenePath,
            Spawn = new[] { g.SpawnPoint.X, g.SpawnPoint.Y, g.SpawnPoint.Z },
            Solids = g.Solids.Select(s => new BoxDoc
            {
                Min = new[] { s.Min.X, s.Min.Y, s.Min.Z },
                Max = new[] { s.Max.X, s.Max.Y, s.Max.Z },
            }).ToArray(),
            EnemySpawns = g.TypedEnemySpawns.Select(s => new[] { s.Position.X, s.Position.Y, s.Position.Z }).ToArray(),
            EnemySpawnTypes = g.TypedEnemySpawns.Select(s => (int)s.Type).ToArray(),
            BossSpawn = g.BossSpawn is { } b ? new[] { b.X, b.Y, b.Z } : null,
        };
        return JsonSerializer.Serialize(doc, Options);
    }

    private static Vector3 ToVec(float[]? xyz, string what) =>
        xyz is { Length: 3 }
            ? new Vector3(xyz[0], xyz[1], xyz[2])
            : throw new InvalidDataException($"'{what}' must be a [x, y, z] array");

    private sealed class GeometryDoc
    {
        public string? Scene { get; set; }
        public float[]? Spawn { get; set; }
        public BoxDoc[]? Solids { get; set; }
        public float[][]? EnemySpawns { get; set; }
        public int[]? EnemySpawnTypes { get; set; }
        public float[]? BossSpawn { get; set; }
    }

    private sealed class BoxDoc
    {
        public float[]? Min { get; set; }
        public float[]? Max { get; set; }
    }
}

using System.Numerics;
using System.Text.Json;

namespace WoadRaiders.Core;

/// <summary>
/// JSON (de)serialization for <see cref="DungeonGeometry"/> — the interchange
/// format between map generators / the Godot editor export tool and the
/// headless server. Engine-free so it is unit-testable and usable anywhere.
///
/// Shape:
/// {
///   "scene": "res://maps/YourMap.tscn",          (optional — visual identity / authored scene)
///   "spawn": [x, y, z],
///   "terrain": {                                  (optional — smooth heightfield base plane)
///     "originX": x, "originZ": z, "cellSize": s,
///     "width": w, "depth": d,
///     "heights": [w*d floats, row-major]
///   },
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

        return new DungeonGeometry(ToVec(doc.Spawn, "spawn"), solids, spawns, ParseTerrain(doc.Terrain))
        {
            ScenePath = string.IsNullOrWhiteSpace(doc.Scene) ? null : doc.Scene,
            BossSpawn = doc.BossSpawn is null ? null : ToVec(doc.BossSpawn, "bossSpawn"),
        };
    }

    private static HeightField? ParseTerrain(TerrainDoc? doc)
    {
        if (doc is null)
            return null;
        if (doc.Heights is null)
            throw new InvalidDataException("'terrain.heights' is required when 'terrain' is present");
        // The HeightField constructor validates dimensions, cell size, and finiteness.
        return new HeightField(doc.OriginX, doc.OriginZ, doc.CellSize, doc.Width, doc.Depth, doc.Heights);
    }

    public static string ToJson(DungeonGeometry g)
    {
        var doc = new GeometryDoc
        {
            Scene = g.ScenePath,
            Spawn = new[] { g.SpawnPoint.X, g.SpawnPoint.Y, g.SpawnPoint.Z },
            Terrain = g.Terrain is { } t
                ? new TerrainDoc
                {
                    OriginX = t.OriginX, OriginZ = t.OriginZ, CellSize = t.CellSize,
                    Width = t.Width, Depth = t.Depth, Heights = t.Heights.ToArray(),
                }
                : null,
            Solids = g.Solids.Select(s => new BoxDoc
            {
                Min = new[] { s.Min.X, s.Min.Y, s.Min.Z },
                Max = new[] { s.Max.X, s.Max.Y, s.Max.Z },
            }).ToArray(),
            EnemySpawns = g.EnemySpawns.Select(s => new[] { s.Position.X, s.Position.Y, s.Position.Z }).ToArray(),
            EnemySpawnTypes = g.EnemySpawns.Select(s => (int)s.Type).ToArray(),
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
        public TerrainDoc? Terrain { get; set; }
        public BoxDoc[]? Solids { get; set; }
        public float[][]? EnemySpawns { get; set; }
        public int[]? EnemySpawnTypes { get; set; }
        public float[]? BossSpawn { get; set; }
    }

    private sealed class TerrainDoc
    {
        public float OriginX { get; set; }
        public float OriginZ { get; set; }
        public float CellSize { get; set; }
        public int Width { get; set; }
        public int Depth { get; set; }
        public float[]? Heights { get; set; }
    }

    private sealed class BoxDoc
    {
        public float[]? Min { get; set; }
        public float[]? Max { get; set; }
    }
}

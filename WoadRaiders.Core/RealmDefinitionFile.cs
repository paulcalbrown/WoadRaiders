using System.Numerics;
using System.Text.Json;

namespace WoadRaiders.Core;

/// <summary>
/// JSON (de)serialization for <see cref="RealmDefinition"/> — the interchange
/// format between the realm bake / the Godot editor export tool and the
/// headless server. Engine-free so it is unit-testable and usable anywhere.
///
/// Shape:
/// {
///   "scene": "res://maps/YourMap.tscn",          (optional — visual identity / authored scene)
///   "spawn": [x, y, z],
///   "soup": {                                     (optional — a soupless map is the flat test arena)
///     "vertices": [x,y,z, x,y,z, ...],            (welded: a corner appears once, however many
///                                                  triangles name it)
///     "triangles": [a,b,c, a,b,c, ...]            (untyped: order carries no meaning)
///   },
///   "enemySpawns": [ [x,y,z], ... ],
///   "enemySpawnTypes": [ 0, 1, 2, ... ],         (optional — parallel to enemySpawns;
///                                                 EnemyType values, missing → all Minion)
///   "bossSpawn": [x, y, z]                       (optional — the map's boss)
/// }
/// </summary>
public static class RealmDefinitionFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        // NOT indented. A realm's soup is hundreds of thousands of numbers and
        // this file is derived output — never hand-edited, always re-baked
        // from the scene — so a line and an indent per coordinate buys nothing
        // and costs megabytes in the repository.
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static RealmDefinition Load(string path) => Parse(File.ReadAllText(path));

    public static RealmDefinition Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<GeometryDoc>(json, Options)
                  ?? throw new InvalidDataException("empty realm definition document");

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

        return new RealmDefinition(ToVec(doc.Spawn, "spawn"), ParseSoup(doc.Soup), spawns)
        {
            ScenePath = string.IsNullOrWhiteSpace(doc.Scene) ? null : doc.Scene,
            BossSpawn = doc.BossSpawn is null ? null : ToVec(doc.BossSpawn, "bossSpawn"),
        };
    }

    private static TriangleSoup? ParseSoup(SoupDoc? doc)
    {
        if (doc is null)
            return null;
        if (doc.Vertices is null || doc.Triangles is null)
            throw new InvalidDataException("'soup.vertices' and 'soup.triangles' are required when 'soup' is present");
        // The TriangleSoup constructor validates lengths, indices, and finiteness.
        // A 'floorTriangleCount' from an older bake is read and ignored: the
        // split it recorded is now derived from the geometry itself.
        return new TriangleSoup(doc.Vertices, doc.Triangles);
    }

    public static string ToJson(RealmDefinition g)
    {
        var doc = new GeometryDoc
        {
            Scene = g.ScenePath,
            Spawn = new[] { g.SpawnPoint.X, g.SpawnPoint.Y, g.SpawnPoint.Z },
            Soup = g.Soup is { } soup
                ? new SoupDoc
                {
                    Vertices = soup.Vertices,
                    Triangles = soup.Triangles,
                }
                : null,
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
        public SoupDoc? Soup { get; set; }
        public float[][]? EnemySpawns { get; set; }
        public int[]? EnemySpawnTypes { get; set; }
        public float[]? BossSpawn { get; set; }
    }

    private sealed class SoupDoc
    {
        public float[]? Vertices { get; set; }
        public int[]? Triangles { get; set; }
    }
}

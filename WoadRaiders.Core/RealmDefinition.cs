using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>An axis-aligned box in world space (bounds, authoring fixtures).</summary>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Size => Max - Min;
}

/// <summary>
/// A realm as DATA: its triangle soup (untyped — see
/// <see cref="TriangleSoup"/>), spawn markers, and identity. This class only
/// DESCRIBES the realm — it answers no spatial queries and is not an
/// <see cref="IRealmGeometry"/>; movement lives in <see cref="RealmGeometry"/>,
/// baked from this soup. A realm without a soup is the implicit flat arena
/// (tiny test maps): the world falls back to its open-arena clamp rules.
/// </summary>
public sealed class RealmDefinition
{
    /// <summary>Where new/respawning players are placed.</summary>
    public Vector3 SpawnPoint { get; }

    /// <summary>The realm's geometry. Null → the implicit flat arena.</summary>
    public TriangleSoup? Soup { get; }

    /// <summary>Regular enemy spawn markers — where each one is and what type it produces.</summary>
    public IReadOnlyList<EnemySpawnPoint> EnemySpawns { get; }

    /// <summary>
    /// Where the map's boss stands, if it has one. The boss is a singleton with a
    /// lifecycle unlike the regular population — spawned once, respawned on a long
    /// delay, never counted toward the enemy target, wider collision — so it is a
    /// single optional point rather than an <see cref="EnemySpawnPoint"/> in
    /// <see cref="EnemySpawns"/>. The loader rejects Boss-typed markers to keep that
    /// invariant: at most one boss, authored here.
    /// </summary>
    public Vector3? BossSpawn { get; init; }

    /// <summary>
    /// Identity of the map's authored visuals, if any (a res:// scene path).
    /// Presentation metadata — the simulation never reads it.
    /// </summary>
    public string? ScenePath { get; init; }

    /// <summary>World extent of the soup (a safety clamp / render hint).</summary>
    public Aabb Bounds { get; }

    public RealmDefinition(Vector3 spawnPoint, TriangleSoup? soup, IReadOnlyList<EnemySpawnPoint> enemySpawns)
    {
        SpawnPoint = spawnPoint;
        Soup = soup;
        EnemySpawns = enemySpawns;
        Bounds = soup is not null
            ? new Aabb(soup.BoundsMin, soup.BoundsMax)
            : new Aabb(Vector3.Zero, Vector3.Zero);
    }

    /// <summary>A random enemy spawn marker (respawns keep each marker's enemy type).</summary>
    public EnemySpawnPoint RandomEnemySpawn(Random rng) =>
        EnemySpawns.Count > 0
            ? EnemySpawns[rng.Next(EnemySpawns.Count)]
            : new EnemySpawnPoint(SpawnPoint, EnemyType.Minion);
}

/// <summary>An enemy spawn marker: where, and what kind of enemy it produces.</summary>
public readonly record struct EnemySpawnPoint(Vector3 Position, EnemyType Type);

using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>An axis-aligned box in world space (the unit of dungeon collision).</summary>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Size => Max - Min;
}

/// <summary>
/// Fully 3D dungeon geometry: a set of solid world-space boxes plus spawn
/// markers. This is the shape a Godot-editor scene naturally reduces to
/// (CollisionShape3D/BoxShape3D + Marker3D nodes), exported via
/// tools/export_dungeon.gd — and equally what the procedural generator emits.
///
/// Characters are treated as vertical cylinders (SimConstants.CharacterRadius /
/// CharacterHeight), so collision is 3D-aware: a wall blocks you, a beam above
/// head height does not. Movement slides along solids, axis-separated.
/// </summary>
public sealed class DungeonGeometry : IDungeonGeometry
{
    private const float Eps = 0.01f;

    public Vector3 SpawnPoint { get; }
    public IReadOnlyList<Aabb> Solids { get; }

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
    /// res:// path of the Godot scene these solids were exported from, so clients
    /// can render the authored visuals. Null for procedural maps (clients fall
    /// back to placeholder rendering). Presentation metadata — the simulation
    /// never reads it.
    /// </summary>
    public string? ScenePath { get; init; }

    /// <summary>World extent of all solids (used as a safety clamp and for rendering the floor).</summary>
    public Aabb Bounds { get; }

    public DungeonGeometry(Vector3 spawnPoint, IReadOnlyList<Aabb> solids, IReadOnlyList<EnemySpawnPoint> enemySpawns)
    {
        SpawnPoint = spawnPoint;
        Solids = solids;
        EnemySpawns = enemySpawns;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var s in solids)
        {
            min = Vector3.Min(min, s.Min);
            max = Vector3.Max(max, s.Max);
        }
        Bounds = solids.Count > 0 ? new Aabb(min, max) : new Aabb(Vector3.Zero, Vector3.Zero);
    }

    /// <summary>
    /// True if a character standing at <paramref name="feet"/> would intersect a
    /// solid. A box only blocks when its vertical span overlaps the character's
    /// (feet .. feet+height) and its XZ footprint is within the character radius.
    /// </summary>
    public bool IsBlocked(Vector3 feet, float radius = SimConstants.CharacterRadius)
    {
        foreach (var s in Solids)
        {
            // Vertical overlap: ignore floors under the feet and beams above the head.
            if (s.Min.Y >= feet.Y + SimConstants.CharacterHeight || s.Max.Y <= feet.Y + Eps)
                continue;

            // XZ: closest point on the box footprint to the character centre.
            var cx = Math.Clamp(feet.X, s.Min.X, s.Max.X);
            var cz = Math.Clamp(feet.Z, s.Min.Z, s.Max.Z);
            var dx = feet.X - cx;
            var dz = feet.Z - cz;
            if (dx * dx + dz * dz < radius * radius)
                return true;
        }
        return false;
    }

    /// <summary>Axis-separated XZ slide against the solids; Y passes through unchanged.</summary>
    public Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius)
    {
        var result = position;

        var tryX = new Vector3(result.X + delta.X, result.Y, result.Z);
        if (!IsBlocked(tryX, radius))
            result = tryX;

        var tryZ = new Vector3(result.X, result.Y, result.Z + delta.Z);
        if (!IsBlocked(tryZ, radius))
            result = tryZ;

        // Note: no world-bounds clamp — maps are responsible for being sealed
        // (the procedural generator always is; authored maps should be too).
        return result;
    }

    /// <summary>
    /// Segment-vs-solids visibility: true when the straight line from
    /// <paramref name="from"/> to <paramref name="to"/> touches no solid
    /// (standard slab test per box). Used for enemy aggro and attacks.
    /// </summary>
    public bool HasLineOfSight(Vector3 from, Vector3 to)
    {
        var d = to - from;
        foreach (var s in Solids)
        {
            var tMin = 0f;
            var tMax = 1f;
            var hit = true;

            for (var axis = 0; axis < 3 && hit; axis++)
            {
                var (o, dir, lo, hi) = axis switch
                {
                    0 => (from.X, d.X, s.Min.X, s.Max.X),
                    1 => (from.Y, d.Y, s.Min.Y, s.Max.Y),
                    _ => (from.Z, d.Z, s.Min.Z, s.Max.Z),
                };

                if (Math.Abs(dir) < 1e-6f)
                {
                    if (o < lo || o > hi)
                        hit = false; // parallel and outside the slab — this box can't be hit
                }
                else
                {
                    var t1 = (lo - o) / dir;
                    var t2 = (hi - o) / dir;
                    if (t1 > t2)
                        (t1, t2) = (t2, t1);
                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);
                    if (tMin > tMax)
                        hit = false;
                }
            }

            if (hit)
                return false; // the sight line passes through this solid
        }
        return true;
    }

    /// <summary>A random enemy spawn marker (respawns keep each marker's enemy type).</summary>
    public EnemySpawnPoint RandomEnemySpawn(Random rng) =>
        EnemySpawns.Count > 0
            ? EnemySpawns[rng.Next(EnemySpawns.Count)]
            : new EnemySpawnPoint(SpawnPoint, EnemyType.Minion);
}

/// <summary>An enemy spawn marker: where, and what kind of enemy it produces.</summary>
public readonly record struct EnemySpawnPoint(Vector3 Position, EnemyType Type);

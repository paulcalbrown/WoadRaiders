using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>An axis-aligned box in world space (the unit of dungeon collision).</summary>
public readonly record struct Aabb(Vector3 Min, Vector3 Max)
{
    public Vector3 Center => (Min + Max) * 0.5f;
    public Vector3 Size => Max - Min;
}

/// <summary>
/// Fully 3D realm geometry: an optional smooth <see cref="HeightField"/> terrain
/// as the base plane (flat y=0 without one), plus solid world-space boxes
/// (bridges, ramparts, standing stones) and spawn markers.
///
/// Characters are vertical cylinders (SimConstants.CharacterRadius/Height) whose
/// feet ride the ground. Movement is axis-separated: each axis-move lands on the
/// ground at its destination — the terrain, or the top of a solid no more than
/// <see cref="SimConstants.StepHeight"/> above the current feet. A rise beyond
/// StepHeight is a wall (or an unclimbable cliff) and the move is refused, as is
/// a landing without headroom; drops of any size are allowed. That one rule
/// gives stairs, ramps, ledges, bridges, and one-way jump-downs for free.
/// </summary>
public sealed class DungeonGeometry : IDungeonGeometry
{
    private const float Eps = 0.01f;

    public Vector3 SpawnPoint { get; }
    public IReadOnlyList<Aabb> Solids { get; }

    /// <summary>The smooth terrain base plane. Null → the implicit flat plane y=0.</summary>
    public HeightField? Terrain { get; init; }

    /// <summary>Cosmetic props (braziers etc.) the client dresses the realm with.</summary>
    public IReadOnlyList<DungeonProp> Props { get; init; } = Array.Empty<DungeonProp>();

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
    /// Identity of the map's authored visuals, if any (a res:// scene path). The
    /// client renders the scene when it exists; a terrain-bearing realm is built
    /// from the geometry itself and uses this only as a catalog identity token.
    /// Presentation metadata — the simulation never reads it.
    /// </summary>
    public string? ScenePath { get; init; }

    /// <summary>World extent of the solids and terrain (a safety clamp / render hint).</summary>
    public Aabb Bounds { get; }

    public DungeonGeometry(Vector3 spawnPoint, IReadOnlyList<Aabb> solids, IReadOnlyList<EnemySpawnPoint> enemySpawns,
                           HeightField? terrain = null)
    {
        Terrain = terrain;
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
        if (terrain is not null)
        {
            var lo = float.MaxValue;
            var hi = float.MinValue;
            foreach (var h in terrain.Heights)
            {
                lo = Math.Min(lo, h);
                hi = Math.Max(hi, h);
            }
            min = Vector3.Min(min, new Vector3(terrain.OriginX, lo, terrain.OriginZ));
            max = Vector3.Max(max, new Vector3(terrain.MaxX, hi, terrain.MaxZ));
        }
        Bounds = solids.Count > 0 || terrain is not null
            ? new Aabb(min, max)
            : new Aabb(Vector3.Zero, Vector3.Zero);
    }

    /// <summary>The terrain base plane's height (no solids) — what a projectile hugs.</summary>
    public float GroundHeight(float x, float z) => Terrain?.Sample(x, z) ?? 0f;

    /// <summary>
    /// The surface a character standing at this XZ with feet near
    /// <paramref name="fromY"/> would rest on: the terrain, or the highest solid
    /// top within reach — at most StepHeight above the current feet (anything
    /// higher is a wall, not a step). Never below the terrain.
    /// </summary>
    public float WalkSurface(float x, float z, float fromY, float radius = SimConstants.CharacterRadius)
    {
        var ground = GroundHeight(x, z);
        var reach = fromY + SimConstants.StepHeight;
        foreach (var s in Solids)
        {
            if (s.Max.Y > reach || s.Max.Y <= ground)
                continue;
            if (FootprintWithin(s, x, z, radius))
                ground = s.Max.Y;
        }
        return ground;
    }

    /// <summary>
    /// True if a character whose feet rest at <paramref name="feet"/> would
    /// intersect a wall: a solid overlapping the body column above the step
    /// band. Solids at or below feet+StepHeight are floor (you stand on them,
    /// they never block); solids starting above head height are beams and
    /// don't block either.
    /// </summary>
    public bool IsBlocked(Vector3 feet, float radius = SimConstants.CharacterRadius)
    {
        foreach (var s in Solids)
        {
            if (s.Min.Y >= feet.Y + SimConstants.CharacterHeight || s.Max.Y <= feet.Y + SimConstants.StepHeight + Eps)
                continue;
            if (FootprintWithin(s, feet.X, feet.Z, radius))
                return true;
        }
        return false;
    }

    /// <summary>Closest point of the box footprint within the cylinder radius on XZ?</summary>
    private static bool FootprintWithin(in Aabb s, float x, float z, float radius)
    {
        var cx = Math.Clamp(x, s.Min.X, s.Max.X);
        var cz = Math.Clamp(z, s.Min.Z, s.Max.Z);
        var dx = x - cx;
        var dz = z - cz;
        return dx * dx + dz * dz < radius * radius;
    }

    /// <summary>
    /// Axis-separated ground-riding move: each axis-move lands on the walk
    /// surface at its destination, refused when the ground there rises more than
    /// StepHeight above the current feet (a wall or cliff) or a solid denies the
    /// landing headroom. Y always follows the ground — walking is what climbs
    /// ramps and drops off ledges.
    /// </summary>
    public Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius)
    {
        var result = position;
        result = TryAxisMove(result, delta.X, 0f, radius);
        result = TryAxisMove(result, 0f, delta.Z, radius);
        return result;
    }

    private Vector3 TryAxisMove(Vector3 from, float dx, float dz, float radius)
    {
        var x = from.X + dx;
        var z = from.Z + dz;
        var ground = WalkSurface(x, z, from.Y, radius);
        if (ground > from.Y + SimConstants.StepHeight)
            return from; // the land itself rises too steeply — an unclimbable cliff
        var landed = new Vector3(x, ground, z);
        return IsBlocked(landed, radius) ? from : landed;
    }

    /// <summary>
    /// Segment visibility: true when the straight line from <paramref name="from"/>
    /// to <paramref name="to"/> passes no solid (slab test per box) and never dips
    /// into the terrain. Used for enemy aggro, attacks, and projectile flight.
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

        return !TerrainOccludes(from, to);
    }

    /// <summary>Does the segment dip below the terrain surface anywhere along its run?</summary>
    private bool TerrainOccludes(Vector3 from, Vector3 to)
    {
        if (Terrain is not { } terrain)
            return false;

        var d = to - from;
        var flat = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
        // Sample every half-cell: fine enough that no crest slips between samples.
        var steps = Math.Max(1, (int)(flat / (terrain.CellSize * 0.5f)));
        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            var p = from + d * t;
            if (terrain.Sample(p.X, p.Z) > p.Y + Eps)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Where a ray first meets the walkable world (terrain or a solid top under
    /// it) — the client's cursor picking. Marches the ray, then bisects the
    /// crossing for a tight point. False when the ray escapes without landing.
    /// </summary>
    public bool RaycastGround(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit)
    {
        hit = default;
        if (direction.LengthSquared() < 1e-8f)
            return false;
        direction = Vector3.Normalize(direction);

        var step = Terrain is { } t ? t.CellSize * 0.5f : 20f;
        var prevT = 0f;
        for (var travelled = step; travelled <= maxDistance; travelled += step)
        {
            var p = origin + direction * travelled;
            if (p.Y <= SurfaceUnder(p))
            {
                // Bisect between the last clear point and this buried one.
                float lo = prevT, hi = travelled;
                for (var i = 0; i < 16; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    var q = origin + direction * mid;
                    if (q.Y <= SurfaceUnder(q))
                        hi = mid;
                    else
                        lo = mid;
                }
                var land = origin + direction * hi;
                hit = new Vector3(land.X, SurfaceUnder(land), land.Z);
                return true;
            }
            prevT = travelled;
        }
        return false;
    }

    /// <summary>The highest walkable surface at this XZ that sits at or below the point:
    /// the terrain, or a solid top (a bridge deck under the cursor ray).</summary>
    private float SurfaceUnder(Vector3 p)
    {
        var ground = GroundHeight(p.X, p.Z);
        foreach (var s in Solids)
        {
            if (s.Max.Y <= ground || s.Max.Y > p.Y + Eps)
                continue;
            if (p.X >= s.Min.X && p.X <= s.Max.X && p.Z >= s.Min.Z && p.Z <= s.Max.Z)
                ground = s.Max.Y;
        }
        return ground;
    }

    /// <summary>A random enemy spawn marker (respawns keep each marker's enemy type).</summary>
    public EnemySpawnPoint RandomEnemySpawn(Random rng) =>
        EnemySpawns.Count > 0
            ? EnemySpawns[rng.Next(EnemySpawns.Count)]
            : new EnemySpawnPoint(SpawnPoint, EnemyType.Minion);
}

/// <summary>An enemy spawn marker: where, and what kind of enemy it produces.</summary>
public readonly record struct EnemySpawnPoint(Vector3 Position, EnemyType Type);

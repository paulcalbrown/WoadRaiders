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
    public IReadOnlyList<Vector3> EnemySpawns { get; }

    /// <summary>
    /// res:// path of the Godot scene these solids were exported from, so clients
    /// can render the authored visuals. Null for procedural maps (clients fall
    /// back to placeholder rendering). Presentation metadata — the simulation
    /// never reads it.
    /// </summary>
    public string? ScenePath { get; init; }

    /// <summary>World extent of all solids (used as a safety clamp and for rendering the floor).</summary>
    public Aabb Bounds { get; }

    public DungeonGeometry(Vector3 spawnPoint, IReadOnlyList<Aabb> solids, IReadOnlyList<Vector3> enemySpawns)
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
    public bool IsBlocked(Vector3 feet)
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
            if (dx * dx + dz * dz < SimConstants.CharacterRadius * SimConstants.CharacterRadius)
                return true;
        }
        return false;
    }

    /// <summary>Axis-separated XZ slide against the solids; Y passes through unchanged.</summary>
    public Vector3 Move(Vector3 position, Vector3 delta)
    {
        var result = position;

        var tryX = new Vector3(result.X + delta.X, result.Y, result.Z);
        if (!IsBlocked(tryX))
            result = tryX;

        var tryZ = new Vector3(result.X, result.Y, result.Z + delta.Z);
        if (!IsBlocked(tryZ))
            result = tryZ;

        // Note: no world-bounds clamp — maps are responsible for being sealed
        // (the procedural generator always is; authored maps should be too).
        return result;
    }

    public Vector3 RandomSpawnPosition(Random rng) =>
        EnemySpawns.Count > 0 ? EnemySpawns[rng.Next(EnemySpawns.Count)] : SpawnPoint;
}

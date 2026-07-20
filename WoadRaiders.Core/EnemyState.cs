using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>Authoritative state for a single server-controlled enemy.</summary>
public sealed class EnemyState : Combatant
{
    /// <summary>What kind of enemy this is; stats come from <see cref="EnemyArchetypes"/>.</summary>
    public EnemyType Type { get; }

    /// <summary>The post this enemy guards (its spawn point); it walks back here after losing aggro.</summary>
    public Vector3 HomePosition;

    /// <summary>True while this enemy is actively pursuing a player.</summary>
    public bool Aggroed;

    // --- Pursuit routing (server-side AI scratch; never serialized) ---

    /// <summary>The planned route this enemy is following, when it has one.</summary>
    public readonly List<Vector3> Route = new();

    /// <summary>The next waypoint to reach in <see cref="Route"/>.</summary>
    public int RouteIndex;

    /// <summary>Where the route was planned to; a drifted destination forces a replan.</summary>
    public Vector3 RouteTarget;

    /// <summary>Seconds until this enemy may plan a route again.</summary>
    public float RepathCooldown;

    public EnemyState(int id, EnemyType type = EnemyType.Minion)
        : base(id, EnemyArchetypes.Of(type).MaxHealth)
    {
        Type = type;
    }
}

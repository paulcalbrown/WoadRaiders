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

    public EnemyState(int id, EnemyType type = EnemyType.Minion)
        : base(id, EnemyArchetypes.Of(type).MaxHealth)
    {
        Type = type;
    }
}

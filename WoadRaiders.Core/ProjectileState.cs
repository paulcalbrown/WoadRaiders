using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// What a projectile is — which side it hurts, and which visual the client
/// picks. Serialized as a byte in projectile snapshots, so keep the numbering
/// stable.
/// </summary>
public enum ProjectileKind : byte
{
    EnemyBolt = 0, // a skeleton mage's spell bolt — hits players
    MagicBolt = 1, // a player mage's bolt — hits enemies
    Arrow = 2,     // a player ranger's bolt — hits enemies
}

/// <summary>
/// An in-flight projectile. Travels in a straight line, hits the first alive
/// combatant on the opposing side it touches, and dies on a wall or when its
/// life runs out. Server-authoritative like everything else — the client only
/// draws it.
/// </summary>
public sealed class ProjectileState
{
    public int Id { get; }

    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>Damage dealt to the combatant it strikes (before any armor).</summary>
    public float Damage;

    /// <summary>Seconds of flight remaining before it fizzles.</summary>
    public float Life;

    /// <summary>Which side it hurts and how the client draws it.</summary>
    public ProjectileKind Kind;

    /// <summary>Enemy-fired bolts strike players; player-fired ones strike enemies.</summary>
    public bool HostileToPlayers => Kind == ProjectileKind.EnemyBolt;

    public ProjectileState(int id) => Id = id;
}

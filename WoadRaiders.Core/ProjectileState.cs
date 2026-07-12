using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// An in-flight projectile (a mage's spell bolt). Travels in a straight line,
/// hits the first alive player it touches, and dies on a wall or when its life
/// runs out. Server-authoritative like everything else — the client only draws it.
/// </summary>
public sealed class ProjectileState
{
    public int Id { get; }

    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>Damage dealt to the player it strikes (before armor).</summary>
    public float Damage;

    /// <summary>Seconds of flight remaining before it fizzles.</summary>
    public float Life;

    public ProjectileState(int id) => Id = id;
}

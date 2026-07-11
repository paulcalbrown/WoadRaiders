using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Anything that can move, take damage, and attack — the shared base for players
/// and enemies. Keeping health/targeting here means combat resolution works the
/// same regardless of who is hitting whom.
/// </summary>
public abstract class Combatant
{
    public int Id { get; }

    public Vector2 Position;
    public Vector2 Velocity;
    public float Health;
    public float MaxHealth { get; }

    /// <summary>Seconds remaining until this combatant may attack again.</summary>
    public float AttackCooldown;

    public bool IsAlive => Health > 0f;

    protected Combatant(int id, float maxHealth)
    {
        Id = id;
        MaxHealth = maxHealth;
        Health = maxHealth;
    }

    public void TakeDamage(float amount) => Health = Math.Max(0f, Health - amount);
}

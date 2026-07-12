using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Anything that can move, take damage, and attack — the shared base for players
/// and enemies. Keeping health/targeting here means combat resolution works the
/// same regardless of who is hitting whom. Positions are world-space, Y-up.
/// </summary>
public abstract class Combatant
{
    public int Id { get; }

    public Vector3 Position;
    public Vector3 Velocity;
    public float Health;
    public float MaxHealth { get; }

    /// <summary>Seconds remaining until this combatant may attack again.</summary>
    public float AttackCooldown;

    /// <summary>
    /// Seconds remaining in the current attack animation. Set when an attack
    /// lands, ticked down each step, and broadcast so clients play the attack
    /// clip for this combatant (presentation only — never affects the sim).
    /// </summary>
    public float AttackAnimRemaining;

    /// <summary>True while the attack animation should be playing.</summary>
    public bool IsAttacking => AttackAnimRemaining > 0f;

    public bool IsAlive => Health > 0f;

    protected Combatant(int id, float maxHealth)
    {
        Id = id;
        MaxHealth = maxHealth;
        Health = maxHealth;
    }

    public void TakeDamage(float amount) => Health = Math.Max(0f, Health - amount);

    /// <summary>Restore health, capped at max. Returns the amount actually restored.</summary>
    public float Heal(float amount)
    {
        var healed = Math.Clamp(amount, 0f, MaxHealth - Health);
        Health += healed;
        return healed;
    }
}

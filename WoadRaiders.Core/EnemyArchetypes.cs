namespace WoadRaiders.Core;

/// <summary>
/// The kinds of enemy the simulation knows. Serialized as a byte in enemy
/// snapshots, so keep the numbering stable.
/// </summary>
public enum EnemyType : byte
{
    Minion = 0, // basic melee chaser
    Rogue = 1,  // fast, fragile, quick strikes
    Mage = 2,   // slow, zaps from range
    Boss = 3,   // the Barrow King: huge, heavy hits, guaranteed loot
}

/// <summary>Per-type combat stats. Presentation (model, scale) lives client-side.</summary>
public readonly record struct EnemyArchetype(
    float MaxHealth,
    float MoveSpeed,
    float AttackDamage,
    float AttackRange,
    float AttackCooldown,
    float AggroRange,
    int GuaranteedDrops,
    float Radius,
    float ProjectileSpeed = 0f); // 0 = instant melee; > 0 = fires a travelling bolt

/// <summary>
/// The stat table for every <see cref="EnemyType"/>. Kept in Core so the server,
/// tests, and the client (health-bar fractions) all agree on the numbers.
/// </summary>
public static class EnemyArchetypes
{
    // Minion deliberately mirrors the classic SimConstants values so existing
    // balance (and the tests written against those constants) carry over 1:1.
    // Radius: collision cylinder — the boss is drawn 2.2x, so he collides wider
    // too (otherwise his model clips through walls the sim lets his centre hug).
    private static readonly EnemyArchetype[] Table =
    {
        // MaxHealth, MoveSpeed, Damage, Range, Cooldown, Aggro, GuaranteedDrops, Radius, ProjectileSpeed
        new(SimConstants.EnemyMaxHealth, SimConstants.EnemyMoveSpeed,
            SimConstants.EnemyAttackDamage, SimConstants.EnemyAttackRange,
            SimConstants.EnemyAttackCooldown, 480f, 0, SimConstants.CharacterRadius), // Minion
        new(40f, 200f, 2f, 44f, 0.9f, 560f, 0, SimConstants.CharacterRadius),        // Rogue
        new(45f, 110f, 6f, 180f, 2.5f, 620f, 0, SimConstants.CharacterRadius, 460f), // Mage — fires a spell bolt
        new(300f, 105f, 12f, 70f, 2.0f, 520f, 3, 30f),                                // Boss — drops are guaranteed
    };

    public static EnemyArchetype Of(EnemyType type) => Table[(int)type];
}

namespace WoadRaiders.Core;

/// <summary>
/// Tunable constants for the simulation. Kept in one place so the server, the
/// client's prediction, and the tests all agree on the rules of the world.
/// </summary>
public static class SimConstants
{
    /// <summary>Fixed simulation ticks per second. The server steps at this rate.</summary>
    public const int TickRate = 30;

    /// <summary>Seconds represented by a single fixed tick.</summary>
    public const float TickDelta = 1f / TickRate;

    /// <summary>Player movement speed in world units per second.</summary>
    public const float PlayerMoveSpeed = 220f;

    /// <summary>Starting/max player health.</summary>
    public const float PlayerMaxHealth = 100f;

    /// <summary>Fallback arena (no geometry): extent from the origin on the X axis.</summary>
    public const float WorldHalfWidth = 960f;

    /// <summary>Fallback arena (no geometry): extent from the origin on the Z axis.</summary>
    public const float WorldHalfHeight = 540f;

    // --- Combat: player ---
    /// <summary>Reach of a player's melee strike; the enemy must be this close AND in front.</summary>
    public const float PlayerAttackRange = 72f;
    /// <summary>
    /// Minimum dot product between the player's facing and the direction to an enemy
    /// for a strike to connect — enforces direct, frontal contact rather than a 360°
    /// area sweep. 0.5 ≈ a 120° cone in front (cos 60°). Raise for a tighter arc.
    /// </summary>
    public const float PlayerAttackArcDot = 0.5f;
    public const float PlayerAttackDamage = 30f;
    /// <summary>Seconds between player attacks.</summary>
    public const float PlayerAttackCooldown = 0.4f;

    // --- Combat: enemy ---
    public const float EnemyMaxHealth = 60f;
    public const float EnemyMoveSpeed = 140f;
    /// <summary>How close an enemy must be to strike a player.</summary>
    public const float EnemyAttackRange = 44f;
    public const float EnemyAttackDamage = 3f;
    /// <summary>Seconds between enemy attacks.</summary>
    public const float EnemyAttackCooldown = 1.5f;

    // --- Loot ---
    /// <summary>
    /// Chance (0..1) that a slain common enemy drops equipment. Bosses always pay
    /// out equipment regardless (<see cref="EnemyArchetype.GuaranteedDrops"/>).
    /// </summary>
    public const double EquipmentDropChance = 0.50;

    /// <summary>Chance (0..1) that a slain enemy drops a pile of gold.</summary>
    public const double GoldDropChance = 0.75;

    /// <summary>Coins in a dropped gold pile (inclusive range).</summary>
    public const int GoldDropMin = 5;
    public const int GoldDropMax = 24;

    /// <summary>Chance (0..1) that a slain enemy drops a health potion.</summary>
    public const double PotionDropChance = 0.50;

    /// <summary>Health a potion restores when walked over (capped at max health).</summary>
    public const float PotionHealAmount = 30f;

    /// <summary>How close a player must be to auto-collect a ground item.</summary>
    public const float ItemPickupRadius = 40f;

    // --- Equipment ---
    /// <summary>Each point of equipped armor Power soaks this much of every incoming hit.</summary>
    public const float ArmorDamageReductionPerPower = 0.1f;

    /// <summary>How long the attack animation flag stays set after an attack lands.</summary>
    public const float AttackAnimDuration = 0.6f;

    // --- Character collision (vertical cylinder approximation) ---
    /// <summary>Character body radius on the XZ plane.</summary>
    public const float CharacterRadius = 14f;

    /// <summary>Character body height; solids above this (relative to the feet) don't block.</summary>
    public const float CharacterHeight = 44f;

    /// <summary>Height above the feet that sight lines are traced at (aggro / attack LOS).</summary>
    public const float EyeHeight = 26f;

    // --- Enemy aggro behaviour ---
    /// <summary>An aggroed enemy gives up when its target exceeds AggroRange x this (the leash).</summary>
    public const float EnemyLeashFactor = 1.6f;

    /// <summary>How close (units) a returning enemy must get to its post before it settles.</summary>
    public const float EnemyHomeEpsilon = 8f;

    // --- Projectiles (mage spell bolts) ---
    /// <summary>Bolt radius on the XZ plane (added to the target's radius for a hit).</summary>
    public const float ProjectileRadius = 10f;

    /// <summary>Seconds a bolt flies before fizzling (kills stray/dodged bolts).</summary>
    public const float ProjectileLifetime = 2.5f;
}

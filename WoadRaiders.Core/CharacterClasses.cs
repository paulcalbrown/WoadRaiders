namespace WoadRaiders.Core;

/// <summary>
/// The playable classes. Serialized as a byte in the join request and player
/// snapshots, so keep the numbering stable.
/// </summary>
public enum CharacterClass : byte
{
    Knight = 0, // armored line-holder: tough, steady sword work
    Rogue = 1,  // fast and fragile, knife-quick strikes
    Mage = 2,   // slow glass cannon, hurls spell bolts
    Ranger = 3, // skirmisher, rapid lighter bolts from range
}

/// <summary>
/// Per-class combat stats. Presentation (model, clips, colors) lives client-side.
/// <paramref name="AttackRange"/> gates melee cleaves only; a ranged class
/// (ProjectileSpeed &gt; 0) fires along its facing and the bolt's reach is bounded
/// by <see cref="SimConstants.ProjectileLifetime"/>, so for them the field is
/// advisory (UI stat bars).
/// </summary>
public readonly record struct ClassArchetype(
    float MaxHealth,
    float MoveSpeed,
    float AttackDamage,
    float AttackRange,
    float AttackCooldown,
    float ProjectileSpeed = 0f); // 0 = melee cleave; > 0 = fires a travelling projectile

/// <summary>
/// The stat table for every <see cref="CharacterClass"/>. Kept in Core so the
/// server, the client's prediction, and the tests all agree on the numbers.
/// </summary>
public static class ClassArchetypes
{
    // Knight deliberately mirrors the classic SimConstants player values, so the
    // existing balance (and the tests written against those constants) carry
    // over 1:1 — a class-less build and a knight are the same player.
    private static readonly ClassArchetype[] Table =
    {
        // MaxHealth, MoveSpeed, Damage, Range, Cooldown, ProjectileSpeed
        new(SimConstants.PlayerMaxHealth, SimConstants.PlayerMoveSpeed,
            SimConstants.PlayerAttackDamage, SimConstants.PlayerAttackRange,
            SimConstants.PlayerAttackCooldown),   // Knight
        new(80f, 260f, 22f, 64f, 0.25f),          // Rogue — shreds up close, dies fast
        new(70f, 200f, 26f, 420f, 0.8f, 520f),    // Mage — heavy bolts, ponderous cast
        new(85f, 235f, 18f, 560f, 0.45f, 700f),   // Ranger — quick light bolts, mobile
    };

    public static ClassArchetype Of(CharacterClass cls) => Table[(int)cls];
}

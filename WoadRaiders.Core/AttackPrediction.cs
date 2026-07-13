namespace WoadRaiders.Core;

/// <summary>
/// Client-side prediction of the local player's attack cadence. Mirrors the
/// server's cooldown so the swing animation starts instantly, on the same ticks
/// the server will trigger it, instead of waiting a round-trip for the snapshot
/// Attacking flag. Purely cosmetic — hits and damage stay server-authoritative.
/// Engine-free and tick-exact, so the cadence is unit-testable.
/// </summary>
public struct AttackPrediction
{
    private readonly float _classCooldown; // the class's seconds-between-attacks
    private float _cooldown; // mirrors the server's per-class cooldown timer
    private float _animTime; // predicted attack-anim window still open

    /// <summary>Predict a specific class's cadence (its per-attack cooldown).</summary>
    public AttackPrediction(float attackCooldown) : this() => _classCooldown = attackCooldown;

    /// <summary>True while the predicted swing animation window is open.</summary>
    public readonly bool Swinging => _animTime > 0f;

    // A default-constructed instance predicts the classic (knight) cadence, so
    // class-unaware callers and tests keep their old behavior.
    private readonly float Cooldown =>
        _classCooldown > 0f ? _classCooldown : SimConstants.PlayerAttackCooldown;

    /// <summary>
    /// Advance one fixed simulation tick with this tick's attack intent. The
    /// decrement-then-trigger order matches the server, so both fire an attack on
    /// the same tick of a held button. Returns true on the tick a new swing fires
    /// (the moment to lock in the aim), false otherwise.
    /// </summary>
    public bool Tick(bool attackHeld)
    {
        _cooldown = Math.Max(0f, _cooldown - SimConstants.TickDelta);
        _animTime = Math.Max(0f, _animTime - SimConstants.TickDelta);
        if (attackHeld && _cooldown <= 0f)
        {
            _animTime = SimConstants.AttackAnimDuration;
            _cooldown = Cooldown;
            return true;
        }
        return false;
    }
}

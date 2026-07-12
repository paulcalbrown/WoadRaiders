namespace WoadRaiders.Core;

/// <summary>
/// The "recently lost health" trail shared by the client's HUD bar and the enemy
/// billboard bars: on a hit the chip lingers at the pre-hit level for
/// <see cref="HoldTime"/>, then drains toward the real health fraction at
/// <see cref="DrainRate"/>; on a heal it snaps up immediately (no trailing chip
/// above the fill). Pure easing state — engine-free so the timing is unit-testable.
/// </summary>
public struct DamageChip
{
    /// <summary>Health-fraction per second the chip drains after a hit.</summary>
    public const float DrainRate = 0.8f;

    /// <summary>Seconds the chip lingers at the pre-hit level before it starts draining.</summary>
    public const float HoldTime = 0.35f;

    /// <summary>The fraction the chip currently shows (≥ the real health fraction).</summary>
    public float Fraction;

    private float _hold; // seconds left to linger before draining

    /// <summary>A chip resting at full health.</summary>
    public static DamageChip Full => new() { Fraction = 1f };

    /// <summary>Call when the tracked health just dropped: linger before draining.</summary>
    public void OnDamage() => _hold = HoldTime;

    /// <summary>
    /// Advance one frame toward <paramref name="target"/> (the real health
    /// fraction). A frame that finishes the hold does not also drain — the drain
    /// starts on the next frame, keeping the linger a full <see cref="HoldTime"/>.
    /// </summary>
    public void Advance(float target, float delta)
    {
        if (target >= Fraction)
        {
            Fraction = target; // healed or steady — no trailing chip
            return;
        }
        if (_hold > 0f)
        {
            _hold -= delta;
            return;
        }
        Fraction = Math.Max(target, Fraction - DrainRate * delta);
    }
}

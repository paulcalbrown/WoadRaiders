using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class AttackPredictionTests
{
    private static int TicksOf(float seconds) => (int)MathF.Ceiling(seconds / SimConstants.TickDelta);

    [Fact]
    public void First_press_swings_on_the_same_tick()
    {
        var attack = new AttackPrediction();
        attack.Tick(attackHeld: true);
        Assert.True(attack.Swinging);
    }

    [Fact]
    public void No_input_never_swings()
    {
        var attack = new AttackPrediction();
        for (var i = 0; i < 100; i++)
            attack.Tick(attackHeld: false);
        Assert.False(attack.Swinging);
    }

    [Fact]
    public void A_single_press_keeps_the_swing_window_open_for_the_anim_duration()
    {
        var attack = new AttackPrediction();
        attack.Tick(attackHeld: true);

        var swingTicks = 1; // the trigger tick itself
        while (attack.Swinging)
        {
            attack.Tick(attackHeld: false);
            if (attack.Swinging)
                swingTicks++;
        }

        Assert.Equal(TicksOf(SimConstants.AttackAnimDuration), swingTicks);
    }

    [Fact]
    public void A_held_button_retriggers_exactly_at_the_server_cooldown_cadence()
    {
        // The point of the mirror: the client must fire on the same ticks the server
        // will, or the predicted swing and the authoritative one drift apart. The
        // oracle is the server's own rule (GameWorld.TickCooldowns +
        // ResolvePlayerAttacks): step the cooldown down by Max(0, c - Δ) each tick
        // and fire on the first held tick where it reaches 0. Replicating that
        // float-exact sequence matters — naive ceil(cooldown / Δ) arithmetic is off
        // by one, because per-tick rounding can leave a hair of cooldown standing.
        var expectedGap = 0;
        for (var c = SimConstants.PlayerAttackCooldown; c > 0f; c = MathF.Max(0f, c - SimConstants.TickDelta))
            expectedGap++;

        var attack = new AttackPrediction();
        attack.Tick(attackHeld: true); // first trigger

        // The swing window (0.6 s) outlasts the cooldown (0.4 s), so Swinging alone
        // can't see retriggers; instead watch the remaining window, which decrements
        // between triggers and jumps back to full on each retrigger.
        var gaps = new List<int>();
        var lastRemaining = RemainingWindow(attack);
        var sinceTrigger = 0;
        for (var t = 0; t < 100; t++)
        {
            attack.Tick(attackHeld: true);
            sinceTrigger++;
            var remaining = RemainingWindow(attack);
            if (remaining > lastRemaining)
            {
                gaps.Add(sinceTrigger);
                sinceTrigger = 0;
            }
            lastRemaining = remaining;
        }

        Assert.True(gaps.Count >= 3, "expected several retriggers over 100 held ticks");
        Assert.All(gaps, gap => Assert.Equal(expectedGap, gap));
    }

    /// <summary>Ticks of swing window left. Probes a copy — AttackPrediction is a struct.</summary>
    private static int RemainingWindow(AttackPrediction attack)
    {
        var probe = attack;
        var ticks = 0;
        while (probe.Swinging)
        {
            probe.Tick(attackHeld: false);
            ticks++;
        }
        return ticks;
    }
}

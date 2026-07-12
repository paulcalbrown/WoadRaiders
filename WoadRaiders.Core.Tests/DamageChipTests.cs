using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class DamageChipTests
{
    private const float Frame = 1f / 60f;

    [Fact]
    public void Heals_snap_the_chip_up_immediately()
    {
        var chip = new DamageChip { Fraction = 0.3f };
        chip.Advance(target: 0.9f, Frame);
        Assert.Equal(0.9f, chip.Fraction);
    }

    [Fact]
    public void After_a_hit_the_chip_lingers_for_the_hold_time()
    {
        var chip = DamageChip.Full;
        chip.OnDamage();

        // Advance just under the hold window: the chip must not have moved.
        var elapsed = 0f;
        while (elapsed + Frame < DamageChip.HoldTime)
        {
            chip.Advance(target: 0.5f, Frame);
            elapsed += Frame;
        }
        Assert.Equal(1f, chip.Fraction);
    }

    [Fact]
    public void After_the_hold_the_chip_drains_at_the_drain_rate()
    {
        var chip = DamageChip.Full;

        // No OnDamage → no hold: draining starts immediately.
        chip.Advance(target: 0f, delta: 0.5f);
        Assert.Equal(1f - DamageChip.DrainRate * 0.5f, chip.Fraction, 3);
    }

    [Fact]
    public void The_chip_never_drains_below_the_target()
    {
        var chip = DamageChip.Full;
        chip.Advance(target: 0.9f, delta: 10f); // one huge frame
        Assert.Equal(0.9f, chip.Fraction);
    }

    [Fact]
    public void A_second_hit_during_the_drain_re_arms_the_hold()
    {
        var chip = DamageChip.Full;
        chip.Advance(target: 0.6f, delta: 0.25f); // draining, now at 0.8
        Assert.Equal(0.8f, chip.Fraction, 3);

        chip.OnDamage(); // second hit → linger again at the current level
        chip.Advance(target: 0.4f, DamageChip.HoldTime * 0.9f);
        Assert.Equal(0.8f, chip.Fraction, 3);
    }
}

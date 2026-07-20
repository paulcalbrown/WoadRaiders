using System;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class CharacterClassTests
{
    // Open, clamp-free space with clear lines of sight — combat-geometry neutral.
    private static GameWorld OpenWorld() => new()
    {
        Geometry = TestRealms.Open(),
    };

    [Fact]
    public void Knight_archetype_mirrors_the_classic_constants()
    {
        // The knight IS the pre-class player: same numbers, same behavior, so all
        // balance (and every older test) carries over 1:1.
        var knight = ClassArchetypes.Of(CharacterClass.Knight);
        Assert.Equal(SimConstants.PlayerMaxHealth, knight.MaxHealth);
        Assert.Equal(SimConstants.PlayerMoveSpeed, knight.MoveSpeed);
        Assert.Equal(SimConstants.PlayerAttackDamage, knight.AttackDamage);
        Assert.Equal(SimConstants.PlayerAttackRange, knight.AttackRange);
        Assert.Equal(SimConstants.PlayerAttackCooldown, knight.AttackCooldown);
        Assert.Equal(0f, knight.ProjectileSpeed); // melee
    }

    [Fact]
    public void Players_take_their_class_stats()
    {
        var world = new GameWorld();
        Assert.Equal(SimConstants.PlayerMaxHealth, world.AddPlayer(1, "A").Health, 3); // default = Knight
        Assert.Equal(ClassArchetypes.Of(CharacterClass.Mage).MaxHealth,
                     world.AddPlayer(2, "B", CharacterClass.Mage).Health, 3);
        Assert.Equal(CharacterClass.Ranger, world.AddPlayer(3, "C", CharacterClass.Ranger).Class);
    }

    [Fact]
    public void Rogue_outruns_a_knight()
    {
        var world = OpenWorld();
        var knight = world.AddPlayer(1, "K");
        var rogue = world.AddPlayer(2, "R", CharacterClass.Rogue);
        rogue.Position = new Vector3(0, 0, 200); // side by side, both running +X

        for (var t = 0; t < SimConstants.TickRate; t++) // a full second
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f });
            world.SetInput(2, new PlayerInput { MoveX = 1f });
            world.Step();
        }

        Assert.Equal(SimConstants.PlayerMoveSpeed, knight.Position.X, 1);
        Assert.Equal(ClassArchetypes.Of(CharacterClass.Rogue).MoveSpeed, rogue.Position.X, 1);
    }

    [Fact]
    public void Mage_attack_looses_a_bolt_that_travels_and_strikes_the_enemy()
    {
        var world = OpenWorld();
        world.AddPlayer(1, "M", CharacterClass.Mage);
        var enemy = world.SpawnEnemy(new Vector3(300, 0, 0)); // far outside any melee reach

        world.SetInput(1, new PlayerInput { Attack = true, AimX = 1f });
        world.Step();

        // The cast spawns a bolt aimed down the facing; no damage on the cast tick.
        var bolt = Assert.Single(world.Projectiles.Values);
        Assert.Equal(ProjectileKind.MagicBolt, bolt.Kind);
        Assert.False(bolt.HostileToPlayers);
        Assert.Equal(SimConstants.EnemyMaxHealth, enemy.Health, 3);

        // Let it fly; the standing enemy takes exactly one bolt of class damage.
        for (var t = 0; t < SimConstants.TickRate && enemy.Health >= SimConstants.EnemyMaxHealth; t++)
            world.Step();

        Assert.Equal(SimConstants.EnemyMaxHealth - ClassArchetypes.Of(CharacterClass.Mage).AttackDamage,
                     enemy.Health, 3);
        Assert.Empty(world.Projectiles); // consumed on impact
    }

    [Fact]
    public void Ranger_looses_arrows()
    {
        var world = OpenWorld();
        world.AddPlayer(1, "R", CharacterClass.Ranger);
        world.SpawnEnemy(new Vector3(300, 0, 0));

        world.SetInput(1, new PlayerInput { Attack = true, AimX = 1f });
        world.Step();

        Assert.Equal(ProjectileKind.Arrow, Assert.Single(world.Projectiles.Values).Kind);
    }

    [Fact]
    public void Player_bolts_pass_through_other_players()
    {
        var world = OpenWorld();
        world.AddPlayer(1, "M", CharacterClass.Mage);
        var ally = world.AddPlayer(2, "K");
        ally.Position = new Vector3(150, 0, 0); // squarely in the flight path

        world.SetInput(1, new PlayerInput { Attack = true, AimX = 1f });
        world.Step();
        Assert.Single(world.Projectiles);

        // Fly the bolt to the end of its life: through the ally, hitting no one.
        var lifetimeTicks = (int)(SimConstants.ProjectileLifetime / SimConstants.TickDelta) + 2;
        for (var t = 0; t < lifetimeTicks; t++)
        {
            world.SetInput(1, default); // stop attacking after the first bolt
            world.Step();
        }

        Assert.Empty(world.Projectiles); // fizzled, not consumed on the ally
        Assert.Equal(ally.MaxHealth, ally.Health, 3);
    }

    [Fact]
    public void Enemy_bolts_are_marked_hostile_to_players()
    {
        var world = OpenWorld();
        world.AddPlayer(1, "A");
        var standoff = EnemyArchetypes.Of(EnemyType.Mage).AttackRange - 20f;
        world.SpawnEnemy(new Vector3(standoff, 0, 0), EnemyType.Mage);

        world.Step(); // the skeleton casts

        var bolt = Assert.Single(world.Projectiles.Values);
        Assert.Equal(ProjectileKind.EnemyBolt, bolt.Kind);
        Assert.True(bolt.HostileToPlayers);
    }

    [Fact]
    public void Ranged_attacks_respect_the_class_cooldown()
    {
        var world = OpenWorld();
        world.AddPlayer(1, "R", CharacterClass.Ranger);

        // Hold the trigger for half the cooldown window: exactly one bolt exists.
        var cooldownTicks = (int)MathF.Ceiling(
            ClassArchetypes.Of(CharacterClass.Ranger).AttackCooldown / SimConstants.TickDelta);
        for (var t = 0; t < cooldownTicks / 2; t++)
        {
            world.SetInput(1, new PlayerInput { Attack = true, AimX = 1f });
            world.Step();
        }

        Assert.Single(world.Projectiles);
    }

    [Fact]
    public void Attack_prediction_matches_the_class_cadence()
    {
        // A rogue (0.25s) must re-fire sooner than a default-constructed (knight)
        // prediction — and each must re-fire on exactly its own cooldown boundary
        // (computed by the sim's own decrement rule, immune to float rounding).
        var rogueTicks = TicksBetweenSwings(new AttackPrediction(ClassArchetypes.Of(CharacterClass.Rogue).AttackCooldown));
        var knightTicks = TicksBetweenSwings(default);
        Assert.Equal(CooldownTicks(ClassArchetypes.Of(CharacterClass.Rogue).AttackCooldown), rogueTicks);
        Assert.Equal(CooldownTicks(SimConstants.PlayerAttackCooldown), knightTicks);
        Assert.True(rogueTicks < knightTicks, "the rogue's knife must come out faster than the knight's sword");
    }

    /// <summary>Ticks until a cooldown reaches zero under the sim's decrement-then-check rule.</summary>
    private static int CooldownTicks(float cooldown)
    {
        for (var t = 1; ; t++)
        {
            cooldown = Math.Max(0f, cooldown - SimConstants.TickDelta);
            if (cooldown <= 0f)
                return t;
        }
    }

    private static int TicksBetweenSwings(AttackPrediction attack)
    {
        Assert.True(attack.Tick(true)); // first swing fires immediately
        for (var t = 1; t < 10 * SimConstants.TickRate; t++)
            if (attack.Tick(true))
                return t;
        throw new InvalidOperationException("prediction never fired a second swing");
    }
}

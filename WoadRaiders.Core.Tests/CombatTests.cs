using System;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class CombatTests
{
    [Fact]
    public void Player_attack_damages_enemy_in_range()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A"); // at the origin, faces +X by default
        var enemy = world.SpawnEnemy(new Vector3(50, 0, 0)); // in front, inside PlayerAttackRange (72)

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        Assert.Equal(SimConstants.EnemyMaxHealth - SimConstants.PlayerAttackDamage, enemy.Health, 3);
    }

    [Fact]
    public void Attack_misses_an_enemy_behind_the_player()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");                              // faces +X by default
        var enemy = world.SpawnEnemy(new Vector3(-50, 0, 0)); // directly behind, but in reach

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        Assert.Equal(SimConstants.EnemyMaxHealth, enemy.Health, 3); // untouched — not in front
    }

    [Fact]
    public void Attack_cleaves_every_enemy_in_the_arc()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A"); // faces +X
        var near = world.SpawnEnemy(new Vector3(40, 0, 0));
        var far = world.SpawnEnemy(new Vector3(65, 0, 0));    // also in front and in reach
        var behind = world.SpawnEnemy(new Vector3(-50, 0, 0)); // in reach but behind → spared

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        var damaged = SimConstants.EnemyMaxHealth - SimConstants.PlayerAttackDamage;
        Assert.Equal(damaged, near.Health, 3);
        Assert.Equal(damaged, far.Health, 3);                        // both front enemies hit — cleave
        Assert.Equal(SimConstants.EnemyMaxHealth, behind.Health, 3); // still arc-limited, not 360°
    }

    [Fact]
    public void Facing_tracks_the_last_movement_direction()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");

        world.SetInput(1, new PlayerInput { MoveX = -1f }); // steer left
        world.Step();

        Assert.Equal(new Vector3(-1, 0, 0), player.Facing); // now a strike lands to the left, not +X
    }

    [Fact]
    public void Attack_faces_the_aim_and_strikes_that_way()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A"); // faces +X by default
        var behind = world.SpawnEnemy(new Vector3(-50, 0, 0)); // behind the default facing, in reach

        // Aim behind (-X) and attack: the swing should turn to the aim and land on the enemy
        // that the default +X facing would have missed.
        world.SetInput(1, new PlayerInput { Attack = true, AimX = -1f, AimZ = 0f });
        world.Step();

        Assert.Equal(new Vector3(-1, 0, 0), player.Facing);
        Assert.Equal(SimConstants.EnemyMaxHealth - SimConstants.PlayerAttackDamage, behind.Health, 3);
    }

    [Fact]
    public void A_zero_aim_leaves_the_movement_facing_intact()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");
        world.SetInput(1, new PlayerInput { MoveX = -1f }); // face left
        world.Step();

        // Attack with no aim (0,0): facing must stay where movement left it, not snap to +X.
        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        Assert.Equal(new Vector3(-1, 0, 0), player.Facing);
    }

    [Fact]
    public void Attacking_roots_the_player()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A"); // origin

        // Try to run right while swinging — the swing should hold the player in place.
        world.SetInput(1, new PlayerInput { MoveX = 1f, Attack = true });
        world.Step();

        Assert.Equal(Vector3.Zero, player.Position);
    }

    [Fact]
    public void Held_movement_intent_resumes_once_the_swing_animation_ends()
    {
        // The sim root is scoped to the swing: continuous intent (a held key) flows
        // again once the animation ends. Cancelling a one-shot click-to-move order on
        // attack is a separate, client-side concern (LocalPlayer), not the sim's.
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step(); // fire a swing → rooted for the animation window

        // Hold right without attacking: rooted until the swing anim expires, then moves.
        var animTicks = (int)Math.Ceiling(SimConstants.AttackAnimDuration / SimConstants.TickDelta);
        for (var t = 0; t < animTicks + 5; t++)
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f });
            world.Step();
        }

        Assert.True(player.Position.X > 0f, "held movement should resume once the swing animation ended");
    }

    [Fact]
    public void Player_attack_misses_enemy_out_of_range()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");
        var enemy = world.SpawnEnemy(new Vector3(500, 0, 0)); // well beyond range

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        Assert.Equal(SimConstants.EnemyMaxHealth, enemy.Health, 3);
    }

    [Fact]
    public void Attack_respects_cooldown()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");
        var enemy = world.SpawnEnemy(new Vector3(40, 0, 0));

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();
        var afterFirstHit = enemy.Health;

        world.Step(); // still holding attack, but on cooldown — no second hit
        Assert.Equal(afterFirstHit, enemy.Health, 3);
    }

    [Fact]
    public void Enough_hits_kill_and_remove_the_enemy()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");
        world.SpawnEnemy(new Vector3(40, 0, 0));

        var cooldownTicks = (int)Math.Ceiling(SimConstants.PlayerAttackCooldown / SimConstants.TickDelta) + 1;
        var hitsNeeded = (int)Math.Ceiling(SimConstants.EnemyMaxHealth / SimConstants.PlayerAttackDamage);

        for (var hit = 0; hit < hitsNeeded; hit++)
        {
            world.SetInput(1, new PlayerInput { Attack = true });
            world.Step();
            world.SetInput(1, new PlayerInput { Attack = false });
            for (var t = 0; t < cooldownTicks; t++)
                world.Step();
        }

        Assert.Empty(world.Enemies);
    }

    [Fact]
    public void Enemy_chases_and_damages_a_still_player()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A"); // origin, no input → stands still
        var enemy = world.SpawnEnemy(new Vector3(60, 0, 0)); // just outside attack range (44)

        var maxTicks = SimConstants.TickRate * 3;
        for (var t = 0; t < maxTicks && player.Health >= SimConstants.PlayerMaxHealth; t++)
            world.Step();

        Assert.True(player.Health < SimConstants.PlayerMaxHealth, "enemy should have hit the player");
        Assert.True(enemy.Position.X < 60f, "enemy should have moved toward the player");
    }

    [Fact]
    public void Attacking_sets_then_clears_the_animation_flag()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();
        Assert.True(player.IsAttacking); // flag raised on the swing

        world.SetInput(1, new PlayerInput { Attack = false });
        var ticks = (int)System.Math.Ceiling(SimConstants.AttackAnimDuration / SimConstants.TickDelta) + 1;
        for (var i = 0; i < ticks; i++)
            world.Step();

        Assert.False(player.IsAttacking); // and lowered after the window
    }

    [Fact]
    public void Dead_player_respawns_at_full_health()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");
        player.TakeDamage(SimConstants.PlayerMaxHealth); // down to zero
        Assert.False(player.IsAlive);

        world.Step(); // the respawn pass runs

        Assert.True(player.IsAlive);
        Assert.Equal(SimConstants.PlayerMaxHealth, player.Health, 3);
        Assert.Equal(Vector3.Zero, player.Position);
    }
}

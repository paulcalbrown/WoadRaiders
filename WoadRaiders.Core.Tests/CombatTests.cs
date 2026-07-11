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
        world.AddPlayer(1, "A"); // at the origin
        var enemy = world.SpawnEnemy(new Vector2(50, 0)); // inside PlayerAttackRange (96)

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        Assert.Equal(SimConstants.EnemyMaxHealth - SimConstants.PlayerAttackDamage, enemy.Health, 3);
    }

    [Fact]
    public void Player_attack_misses_enemy_out_of_range()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");
        var enemy = world.SpawnEnemy(new Vector2(500, 0)); // well beyond range

        world.SetInput(1, new PlayerInput { Attack = true });
        world.Step();

        Assert.Equal(SimConstants.EnemyMaxHealth, enemy.Health, 3);
    }

    [Fact]
    public void Attack_respects_cooldown()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");
        var enemy = world.SpawnEnemy(new Vector2(40, 0));

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
        world.SpawnEnemy(new Vector2(40, 0));

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
        var enemy = world.SpawnEnemy(new Vector2(60, 0)); // just outside attack range (44)

        var maxTicks = SimConstants.TickRate * 3;
        for (var t = 0; t < maxTicks && player.Health >= SimConstants.PlayerMaxHealth; t++)
            world.Step();

        Assert.True(player.Health < SimConstants.PlayerMaxHealth, "enemy should have hit the player");
        Assert.True(enemy.Position.X < 60f, "enemy should have moved toward the player");
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
        Assert.Equal(Vector2.Zero, player.Position);
    }
}

using System;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class SpawnDirectorTests
{
    // Five markers spread well away from the origin player spawn, plus a boss chamber.
    private static DungeonGeometry MakeDungeon() => new(
        Vector3.Zero,
        null,
        new[]
        {
            new EnemySpawnPoint(new Vector3(400, 0, 0), EnemyType.Minion),
            new EnemySpawnPoint(new Vector3(0, 0, 400), EnemyType.Rogue),
            new EnemySpawnPoint(new Vector3(-400, 0, 0), EnemyType.Mage),
            new EnemySpawnPoint(new Vector3(0, 0, -400), EnemyType.Minion),
            new EnemySpawnPoint(new Vector3(400, 0, 400), EnemyType.Rogue),
        })
    {
        BossSpawn = new Vector3(900, 0, 900),
    };

    private static SpawnDirector Directed(out GameWorld world, DungeonGeometry? dungeon = null, int seed = 1)
    {
        world = new GameWorld();
        return new SpawnDirector(world, dungeon ?? MakeDungeon(), new Random(seed));
    }

    [Fact]
    public void Initial_spawn_places_one_enemy_per_marker_plus_the_boss()
    {
        var director = Directed(out var world);

        var spawned = director.SpawnInitial();

        Assert.Equal(5, director.TargetEnemyCount);
        Assert.Equal(5, spawned);
        Assert.Equal(5, world.Enemies.Values.Count(e => e.Type != EnemyType.Boss));
        Assert.Single(world.Enemies.Values, e => e.Type == EnemyType.Boss);
        // The authored mix is exact: the marker types are reproduced.
        Assert.Equal(2, world.Enemies.Values.Count(e => e.Type == EnemyType.Rogue));
    }

    [Fact]
    public void Target_count_is_clamped_up_for_sparse_maps()
    {
        var sparse = new DungeonGeometry(Vector3.Zero, null,
            new[] { new EnemySpawnPoint(new Vector3(400, 0, 0), EnemyType.Minion) }); // 1 marker
        var director = new SpawnDirector(new GameWorld(), sparse, new Random(1));

        Assert.Equal(4, director.TargetEnemyCount); // clamped up to the floor of 4
    }

    [Fact]
    public void Population_is_topped_back_up_after_an_enemy_dies()
    {
        var director = Directed(out var world);
        director.SpawnInitial();

        // Kill one regular; Step removes it, leaving us one under target.
        var victim = world.Enemies.Values.First(e => e.Type != EnemyType.Boss);
        victim.TakeDamage(victim.MaxHealth);
        world.Step();
        director.Update();
        Assert.Equal(4, world.Enemies.Values.Count(e => e.Type != EnemyType.Boss));

        // Nothing respawns before the interval elapses...
        for (var t = 0; t < SpawnDirector.RepopIntervalTicks - 2; t++) { world.Step(); director.Update(); }
        Assert.Equal(4, world.Enemies.Values.Count(e => e.Type != EnemyType.Boss));

        // ...and exactly one does once it passes.
        for (var t = 0; t < 4; t++) { world.Step(); director.Update(); }
        Assert.Equal(5, world.Enemies.Values.Count(e => e.Type != EnemyType.Boss));
    }

    [Fact]
    public void Repopulation_never_overshoots_the_target()
    {
        var director = Directed(out var world);
        director.SpawnInitial();

        // Run for many repop intervals with a full population — nothing should be added.
        for (var t = 0; t < SpawnDirector.RepopIntervalTicks * 5; t++) { world.Step(); director.Update(); }

        Assert.Equal(5, world.Enemies.Values.Count(e => e.Type != EnemyType.Boss));
    }

    [Fact]
    public void Boss_respawns_exactly_after_the_delay_and_raises_its_events()
    {
        var director = Directed(out var world);
        director.SpawnInitial();

        int fell = 0, rose = 0;
        director.BossFell += () => fell++;
        director.BossRose += () => rose++;

        // Slay the boss.
        var boss = world.Enemies.Values.First(e => e.Type == EnemyType.Boss);
        boss.TakeDamage(boss.MaxHealth);
        world.Step();
        director.Update();

        Assert.True(director.BossIsDown);
        Assert.Equal(1, fell);
        Assert.DoesNotContain(world.Enemies.Values, e => e.Type == EnemyType.Boss);

        // Not a tick early.
        for (var t = 0; t < SpawnDirector.BossRespawnDelayTicks - 2; t++) { world.Step(); director.Update(); }
        Assert.DoesNotContain(world.Enemies.Values, e => e.Type == EnemyType.Boss);
        Assert.Equal(0, rose);

        // ...and back on schedule.
        for (var t = 0; t < 4; t++) { world.Step(); director.Update(); }
        Assert.False(director.BossIsDown);
        Assert.Equal(1, rose);
        Assert.Single(world.Enemies.Values, e => e.Type == EnemyType.Boss);
    }

    [Fact]
    public void Regular_respawns_stay_clear_of_the_player_spawn()
    {
        // Four markers: three hugging the spawn, one far. The initial spawn uses all
        // four (authored), but a random respawn must avoid the near ones.
        var dungeon = new DungeonGeometry(Vector3.Zero, null, new[]
        {
            new EnemySpawnPoint(new Vector3(30, 0, 0), EnemyType.Minion),   // < 200 from spawn
            new EnemySpawnPoint(new Vector3(0, 0, 30), EnemyType.Minion),   // < 200
            new EnemySpawnPoint(new Vector3(30, 0, 30), EnemyType.Minion),  // < 200
            new EnemySpawnPoint(new Vector3(500, 0, 500), EnemyType.Rogue), // > 200 — the only safe respawn
        });
        var world = new GameWorld();
        var director = new SpawnDirector(world, dungeon, new Random(3));
        director.SpawnInitial();

        // Kill a near enemy so the director must top the population up.
        var victim = world.Enemies.Values.First(e => e.Position.X == 30 && e.Position.Z == 0);
        var before = world.Enemies.Keys.ToHashSet();
        victim.TakeDamage(victim.MaxHealth);
        world.Step();

        for (var t = 0; t < SpawnDirector.RepopIntervalTicks + 2; t++) { world.Step(); director.Update(); }

        var added = world.Enemies.Values.Single(e => !before.Contains(e.Id));
        Assert.True(Vector3.DistanceSquared(added.Position, Vector3.Zero)
                    > SpawnDirector.MinRespawnDistanceFromPlayerSpawn * SpawnDirector.MinRespawnDistanceFromPlayerSpawn,
                    $"respawn should avoid the player spawn (landed at {added.Position})");
        Assert.Equal(new Vector3(500, 0, 500), added.Position);
    }
}

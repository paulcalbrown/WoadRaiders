using System;
using System.IO;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class EnemyTypeTests
{
    [Fact]
    public void Enemies_take_their_archetype_stats()
    {
        var world = new GameWorld();
        Assert.Equal(SimConstants.EnemyMaxHealth, world.SpawnEnemy(Vector3.Zero).Health, 3); // default = Minion
        Assert.Equal(EnemyArchetypes.Of(EnemyType.Boss).MaxHealth,
                     world.SpawnEnemy(Vector3.Zero, EnemyType.Boss).Health, 3);
        Assert.True(EnemyArchetypes.Of(EnemyType.Boss).MaxHealth > 5 * SimConstants.EnemyMaxHealth,
                    "a boss should be an order tougher than a minion");
    }

    [Fact]
    public void Enemy_beyond_aggro_range_stands_idle()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A"); // origin, standing still
        var beyond = EnemyArchetypes.Of(EnemyType.Minion).AggroRange + 50f;
        var enemy = world.SpawnEnemy(new Vector3(beyond, 0, 0));

        for (var t = 0; t < SimConstants.TickRate; t++) // a full second
            world.Step();

        Assert.Equal(beyond, enemy.Position.X, 3); // never moved — no aggro
    }

    [Fact]
    public void Enemy_inside_aggro_range_chases()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");
        var inside = EnemyArchetypes.Of(EnemyType.Minion).AggroRange - 80f;
        var enemy = world.SpawnEnemy(new Vector3(inside, 0, 0));

        world.Step();

        Assert.True(enemy.Position.X < inside, "enemy inside aggro range should close in");
    }

    [Fact]
    public void Mage_fires_a_bolt_that_travels_and_then_hits()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");
        var arch = EnemyArchetypes.Of(EnemyType.Mage);
        var standoff = arch.AttackRange - 20f; // inside cast range, far outside melee range
        var mage = world.SpawnEnemy(new Vector3(standoff, 0, 0), EnemyType.Mage);

        world.Step(); // mage casts: a bolt spawns, no damage yet
        Assert.Single(world.Projectiles);
        Assert.Equal(SimConstants.PlayerMaxHealth, player.Health, 3); // damage is on impact, not the cast
        Assert.Equal(standoff, mage.Position.X, 3);                   // cast from where it stood
        Assert.True(mage.IsAttacking);                               // and plays its cast animation

        // Let the bolt fly across the gap; the standing player eventually takes the hit.
        for (var t = 0; t < SimConstants.TickRate && player.Health >= SimConstants.PlayerMaxHealth; t++)
            world.Step();

        Assert.Equal(SimConstants.PlayerMaxHealth - arch.AttackDamage, player.Health, 3);
        Assert.Empty(world.Projectiles); // the bolt is consumed on impact
    }

    [Fact]
    public void Bolt_dodged_by_stepping_aside_misses_and_fizzles()
    {
        // Empty geometry = open, but no arena clamp (so the dodge sticks) and clear LOS.
        var world = new GameWorld
        {
            Geometry = new DungeonGeometry(Vector3.Zero, Array.Empty<Aabb>(), Array.Empty<Vector3>()),
        };
        var player = world.AddPlayer(1, "A"); // origin
        world.SpawnEnemy(new Vector3(160, 0, 0), EnemyType.Mage);

        world.Step(); // fires a bolt aimed down the X axis at the origin
        Assert.Single(world.Projectiles);

        // Player blinks far off the bolt's line AND past the mage's leash, so the mage
        // gives up rather than chasing and re-firing.
        player.Position = new Vector3(0, 0, 1100);

        for (var t = 0; t < SimConstants.TickRate * 3; t++)
            world.Step();

        Assert.Equal(SimConstants.PlayerMaxHealth, player.Health, 3); // clean miss
        Assert.Empty(world.Projectiles);                              // and it eventually fizzled
    }

    [Fact]
    public void Bolt_is_stopped_by_a_wall()
    {
        // Wall BEYOND the player, so the mage has a clear shot to fire, but the bolt
        // (which flies on past its target) runs into the wall.
        var wall = new Aabb(new Vector3(150, 0, -200), new Vector3(170, 80, 200));
        var world = new GameWorld
        {
            Geometry = new DungeonGeometry(Vector3.Zero, new[] { wall }, Array.Empty<Vector3>()),
        };
        var mage = world.SpawnEnemy(Vector3.Zero, EnemyType.Mage);
        var player = world.AddPlayer(1, "A");
        player.Position = new Vector3(100, 0, 0); // clear LOS from the mage (wall is farther east)

        world.Step(); // fires a bolt east, toward the player and the wall behind it
        Assert.Single(world.Projectiles);
        player.Position = new Vector3(100, 0, 300); // step off the bolt's line

        for (var t = 0; t < SimConstants.TickRate * 2; t++)
            world.Step();

        Assert.Equal(SimConstants.PlayerMaxHealth, player.Health, 3); // the wall ate the bolt
        Assert.Empty(world.Projectiles);
    }

    [Fact]
    public void Boss_always_drops_its_guaranteed_loot()
    {
        var world = new GameWorld(new Random(7));
        world.AddPlayer(1, "A"); // origin — far from the drops, so nothing is auto-collected
        var boss = world.SpawnEnemy(new Vector3(600, 0, 600), EnemyType.Boss);

        boss.TakeDamage(boss.MaxHealth);
        world.Step();

        Assert.Empty(world.Enemies);
        Assert.Equal(EnemyArchetypes.Of(EnemyType.Boss).GuaranteedDrops, world.GroundItems.Count);
    }

    [Fact]
    public void Geometry_json_round_trips_types_and_boss()
    {
        var geometry = new DungeonGeometry(
            new Vector3(1, 0, 2),
            new[] { new Aabb(Vector3.Zero, Vector3.One) },
            new[]
            {
                new EnemySpawnPoint(new Vector3(10, 0, 0), EnemyType.Minion),
                new EnemySpawnPoint(new Vector3(20, 0, 0), EnemyType.Rogue),
                new EnemySpawnPoint(new Vector3(30, 0, 0), EnemyType.Mage),
            })
        {
            BossSpawn = new Vector3(99, 0, 42),
        };

        var parsed = DungeonGeometryFile.Parse(DungeonGeometryFile.ToJson(geometry));

        Assert.Equal(geometry.TypedEnemySpawns, parsed.TypedEnemySpawns);
        Assert.Equal(geometry.BossSpawn, parsed.BossSpawn);
    }

    [Fact]
    public void Enemy_cannot_see_or_strike_through_a_wall()
    {
        // A wall between a mage and a player: solid x in [70,90], full height, wide in z.
        var wall = new Aabb(new Vector3(70, 0, -200), new Vector3(90, 80, 200));
        var world = new GameWorld
        {
            Geometry = new DungeonGeometry(Vector3.Zero, new[] { wall }, Array.Empty<Vector3>()),
        };
        var player = world.AddPlayer(1, "A"); // at the origin, west of the wall
        var standoff = EnemyArchetypes.Of(EnemyType.Mage).AttackRange - 20f;
        var mage = world.SpawnEnemy(new Vector3(standoff, 0, 0), EnemyType.Mage); // east of the wall

        world.Step();

        Assert.Equal(SimConstants.PlayerMaxHealth, player.Health, 3); // no zap through the wall
        Assert.False(mage.Aggroed);                                    // and no aggro through it either
    }

    [Fact]
    public void Enemy_returns_to_its_post_after_losing_aggro()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");
        var arch = EnemyArchetypes.Of(EnemyType.Minion);
        var home = new Vector3(400f, 0, 0); // inside aggro range of the origin
        var enemy = world.SpawnEnemy(home);

        world.Step(); // aggro + start chasing
        Assert.True(enemy.Aggroed);
        Assert.True(enemy.Position.X < home.X);

        // The player blinks beyond the leash (e.g. died and respawned across the map).
        // Stay inside the open-arena clamp (±WorldHalfWidth) — the fallback arena
        // would silently clamp a farther teleport back inside the leash.
        player.Position = new Vector3(-520f, 0, 0); // ~920 from the enemy > 480 * 1.6
        world.Step();
        Assert.False(enemy.Aggroed);

        for (var t = 0; t < SimConstants.TickRate * 5 && // walk home (plenty of time)
             Vector3.Distance(enemy.Position, home) > SimConstants.EnemyHomeEpsilon; t++)
            world.Step();

        Assert.True(Vector3.Distance(enemy.Position, home) <= SimConstants.EnemyHomeEpsilon,
                    $"enemy should have walked back to its post (got {enemy.Position})");
    }

    [Fact]
    public void Boss_collides_with_its_wider_radius()
    {
        var wall = new Aabb(new Vector3(100, 0, -200), new Vector3(120, 80, 200));
        var geo = new DungeonGeometry(Vector3.Zero, new[] { wall }, Array.Empty<Vector3>());
        var nearWall = new Vector3(100 - 20f, 0, 0); // 20 units from the wall face

        Assert.False(geo.IsBlocked(nearWall));                                       // fine for a regular character (radius 14)
        Assert.True(geo.IsBlocked(nearWall, EnemyArchetypes.Of(EnemyType.Boss).Radius)); // too close for the boss (radius 30)
    }

    [Fact]
    public void Loader_rejects_boss_and_out_of_range_spawn_types()
    {
        static string Json(int type) => $$"""
            { "spawn": [0,0,0],
              "solids": [ { "min": [0,0,0], "max": [1,1,1] } ],
              "enemySpawns": [ [5,0,5] ],
              "enemySpawnTypes": [ {{type}} ] }
            """;

        Assert.Throws<InvalidDataException>(() => DungeonGeometryFile.Parse(Json((int)EnemyType.Boss))); // bosses use bossSpawn
        Assert.Throws<InvalidDataException>(() => DungeonGeometryFile.Parse(Json(259))); // must not wrap modulo 256 into "Boss"
        Assert.Throws<InvalidDataException>(() => DungeonGeometryFile.Parse(Json(-1)));
        _ = DungeonGeometryFile.Parse(Json((int)EnemyType.Mage)); // 0..2 stay valid
    }

    [Fact]
    public void Geometry_json_without_types_defaults_to_minions()
    {
        // Old-schema maps (TestArena.json) must keep loading unchanged.
        const string legacy = """
            { "spawn": [0,0,0],
              "solids": [ { "min": [0,0,0], "max": [1,1,1] } ],
              "enemySpawns": [ [5,0,5], [9,0,9] ] }
            """;

        var parsed = DungeonGeometryFile.Parse(legacy);

        Assert.Null(parsed.BossSpawn);
        Assert.All(parsed.TypedEnemySpawns, s => Assert.Equal(EnemyType.Minion, s.Type));
        Assert.Equal(2, parsed.TypedEnemySpawns.Count);
    }
}

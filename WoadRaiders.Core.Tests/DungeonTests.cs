using System;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class DungeonTests
{
    [Fact]
    public void Generation_is_deterministic_for_a_seed()
    {
        var a = DungeonGenerator.Generate(12345);
        var b = DungeonGenerator.Generate(12345);

        Assert.Equal(a.SpawnPoint, b.SpawnPoint);
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
            Assert.Equal(a.IsFloorTile(x, y), b.IsFloorTile(x, y));
    }

    [Fact]
    public void Different_seeds_produce_different_layouts()
    {
        var a = DungeonGenerator.Generate(1);
        var b = DungeonGenerator.Generate(2);

        var identical = true;
        for (var y = 0; y < a.Height && identical; y++)
        for (var x = 0; x < a.Width && identical; x++)
            if (a.IsFloorTile(x, y) != b.IsFloorTile(x, y))
                identical = false;

        Assert.False(identical);
    }

    [Fact]
    public void Spawn_point_is_on_floor()
    {
        var map = DungeonGenerator.Generate(777);
        Assert.True(map.IsWalkable(map.SpawnPoint));
    }

    [Fact]
    public void Dungeon_has_floor_and_a_wall_border()
    {
        var map = DungeonGenerator.Generate(3);
        Assert.True(map.FloorCount > 0);
        Assert.False(map.IsFloorTile(0, 0)); // border is always wall
    }

    [Fact]
    public void Random_floor_position_is_always_walkable()
    {
        var map = DungeonGenerator.Generate(42);
        var rng = new Random(1);
        for (var i = 0; i < 50; i++)
            Assert.True(map.IsWalkable(map.RandomFloorPosition(rng)));
    }

    [Fact]
    public void Dungeon_is_a_single_connected_region()
    {
        var map = DungeonGenerator.Generate(2024);
        var (sx, sy) = map.WorldToTile(map.SpawnPoint);

        // Flood-fill floor from spawn; every floor tile must be reachable.
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();
        queue.Enqueue((sx, sy));
        seen.Add((sx, sy));
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                if (map.IsFloorTile(nx, ny) && seen.Add((nx, ny)))
                    queue.Enqueue((nx, ny));
        }

        Assert.Equal(map.FloorCount, seen.Count);
    }

    [Fact]
    public void Walls_block_player_movement()
    {
        // Hand-built 3×3 map: only the centre tile (1,1) is floor.
        var floor = new bool[9];
        floor[1 * 3 + 1] = true;
        var map = new DungeonMap(3, 3, 10f, floor, spawnPoint: Vector2.Zero);

        var world = new GameWorld(new Random(1)) { Map = map };
        var player = world.AddPlayer(1, "A");
        player.Position = map.SpawnPoint; // centre, world (0,0)

        world.SetInput(1, new PlayerInput { MoveX = 1f }); // barge into the wall
        for (var i = 0; i < 30; i++)
            world.Step();

        Assert.True(map.IsWalkable(player.Position));
        Assert.True(player.Position.X < map.TileSize); // never crossed into the wall tile
    }
}

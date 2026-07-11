using System;
using System.Collections.Generic;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class DungeonGeometryTests
{
    private static DungeonGeometry WallEast() => new(
        spawnPoint: Vector3.Zero,
        solids: new[]
        {
            // A wall plane at x ∈ [60, 100], full height, spanning far along z.
            new Aabb(new Vector3(60, 0, -2000), new Vector3(100, 70, 2000)),
        },
        enemySpawns: Array.Empty<Vector3>());

    [Fact]
    public void Wall_blocks_movement()
    {
        var geo = WallEast();
        var pos = Vector3.Zero;

        for (var i = 0; i < 100; i++)
            pos = geo.Move(pos, new Vector3(10, 0, 0)); // barge east into the wall

        Assert.False(geo.IsBlocked(pos));
        Assert.True(pos.X <= 60 - SimConstants.CharacterRadius + 0.01f,
            $"expected to stop at the wall face, got X={pos.X}");
    }

    [Fact]
    public void Movement_slides_along_wall()
    {
        var geo = WallEast();
        var pos = Vector3.Zero;

        for (var i = 0; i < 50; i++)
            pos = geo.Move(pos, new Vector3(10, 0, 10)); // diagonal into the wall

        Assert.True(pos.X < 60, "X advance must stop at the wall");
        Assert.True(pos.Z > 300f, $"Z should keep sliding along the wall, got Z={pos.Z}");
    }

    [Fact]
    public void Overhead_beam_does_not_block()
    {
        var geo = new DungeonGeometry(
            Vector3.Zero,
            new[]
            {
                // A beam above head height (character height is 44).
                new Aabb(new Vector3(-50, 60, -50), new Vector3(50, 80, 50)),
            },
            Array.Empty<Vector3>());

        Assert.False(geo.IsBlocked(Vector3.Zero));
        var pos = geo.Move(new Vector3(-80, 0, 0), new Vector3(30, 0, 0));
        Assert.Equal(-50f, pos.X, 3); // walked freely under the beam
    }

    [Fact]
    public void Floor_box_under_feet_does_not_block()
    {
        var geo = new DungeonGeometry(
            Vector3.Zero,
            new[]
            {
                // A floor slab whose top is exactly at the feet (y = 0).
                new Aabb(new Vector3(-100, -10, -100), new Vector3(100, 0, 100)),
            },
            Array.Empty<Vector3>());

        Assert.False(geo.IsBlocked(Vector3.Zero));
    }

    [Fact]
    public void Json_round_trips()
    {
        var original = new DungeonGeometry(
            new Vector3(1, 2, 3),
            new[]
            {
                new Aabb(new Vector3(-10, 0, -10), new Vector3(10, 70, 10)),
                new Aabb(new Vector3(50, 0, 50), new Vector3(90, 35, 60)),
            },
            new List<Vector3> { new(5, 0, 5), new(-7, 0, 9) })
        {
            ScenePath = "res://maps/Test.tscn",
        };

        var restored = DungeonGeometryFile.Parse(DungeonGeometryFile.ToJson(original));

        Assert.Equal(original.SpawnPoint, restored.SpawnPoint);
        Assert.Equal(original.ScenePath, restored.ScenePath);
        Assert.Equal(original.Solids.Count, restored.Solids.Count);
        for (var i = 0; i < original.Solids.Count; i++)
            Assert.Equal(original.Solids[i], restored.Solids[i]);
        Assert.Equal(original.EnemySpawns, restored.EnemySpawns);
    }

    [Fact]
    public void Json_without_scene_path_parses_as_null()
    {
        var geo = new DungeonGeometry(Vector3.Zero,
            new[] { new Aabb(Vector3.Zero, new Vector3(1, 1, 1)) }, Array.Empty<Vector3>());

        var restored = DungeonGeometryFile.Parse(DungeonGeometryFile.ToJson(geo));

        Assert.Null(restored.ScenePath);
    }

    [Fact]
    public void GameWorld_uses_geometry_for_collision()
    {
        var world = new GameWorld(new Random(1)) { Geometry = WallEast() };
        var player = world.AddPlayer(1, "A");

        world.SetInput(1, new PlayerInput { MoveX = 1f }); // barge into the wall
        for (var i = 0; i < 120; i++)
            world.Step();

        Assert.True(player.Position.X <= 60 - SimConstants.CharacterRadius + 0.01f);
    }
}

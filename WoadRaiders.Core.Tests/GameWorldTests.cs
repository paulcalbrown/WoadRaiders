using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

public class GameWorldTests
{
    [Fact]
    public void Step_moves_player_right_at_move_speed()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");
        world.SetInput(1, new PlayerInput { MoveX = 1f, MoveY = 0f });

        world.Step();

        var expected = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
        Assert.Equal(expected, player.Position.X, 3);
        Assert.Equal(0f, player.Position.Y, 3);
        Assert.Equal(1, world.Tick);
    }

    [Fact]
    public void Diagonal_input_is_not_faster_than_cardinal()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");
        world.SetInput(1, new PlayerInput { MoveX = 1f, MoveY = 1f });

        world.Step();

        var perTick = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
        // Normalised diagonal: total displacement equals one tick of move speed.
        Assert.Equal(perTick, player.Position.Length(), 3);
    }

    [Fact]
    public void Player_is_clamped_to_world_bounds()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");
        world.SetInput(1, new PlayerInput { MoveX = 1f });

        for (var i = 0; i < 10_000; i++)
            world.Step();

        Assert.Equal(SimConstants.WorldHalfWidth, player.Position.X, 2);
    }

    [Fact]
    public void No_input_leaves_player_still()
    {
        var world = new GameWorld();
        var player = world.AddPlayer(1, "A");

        world.Step();

        Assert.Equal(Vector2.Zero, player.Position);
    }

    [Fact]
    public void Simulation_is_deterministic()
    {
        static Vector2 Run()
        {
            var world = new GameWorld();
            world.AddPlayer(1, "A");
            world.SetInput(1, new PlayerInput { MoveX = 0.3f, MoveY = -0.7f });
            for (var i = 0; i < 100; i++)
                world.Step();
            return world.Players[1].Position;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Removed_player_leaves_the_world()
    {
        var world = new GameWorld();
        world.AddPlayer(1, "A");
        world.AddPlayer(2, "B");

        world.RemovePlayer(1);

        Assert.False(world.Players.ContainsKey(1));
        Assert.True(world.Players.ContainsKey(2));
    }
}

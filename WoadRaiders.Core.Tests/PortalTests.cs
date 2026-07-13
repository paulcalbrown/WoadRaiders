using System;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

// The exit portal: opened once when the boss falls, and the way out of the run —
// an alive player standing in it is removed from the world with their haul recorded.
public class PortalTests
{
    [Fact]
    public void Open_portal_sticks_at_its_first_position()
    {
        var world = new GameWorld(new Random(1));

        world.OpenPortal(new Vector3(100, 0, 0));
        world.OpenPortal(new Vector3(999, 0, 0)); // a respawned boss falling again

        Assert.Equal(new Vector3(100, 0, 0), world.Portal);
    }

    [Fact]
    public void Player_in_the_portal_exits_with_their_haul()
    {
        var world = new GameWorld(new Random(1));
        var player = world.AddPlayer(1, "Bran");
        player.Gold = 77;
        player.Inventory.Add(new Item(5, "Axe", ItemRarity.Common, ItemType.Axe, 3));

        for (var i = 0; i < 10; i++)
            world.Step(); // let the run-clock advance before the exit
        world.OpenPortal(new Vector3(30, 0, 0));
        player.Position = new Vector3(10, 0, 0); // within PortalRadius of the mouth
        world.Step();

        var exit = Assert.Single(world.ConsumePortalExits());
        Assert.Equal(1, exit.PlayerId);
        Assert.Equal("Bran", exit.PlayerName);
        Assert.Equal(77, exit.Gold);
        Assert.Equal(1, exit.ItemsLooted);
        Assert.Equal(10, exit.DurationTicks); // joined at tick 0; the 11th step records the exit before advancing Tick
        Assert.Empty(world.Players);          // gone from the world the same tick
        Assert.Empty(world.ConsumePortalExits()); // drained — reported exactly once
    }

    [Fact]
    public void Players_outside_the_portal_stay_in_the_run()
    {
        var world = new GameWorld(new Random(1));
        world.AddPlayer(1, "Far");
        world.Players[1].Position = new Vector3(SimConstants.PortalRadius + 20f, 0, 0);

        world.OpenPortal(Vector3.Zero);
        world.Step();

        Assert.Empty(world.ConsumePortalExits());
        Assert.Single(world.Players);
    }

    [Fact]
    public void No_exit_while_the_portal_is_closed()
    {
        var world = new GameWorld(new Random(1));
        world.AddPlayer(1, "Early"); // standing at the future portal spot

        world.Step();

        Assert.Empty(world.ConsumePortalExits());
        Assert.Single(world.Players);
    }

    [Fact]
    public void Slain_enemies_are_tallied_for_the_run_summary()
    {
        var world = new GameWorld(new Random(1));
        world.SpawnEnemy(new Vector3(500, 0, 0)).TakeDamage(9999f);
        world.SpawnEnemy(new Vector3(600, 0, 0)).TakeDamage(9999f);

        world.Step();

        Assert.Equal(2, world.EnemiesSlain);
    }
}

using System;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;
using WoadRaiders.Server;

namespace WoadRaiders.Server.Tests;

// Drives the session with no socket â€” the point of extracting it from the server.
public class GameSessionTests
{
    private static DungeonGeometry OpenArena() =>
        new(Vector3.Zero, null, Array.Empty<EnemySpawnPoint>());

    [Fact]
    public void Buffered_input_moves_the_player_and_is_consumed_in_order()
    {
        var session = new GameSession(OpenArena(), new Random(1));
        session.AddPlayer(1, "A");
        for (uint seq = 1; seq <= 6; seq++)
            session.EnqueueInput(1, new PlayerInput { MoveX = 1f, Sequence = seq });

        var startX = session.Snapshot().Players.Single().X;
        for (var i = 0; i < 6; i++)
            session.Step();
        var player = session.Snapshot().Players.Single();

        Assert.True(player.X > startX, "east input should move the player east over ticks");
        Assert.Equal(6u, player.LastProcessedInput); // every input applied exactly once, in order
    }

    [Fact]
    public void A_starved_buffer_holds_the_player_without_regressing_its_sequence()
    {
        var session = new GameSession(OpenArena(), new Random(1));
        session.AddPlayer(1, "A");
        session.EnqueueInput(1, new PlayerInput { MoveX = 1f, Sequence = 1 });
        session.EnqueueInput(1, new PlayerInput { MoveX = 1f, Sequence = 2 });

        for (var i = 0; i < 5; i++) // drains the two inputs, then starves
            session.Step();

        // Held at the last processed sequence â€” never reset, so the client reconciles cleanly.
        Assert.Equal(2u, session.Snapshot().Players.Single().LastProcessedInput);
    }

    [Fact]
    public void Removing_a_player_drops_it_from_the_snapshot_and_ignores_its_input()
    {
        var session = new GameSession(OpenArena(), new Random(1));
        session.AddPlayer(1, "A");
        session.AddPlayer(2, "B");

        session.RemovePlayer(1);
        session.EnqueueInput(1, new PlayerInput { MoveX = 1f, Sequence = 1 }); // gone â€” must be a harmless no-op
        session.Step();

        var players = session.Snapshot().Players;
        Assert.Single(players);
        Assert.Equal(2, players[0].Id);
    }

    [Fact]
    public void Announces_the_boss_awaiting_at_startup()
    {
        var dungeon = new DungeonGeometry(Vector3.Zero, null,
            new[] { new EnemySpawnPoint(new Vector3(400, 0, 0), EnemyType.Minion) })
        {
            BossSpawn = new Vector3(900, 0, 900),
        };
        var session = new GameSession(dungeon, new Random(1));
        var events = new List<SessionEvent>();
        session.Notice += events.Add;

        session.SpawnInitial();

        Assert.Contains(events, e => e.Kind == SessionEventKind.BossAwaits);
    }

    [Fact]
    public void A_bossless_map_makes_no_boss_announcement()
    {
        var session = new GameSession(OpenArena(), new Random(1)); // OpenArena has no BossSpawn
        var events = new List<SessionEvent>();
        session.Notice += events.Add;

        session.SpawnInitial();

        Assert.DoesNotContain(events, e => e.Kind == SessionEventKind.BossAwaits);
    }
}

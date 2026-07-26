using System;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The baked-link crossing state and its replication contract, on a scripted
/// geometry so every branch is reachable without a bake: a shelf at Y=100
/// whose mesh ends at X=200, with one one-way link from that rim down to the
/// floor at Y=0. What is pinned here is the CONTRACT the wire depends on —
/// entry is deterministic, a crossing roots the player, acks keep flowing,
/// prediction walks the identical arc, and a reconcile that restores
/// (Code, Tick) replays to the server's exact position while one that drops
/// them drifts.
/// </summary>
public class LinkTraversalTests
{
    private static readonly Vector3 Spawn = new(100f, 100f, 100f);

    private sealed class CliffGeometry : IRealmGeometry
    {
        public const float RimX = 200f;
        public static readonly Vector3 Lip = new(RimX, 100f, 100f);
        public static readonly Vector3 Landing = new(260f, 0f, 100f);

        public Vector3 SpawnPoint => Spawn;

        // The shelf's mesh ends at the rim: an eastward step clamps there,
        // exactly as MoveAlongSurface clamps at a polygon boundary.
        public Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius)
        {
            var next = position + delta;
            if (position.X <= RimX && next.X > RimX)
                next.X = RimX;
            return next;
        }

        public int FindLink(Vector3 position, Vector3 desiredDir, float radius = SimConstants.CharacterRadius)
        {
            var dx = Lip.X - position.X;
            var dz = Lip.Z - position.Z;
            return dx * dx + dz * dz <= SimConstants.LinkBoardRadius * SimConstants.LinkBoardRadius
                   && MathF.Abs(Lip.Y - position.Y) <= SimConstants.LinkBoardHeadroom
                   && desiredDir.X > 0f
                ? 0
                : -1;
        }

        public bool TryGetLink(int code, out Vector3 from, out Vector3 to, float radius = SimConstants.CharacterRadius)
        {
            from = Lip;
            to = Landing;
            return code == 0;
        }
    }

    private static GameWorld WorldOnCliff(out PlayerState player)
    {
        var world = new GameWorld { Geometry = new CliffGeometry() };
        player = world.AddPlayer(1, "faller");
        player.Position = Spawn;
        return world;
    }

    private static void Push(GameWorld world, ref uint seq, bool attack = false, float moveX = 1f)
    {
        world.SetInput(1, new PlayerInput { MoveX = moveX, Attack = attack, Sequence = ++seq });
        world.Step();
    }

    /// <summary>Walk east until the push over the rim boards the link.</summary>
    private static void WalkToCrossing(GameWorld world, PlayerState player, ref uint seq)
    {
        for (var step = 0; step < 40 && !player.Link.Active; step++)
            Push(world, ref seq);
        Assert.True(player.Link.Active, "the push over the rim never boarded the link");
    }

    [Fact]
    public void Walking_off_the_rim_boards_the_link_and_lands_at_its_end()
    {
        var world = WorldOnCliff(out var player);
        uint seq = 0;
        WalkToCrossing(world, player, ref seq);

        // Entry: snapped to the lip, at the crossing's first tick.
        Assert.Equal(CliffGeometry.Lip, player.Position);
        Assert.Equal(0, (int)player.Link.Tick);
        var total = (int)player.Link.TotalTicks;
        Assert.True(total >= 2, $"a crossing is a visible motion, got {total} ticks");

        // The arc: strictly descending, rooted, still crossing.
        var lastY = player.Position.Y;
        for (var t = 1; t < total; t++)
        {
            Push(world, ref seq);
            Assert.True(player.Link.Active);
            Assert.True(player.Position.Y < lastY, "a fall descends every tick");
            Assert.Equal(Vector3.Zero, player.Velocity);
            lastY = player.Position.Y;
        }

        // The landing tick sets down exactly on the link's end and clears.
        Push(world, ref seq);
        Assert.False(player.Link.Active);
        Assert.Equal(CliffGeometry.Landing, player.Position);
    }

    [Fact]
    public void A_crossing_roots_the_player_and_holds_its_swing()
    {
        var world = WorldOnCliff(out var player);
        uint seq = 0;
        WalkToCrossing(world, player, ref seq);
        var total = (int)player.Link.TotalTicks;

        // Hammer attack the whole way down: no swing fires and the arc is
        // untouched by the held movement key.
        for (var t = 1; t < total; t++)
        {
            Push(world, ref seq, attack: true);
            Assert.False(player.IsAttacking, "no footing to swing from mid-crossing");
            Assert.Equal(0f, player.AttackCooldown);
        }

        // The landing tick regains footing — a held attack fires that same tick.
        Push(world, ref seq, attack: true);
        Assert.False(player.Link.Active);
        Assert.True(player.IsAttacking, "footing regained, the held swing should fire");
    }

    [Fact]
    public void Acks_keep_flowing_mid_crossing()
    {
        // Reconciliation drains the pending-input buffer by LastProcessedInput,
        // so a crossing that stopped acking would freeze the local player's
        // reconcile for its whole duration.
        var world = WorldOnCliff(out var player);
        uint seq = 0;
        WalkToCrossing(world, player, ref seq);

        while (player.Link.Active)
        {
            Push(world, ref seq);
            Assert.Equal(seq, player.LastProcessedInput);
        }
    }

    [Fact]
    public void Prediction_walks_the_same_arc_as_the_server()
    {
        var world = WorldOnCliff(out var serverPlayer);
        var prediction = new ClientPrediction(1, Spawn, new CliffGeometry());

        // Tick for tick — approach, entry, arc, landing, walk-off — the
        // predicted position must be bit-identical to the authoritative one.
        for (uint seq = 1; seq <= 30; seq++)
        {
            var input = new PlayerInput { MoveX = 1f, Sequence = seq };
            world.SetInput(1, input);
            world.Step();
            prediction.Predict(input);
            Assert.Equal(serverPlayer.Position, prediction.Position);
        }
    }

    [Fact]
    public void Reconcile_mid_crossing_replays_to_the_server_position()
    {
        var world = WorldOnCliff(out var serverPlayer);
        var prediction = new ClientPrediction(1, Spawn, new CliffGeometry());

        // The client has predicted 25 ticks; the server has processed 16 —
        // mid-crossing (entry lands around tick 14 at this walk speed).
        for (uint seq = 1; seq <= 28; seq++)
            prediction.Predict(new PlayerInput { MoveX = 1f, Sequence = seq });
        for (uint seq = 1; seq <= 25; seq++)
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f, Sequence = seq });
            world.Step();
        }
        Assert.True(serverPlayer.Link.Active, "fixture drift: tick 25 should sit mid-crossing");

        prediction.Reconcile(serverPlayer.Position, serverPlayer.AttackAnimRemaining,
                             serverPlayer.AttackCooldown, lastProcessedInput: 25,
                             serverPlayer.Link.Code, serverPlayer.Link.Tick, serverPlayer.LinkIntent);

        // The server catches up through the same inputs the client replayed.
        for (uint seq = 26; seq <= 28; seq++)
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f, Sequence = seq });
            world.Step();
        }

        Assert.Equal(serverPlayer.Position, prediction.Position);
    }

    [Fact]
    public void Reconcile_mid_intent_counts_the_same_held_ticks()
    {
        // The ack lands while the push is HELD at the rim but before it has
        // been held long enough to board. The replay must resume the count
        // from the server's number, or the crossing predicts a tick early
        // (double-counted) or late (reset) and every position after differs.
        var world = WorldOnCliff(out var serverPlayer);
        var prediction = new ClientPrediction(1, Spawn, new CliffGeometry());

        for (uint seq = 1; seq <= 25; seq++)
            prediction.Predict(new PlayerInput { MoveX = 1f, Sequence = seq });
        for (uint seq = 1; seq <= 15; seq++)
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f, Sequence = seq });
            world.Step();
        }
        Assert.True(serverPlayer.LinkIntent > 0 && !serverPlayer.Link.Active,
            $"fixture drift: tick 15 should sit mid-intent, got intent {serverPlayer.LinkIntent}");

        prediction.Reconcile(serverPlayer.Position, serverPlayer.AttackAnimRemaining,
                             serverPlayer.AttackCooldown, lastProcessedInput: 15,
                             -1, 0, serverPlayer.LinkIntent);

        for (uint seq = 16; seq <= 25; seq++)
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f, Sequence = seq });
            world.Step();
        }

        Assert.Equal(serverPlayer.Position, prediction.Position);
    }

    [Fact]
    public void A_brushed_push_never_boards_but_a_held_one_does()
    {
        // Boarding is irreversible and a single tick can qualify by accident
        // (a camera turn sweeps the push square to a crossing). A push that
        // qualifies for LinkIntentTicks-1 ticks and then wavers must never
        // board, however often it repeats; the same push held one tick longer
        // must.
        var world = WorldOnCliff(out var player);
        player.Position = new Vector3(CliffGeometry.RimX - 2f, 100f, 100f);
        uint seq = 0;

        // The cliff's 100-unit drop is PLUNGE-scale, so even a push held one
        // tick short of the plunge requirement, broken once, must never board.
        for (var cycle = 0; cycle < 5; cycle++)
        {
            player.Position = new Vector3(CliffGeometry.RimX - 2f, 100f, 100f);
            for (var t = 0; t < SimConstants.LinkIntentTicksPlunge - 1; t++)
                Push(world, ref seq); // east into the rim: qualifies…
            world.SetInput(1, new PlayerInput { MoveZ = 1f, Sequence = ++seq });
            world.Step();             // …then wavers away: the count resets
            Assert.False(player.Link.Active, $"cycle {cycle}: a brushed push boarded");
        }

        player.Position = new Vector3(CliffGeometry.RimX - 2f, 100f, 100f);
        for (var t = 0; t < SimConstants.LinkIntentTicksPlunge && !player.Link.Active; t++)
            Push(world, ref seq);
        Assert.True(player.Link.Active, "a held push must board");
    }

    [Fact]
    public void Reconcile_without_the_link_state_drifts()
    {
        // The failure this wire field exists to prevent: restore the position
        // but not the crossing, and the replay treats a rooted player as free —
        // it walks the pending inputs off into mid-air and lands somewhere the
        // server never was.
        var world = WorldOnCliff(out var serverPlayer);
        var prediction = new ClientPrediction(1, Spawn, new CliffGeometry());

        for (uint seq = 1; seq <= 28; seq++)
            prediction.Predict(new PlayerInput { MoveX = 1f, Sequence = seq });
        for (uint seq = 1; seq <= 25; seq++)
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f, Sequence = seq });
            world.Step();
        }
        Assert.True(serverPlayer.Link.Active, "fixture drift: tick 25 should sit mid-crossing");

        prediction.Reconcile(serverPlayer.Position, serverPlayer.AttackAnimRemaining,
                             serverPlayer.AttackCooldown, lastProcessedInput: 25);

        for (uint seq = 26; seq <= 28; seq++)
        {
            world.SetInput(1, new PlayerInput { MoveX = 1f, Sequence = seq });
            world.Step();
        }

        Assert.NotEqual(serverPlayer.Position, prediction.Position);
    }

    [Fact]
    public void Death_mid_crossing_respawns_grounded()
    {
        var world = WorldOnCliff(out var player);
        uint seq = 0;
        WalkToCrossing(world, player, ref seq);

        player.TakeDamage(player.Health);
        Push(world, ref seq);

        Assert.False(player.Link.Active, "a death mid-fall must not keep falling");
        Assert.Equal(Spawn, player.Position);
        Assert.Equal(player.MaxHealth, player.Health);
    }

    // --- the pure functions the arc derives from ---

    [Fact]
    public void Duration_is_never_a_teleport_and_scales_with_the_drop()
    {
        Assert.Equal(2, (int)LinkTraversal.Duration(Vector3.Zero, Vector3.Zero));

        // 220 units of drop at LinkFallSpeed 660 and 30 ticks/s: 10 ticks.
        var tall = LinkTraversal.Duration(new Vector3(0, 220, 0), Vector3.Zero);
        Assert.Equal(10, (int)tall);

        // A long shallow link is paced by the horizontal cap, not the tiny drop.
        var shallow = LinkTraversal.Duration(new Vector3(0, 20, 0), new Vector3(300, 0, 0));
        Assert.True(shallow > 2, $"300 units sideways cannot cross in {shallow} ticks");
    }

    [Fact]
    public void The_arc_starts_at_the_lip_and_ends_on_the_landing()
    {
        var crossing = LinkTraversal.Begin(0, CliffGeometry.Lip, CliffGeometry.Landing);
        Assert.Equal(CliffGeometry.Lip, crossing.PositionAt(0));
        Assert.Equal(CliffGeometry.Landing, crossing.PositionAt(crossing.TotalTicks));
    }

    [Fact]
    public void Restore_rebuilds_the_crossing_and_distrusts_bad_wire()
    {
        var geometry = new CliffGeometry();

        var crossing = LinkTraversal.Restore(0, 2, geometry);
        Assert.True(crossing.Active);
        Assert.Equal(CliffGeometry.Lip, crossing.From);
        Assert.Equal(CliffGeometry.Landing, crossing.To);
        Assert.Equal(2, (int)crossing.Tick);

        // A tick at or past the total (the server clears those before they can
        // ride the wire) clamps to the last airborne tick instead of overshooting.
        var clamped = LinkTraversal.Restore(0, 999, geometry);
        Assert.Equal(clamped.TotalTicks - 1, clamped.Tick);

        // A code the geometry does not know restores to grounded, not a crash.
        Assert.False(LinkTraversal.Restore(7, 0, geometry).Active);
        Assert.False(LinkTraversal.Restore(-1, 0, geometry).Active);
    }
}

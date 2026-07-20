using System;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Server.Tests;

// The full end-of-run chain at the session level, no sockets: a knight walks to
// the boss, cuts it down, the portal opens where the boss stood, the knight steps
// through, and the session reports the finished run.
public class PortalRunTests
{
    private static readonly Vector3 BossPost = new(200, 0, 0);

    private static RealmDefinition BossArena()
    {
        // Four far-off markers satisfy the director's population floor without
        // ever aggroing (minion aggro 480 << 3000); the boss guards its post.
        var markers = Enumerable.Range(0, 4)
            .Select(i => new EnemySpawnPoint(new Vector3(3000 + 100 * i, 0, 3000), EnemyType.Minion))
            .ToArray();
        return new RealmDefinition(Vector3.Zero, null, markers) { BossSpawn = BossPost };
    }

    [Fact]
    public void Slaying_the_boss_opens_the_portal_and_walking_in_completes_the_run()
    {
        var session = new GameSession(BossArena(), new Random(1));
        session.SpawnInitial();
        session.AddPlayer(1, "Bran", CharacterClass.Knight);

        // Drive the knight one tick at a time from the authoritative snapshot:
        // close on the boss, then hold the blade to its ribs until it falls.
        uint seq = 0;
        var snap = session.Snapshot();
        for (var tick = 0; tick < 2000 && !snap.PortalOpen; tick++)
        {
            var me = snap.Players.Single();
            var boss = snap.Enemies.Where(e => e.Type == (byte)EnemyType.Boss)
                .Cast<EnemySnapshot?>().FirstOrDefault();

            var input = new PlayerInput { Sequence = ++seq };
            if (boss is { } target)
            {
                var dx = target.X - me.X;
                var dz = target.Z - me.Z;
                var dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist > 60f)
                {
                    (input.MoveX, input.MoveZ) = (dx / dist, dz / dist);
                }
                else
                {
                    input.Attack = true;
                    (input.AimX, input.AimZ) = (dx / dist, dz / dist);
                }
            }
            session.EnqueueInput(1, input);
            session.Step();
            snap = session.Snapshot();
        }

        Assert.True(snap.PortalOpen, "slaying the boss should open the portal");
        Assert.Equal(BossPost.X, snap.PortalX);
        Assert.Equal(BossPost.Z, snap.PortalZ);

        // The knight stands at the boss's corpse â€” walk the last stretch into the mouth.
        RunReport? report = null;
        for (var tick = 0; tick < 300 && report is null; tick++)
        {
            var me = snap.Players.Cast<PlayerSnapshot?>().FirstOrDefault();
            if (me is { } self)
            {
                var dx = snap.PortalX - self.X;
                var dz = snap.PortalZ - self.Z;
                var dist = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dz * dz));
                session.EnqueueInput(1, new PlayerInput { MoveX = dx / dist, MoveZ = dz / dist, Sequence = ++seq });
            }
            session.Step();
            report = session.ConsumePortalExits().Cast<RunReport?>().FirstOrDefault();
            snap = session.Snapshot();
        }

        Assert.NotNull(report);
        Assert.Equal(1, report.Value.PlayerId);
        Assert.Equal("Bran", report.Value.PlayerName);
        Assert.True(report.Value.FoesSlain >= 1, "the boss itself counts toward the tally");
        Assert.True(report.Value.DurationSeconds > 0, "the fight takes real seconds");
        Assert.Empty(snap.Players); // out of the world â€” no snapshot carries them again
    }
}

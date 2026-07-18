// A scripted LiteNetLib probe that verifies the boss portal end to end against
// a running dedicated server (see .claude/skills/verify). It needs the tiny
// boss arena so the fight is seconds, not minutes:
//
//   dotnet run --project WoadRaiders.Server -- --map tools/maps/portal_arena.json
//   dotnet run tools/PortalProbe.cs                (in another shell)
//
// It forges an instance as a KNIGHT, walks to the boss, cuts it down, walks
// into the portal that tears open, and asserts from the authoritative stream:
//   A) the portal appears in snapshots after the boss falls, where the boss stood,
//   B) stepping into it yields a RunComplete with a sane summary,
//   C) after the RunComplete our player is out — no later snapshot carries us.
// Exit code 0 = all pass.

#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj

using System.Diagnostics;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;

const float BossX = 220f; // the arena map's bossSpawn
const float MeleeWithin = 60f;

var listener = new EventBasedNetListener();
var net = new NetManager(listener) { AutoRecycle = true };
var assembler = new SnapshotAssembler();

int myId = -1;
uint sequence = 0;
var welcomed = false;
var sawPortalOpen = false;
float portalX = float.NaN, portalZ = float.NaN;
RunCompletePacket? runComplete = null;
var seenAfterRunComplete = false;
WorldSnapshotPacket? latest = null;
NetPeer? server = null;

listener.PeerConnectedEvent += peer =>
{
    server = peer;
    Console.WriteLine("[probe] connected — forging a portal-arena instance as a Knight");
    peer.Send(NetProtocol.Frame(MessageType.JoinRequest,
        new JoinRequest
        {
            Name = "PortalProbe",
            Class = (byte)CharacterClass.Knight,
            Mode = (byte)JoinMode.Create,
            Dungeon = (byte)DungeonId.Crag, // --map hosts one map; every forge uses it
            InstanceName = "Portal run",
        }),
        0, DeliveryMethod.ReliableOrdered);
};

listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
{
    switch ((MessageType)reader.GetByte())
    {
        case MessageType.Welcome:
            var welcome = new WelcomePacket();
            welcome.Deserialize(reader);
            myId = welcome.PlayerId;
            welcomed = true;
            Console.WriteLine($"[probe] welcomed as player {myId} into instance #{welcome.InstanceId}");
            break;

        case MessageType.RunComplete:
            var packet = new RunCompletePacket();
            packet.Deserialize(reader);
            runComplete = packet;
            Console.WriteLine($"[probe] run complete — {packet.Gold} gold, {packet.ItemsLooted} relics, " +
                              $"{packet.FoesSlain} foes, {packet.DurationSeconds}s in \"{packet.RaidName}\"");
            break;

        case MessageType.WorldSnapshot:
            if (!assembler.TryAdd(reader, out var snapshot))
                break;
            latest = snapshot;
            if (snapshot.PortalOpen && !sawPortalOpen)
            {
                sawPortalOpen = true;
                (portalX, portalZ) = (snapshot.PortalX, snapshot.PortalZ);
                Console.WriteLine($"[probe] the portal tears open at ({portalX:0}, {portalZ:0})");
            }
            if (runComplete is not null && snapshot.Players.Any(p => p.Id == myId))
                seenAfterRunComplete = true; // must never happen — we left the world
            break;
    }
};

// One bot tick: before the portal opens, hunt the boss (walk in, then hold the
// blade to its ribs); after it opens, walk into the mouth.
void SendIntent()
{
    if (latest is null)
        return;
    var me = latest.Players.Cast<PlayerSnapshot?>().FirstOrDefault(p => p!.Value.Id == myId);
    if (me is not { } self)
        return;

    var input = new InputPacket { Sequence = ++sequence };
    if (latest.PortalOpen)
    {
        var dx = latest.PortalX - self.X;
        var dz = latest.PortalZ - self.Z;
        var dist = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dz * dz));
        (input.MoveX, input.MoveZ) = (dx / dist, dz / dist);
    }
    else
    {
        var boss = latest.Enemies.Where(e => e.Type == (byte)EnemyType.Boss)
            .Cast<EnemySnapshot?>().FirstOrDefault();
        if (boss is not { } target)
            return; // between the boss falling and the portal snapshot — hold still

        var dx = target.X - self.X;
        var dz = target.Z - self.Z;
        var dist = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dz * dz));
        if (dist > MeleeWithin)
        {
            (input.MoveX, input.MoveZ) = (dx / dist, dz / dist);
        }
        else
        {
            input.Attack = true;
            (input.AimX, input.AimZ) = (dx / dist, dz / dist);
        }
    }
    server?.Send(NetProtocol.Frame(MessageType.Input, input), 0, DeliveryMethod.ReliableOrdered);
}

net.Start();
net.Connect("127.0.0.1", NetConfig.DefaultPort, NetConfig.ConnectionKey);
Console.WriteLine("[probe] dialing 127.0.0.1:9050 ...");

var clock = Stopwatch.StartNew();
var nextInput = TimeSpan.Zero;
TimeSpan? completedAt = null;
while (clock.Elapsed < TimeSpan.FromSeconds(90))
{
    net.PollEvents();
    if (welcomed && runComplete is null && clock.Elapsed >= nextInput)
    {
        SendIntent();
        nextInput = clock.Elapsed + TimeSpan.FromMilliseconds(33);
    }
    // Linger a moment after the summary to prove no snapshot carries us anymore.
    if (runComplete is not null)
    {
        completedAt ??= clock.Elapsed;
        if (clock.Elapsed - completedAt > TimeSpan.FromSeconds(1.5))
            break;
    }
    Thread.Sleep(5);
}
net.Stop();

var portalAtBoss = sawPortalOpen && MathF.Abs(portalX - BossX) < 1f && MathF.Abs(portalZ) < 1f;
var summaryOk = runComplete is { } run && run.DurationSeconds > 0 && run.FoesSlain >= 1
                && run.RaidName == "Portal run";
Console.WriteLine($"A portal opened at the boss:  {(portalAtBoss ? "PASS" : $"FAIL (open={sawPortalOpen} at {portalX},{portalZ})")}");
Console.WriteLine($"B run summary received:       {(summaryOk ? "PASS" : "FAIL")}");
Console.WriteLine($"C gone from the world after:  {(!seenAfterRunComplete && runComplete is not null ? "PASS" : "FAIL")}");

var pass = portalAtBoss && summaryOk && !seenAfterRunComplete;
Console.WriteLine(pass ? "[probe] ALL CHECKS PASSED" : "[probe] FAILURES — see above");
return pass ? 0 : 1;

// A scripted LiteNetLib probe that verifies the Crypt — the first INDOOR,
// DESCENDING realm — end to end against a running dedicated server (see
// .claude/skills/verify). The mirror of TerrainProbe (the Crag climbs; the
// Crypt sinks). A .NET 10 file-based app:
//
//   dotnet run --project WoadRaiders.Server        (in one shell)
//   dotnet run tools/CryptProbe.cs                 (in another)
//
// It forges a fresh instance of the Crypt and asserts, from the authoritative
// stream alone:
//   A) the Welcome arrives,
//   B) the geometry packet carries the crypt's triangle soup + navmesh,
//   C) our spawn stands ON the undercroft floor (snapshot Y ≈ the sample there),
//   D) walking east — down the descent stair, across the Broken Span, into the
//      catacomb maze — SINKS the authoritative Y (the descent is simulated
//      server-side, not painted on),
//   E) the rebuilt client-side geometry agrees with the server about the walk
//      (prediction-grade determinism over stairs and the stepped bridge deck).
// Exit code 0 = all pass.

#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj

using System.Diagnostics;
using System.Numerics;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;

var listener = new EventBasedNetListener();
var net = new NetManager(listener) { AutoRecycle = true };
var assembler = new SnapshotAssembler();

int myId = -1;
uint sequence = 0;
var welcomed = false;
RealmDefinition? realm = null;
IRealmGeometry? movement = null; // the shipped navmesh — what prediction (and this replay) moves on
RealmGeometryPacket? geometryPacket = null;
// Realms arrive chunked since v21 (Shared.GeometryChunks) — a realm may now
// outgrow what one fragmented reliable message can carry.
var geometryChunks = new GeometryAssembler();
float? spawnY = null;
float floorY = float.MaxValue;
Vector3? latestPos = null;

listener.PeerConnectedEvent += peer =>
{
    Console.WriteLine("[probe] connected — forging an instance of the Crypt");
    peer.Send(NetProtocol.Frame(MessageType.JoinRequest,
        new JoinRequest
        {
            Name = "CryptProbe",
            Class = (byte)CharacterClass.Knight,
            Mode = (byte)JoinMode.Create,
            Dungeon = (byte)DungeonId.Crypt,
            InstanceName = "CryptProbe's raid",
        }),
        0, DeliveryMethod.ReliableOrdered);
};

listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
{
    switch ((MessageType)reader.GetByte())
    {
        case MessageType.RealmGeometry:
            if (geometryChunks.TryAdd(reader, out var wholeRealm))
            {
                geometryPacket = wholeRealm;
                realm = RealmSnapshot.ToDefinition(geometryPacket);
                movement = RealmSnapshot.ToMovementGeometry(geometryPacket);
                Console.WriteLine($"[probe] geometry: {geometryPacket.SoupTriangles.Length / 3} triangles " +
                                  $"{geometryPacket.NavMesh.Length / 1024} KB navmesh");
            }
            break;

        case MessageType.Welcome:
            var welcome = new WelcomePacket();
            welcome.Deserialize(reader);
            myId = welcome.PlayerId;
            welcomed = true;
            break;

        case MessageType.WorldSnapshot:
            if (assembler.TryAdd(reader, out var snapshot))
            {
                foreach (var p in snapshot.Players)
                {
                    if (p.Id != myId)
                        continue;
                    latestPos = new Vector3(p.X, p.Y, p.Z);
                    spawnY ??= p.Y;
                    floorY = MathF.Min(floorY, p.Y);
                }
            }
            break;
    }
};

net.Start();
var server = net.Connect("127.0.0.1", NetConfig.DefaultPort, NetConfig.ConnectionKey);
Console.WriteLine("[probe] dialing 127.0.0.1:9050 ...");

// March east: down the descent stair, through the Hall of the Dead, along the
// Processional, over the Broken Span, into the catacomb maze — the realm sinks
// ~160 over ~2.6 km of walking.
var clock = Stopwatch.StartNew();
var nextInput = TimeSpan.Zero;
var lastX = float.MinValue;

// March until the walker has DESCENDED far enough to prove the point, then stop
// — never for a fixed duration.
//
// This used to march for a flat 55 s, a number tuned to a realm that no longer
// exists. Two things then broke it at once, and both are the same mistake:
//   * the Crypt was reshaped, so 55 s no longer means "reaches the far end";
//   * its garrison went from 20 to 200, so a walker holding W with no weapon
//     drawn now DIES in the Ossuary at about 25 s and respawns at the entrance.
// The replay compares against the server's last snapshot, so a death midway
// makes it diverge by six thousand units and the probe reports a desync that is
// not there. It cried wolf on a realm that was working perfectly.
//
// Stopping on the assertion instead of the clock fixes both: the probe finishes
// as soon as it has seen what it came to see, and gets out before the fight.
const float SunkEnough = 150f;   // must exceed test D's own 100-unit threshold
var marched = TimeSpan.FromSeconds(45); // a cap, not a target
var deepest = float.MaxValue;
var died = false;
while (clock.Elapsed < marched)
{
    net.PollEvents();
    if (welcomed && clock.Elapsed >= nextInput)
    {
        server.Send(NetProtocol.Frame(MessageType.Input,
            new InputPacket { MoveX = 1f, Sequence = ++sequence }), 0, DeliveryMethod.ReliableOrdered);
        nextInput = clock.Elapsed + TimeSpan.FromMilliseconds(33);
    }

    if (latestPos is { } here && spawnY is { } startY)
    {
        deepest = MathF.Min(deepest, here.Y);
        // A raider who is suddenly BEHIND where they were is a raider who died
        // and respawned. Say so rather than reporting it as a prediction fault.
        if (here.X < lastX - 200f)
        {
            died = true;
            break;
        }
        lastX = MathF.Max(lastX, here.X);
        if (startY - here.Y >= SunkEnough)
            break;
    }
    Thread.Sleep(5);
}
net.Stop();
Console.WriteLine(died
    ? $"[probe] the walker was killed and respawned after {clock.Elapsed.TotalSeconds:0} s — " +
      "the realm's garrison outlasts a probe that cannot fight; shortening the march"
    : $"[probe] marched {clock.Elapsed.TotalSeconds:0} s, {sequence} inputs, deepest y={deepest:0}");

var terrainOk = geometryPacket is { } gp && gp.SoupTriangles.Length > 0 && realm?.Soup is not null;
var spawnGroundY = movement is not null && realm is not null
    ? movement.GroundHeight(realm.SpawnPoint)
    : float.NaN;
var spawnOk = spawnY is { } sy && MathF.Abs(sy - spawnGroundY) < 2f;
var sank = spawnY is { } s && floorY < s - 100f;

// Replay-determinism: walk the same inputs over the REBUILT geometry and land
// where the server's last snapshot puts us.
var replayOk = false;
if (realm is not null && movement is not null && latestPos is { } serverPos && spawnY is not null)
{
    var pos = realm.SpawnPoint;
    var step = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
    for (var i = 0; i < (int)sequence; i++)
        pos = movement.Move(pos, new Vector3(step, 0, 0));
    // The server processed at most `sequence` of our inputs (some may still be
    // in flight), so our replay can only be AHEAD along +X, never behind. The
    // height check compares against the movement geometry's own ground under
    // the server position, so the bridge deck (above the pit's terrain) is
    // judged by the same rules the server moved by.
    replayOk = pos.X >= serverPos.X - 1f
               && MathF.Abs(pos.Z - serverPos.Z) < 1f
               && MathF.Abs(pos.Y - movement.Move(serverPos, new Vector3(0.01f, 0, 0)).Y) < 20f;
    Console.WriteLine($"[probe] server pos ({serverPos.X:0.0}, {serverPos.Y:0.0}, {serverPos.Z:0.0}), " +
                      $"replay pos ({pos.X:0.0}, {pos.Y:0.0}, {pos.Z:0.0})");
}

Console.WriteLine($"A welcomed ({NetConfig.ConnectionKey}):  {(welcomed ? "PASS" : "FAIL")}");
Console.WriteLine($"B terrain on the wire:         {(terrainOk ? "PASS" : "FAIL")}");
Console.WriteLine($"C spawn stands on the floor:   {(spawnOk ? "PASS" : $"FAIL (Y={spawnY}, floor={spawnGroundY})")}");
Console.WriteLine($"D the walk sank into the deep: {(sank ? "PASS" : $"FAIL (spawn Y={spawnY}, floor Y={floorY})")}");
Console.WriteLine($"E client replay agrees:        {(replayOk ? "PASS" : "FAIL")}");

var pass = welcomed && terrainOk && spawnOk && sank && replayOk && !died;
Console.WriteLine(pass ? "[probe] ALL CHECKS PASSED" : "[probe] FAILURES — see above");
return pass ? 0 : 1;

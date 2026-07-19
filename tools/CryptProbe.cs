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
//   B) the geometry packet carries the crypt's heightfield terrain,
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
DungeonGeometry? geometry = null;
DungeonGeometryPacket? geometryPacket = null;
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
        case MessageType.DungeonGeometry:
            geometryPacket = new DungeonGeometryPacket();
            geometryPacket.Deserialize(reader);
            geometry = DungeonSnapshot.ToGeometry(geometryPacket);
            Console.WriteLine($"[probe] geometry: terrain={geometryPacket.HasTerrain} " +
                              $"({geometryPacket.TerrainWidth}x{geometryPacket.TerrainDepth}), " +
                              $"{geometryPacket.Boxes.Length / 6} solids");
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
while (clock.Elapsed < TimeSpan.FromSeconds(12))
{
    net.PollEvents();
    if (welcomed && clock.Elapsed >= nextInput)
    {
        server.Send(NetProtocol.Frame(MessageType.Input,
            new InputPacket { MoveX = 1f, Sequence = ++sequence }), 0, DeliveryMethod.ReliableOrdered);
        nextInput = clock.Elapsed + TimeSpan.FromMilliseconds(33);
    }
    Thread.Sleep(5);
}
net.Stop();

var terrainOk = geometryPacket is { HasTerrain: true } && geometry?.Terrain is not null;
var spawnGroundY = geometry is not null && latestPos is not null
    ? geometry.Terrain!.Sample(geometry.SpawnPoint.X, geometry.SpawnPoint.Z)
    : float.NaN;
var spawnOk = spawnY is { } sy && MathF.Abs(sy - spawnGroundY) < 2f;
var sank = spawnY is { } s && floorY < s - 100f;

// Replay-determinism: walk the same inputs over the REBUILT geometry and land
// where the server's last snapshot puts us.
var replayOk = false;
if (geometry is not null && latestPos is { } serverPos && spawnY is not null)
{
    var pos = geometry.SpawnPoint;
    var step = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
    for (var i = 0; i < (int)sequence; i++)
        pos = geometry.Move(pos, new Vector3(step, 0, 0));
    // The server processed at most `sequence` of our inputs (some may still be
    // in flight), so our replay can only be AHEAD along +X, never behind. The
    // height check reads the server's own ground under its position, so the
    // bridge deck (a solid, above the pit's terrain) is judged by Move, not
    // by the terrain sample alone.
    replayOk = pos.X >= serverPos.X - 1f
               && MathF.Abs(pos.Z - serverPos.Z) < 1f
               && MathF.Abs(pos.Y - geometry.Move(serverPos, Vector3.Zero).Y) < 20f;
    Console.WriteLine($"[probe] server pos ({serverPos.X:0.0}, {serverPos.Y:0.0}, {serverPos.Z:0.0}), " +
                      $"replay pos ({pos.X:0.0}, {pos.Y:0.0}, {pos.Z:0.0})");
}

Console.WriteLine($"A welcomed ({NetConfig.ConnectionKey}):  {(welcomed ? "PASS" : "FAIL")}");
Console.WriteLine($"B terrain on the wire:         {(terrainOk ? "PASS" : "FAIL")}");
Console.WriteLine($"C spawn stands on the floor:   {(spawnOk ? "PASS" : $"FAIL (Y={spawnY}, floor={spawnGroundY})")}");
Console.WriteLine($"D the walk sank into the deep: {(sank ? "PASS" : $"FAIL (spawn Y={spawnY}, floor Y={floorY})")}");
Console.WriteLine($"E client replay agrees:        {(replayOk ? "PASS" : "FAIL")}");

var pass = welcomed && terrainOk && spawnOk && sank && replayOk;
Console.WriteLine(pass ? "[probe] ALL CHECKS PASSED" : "[probe] FAILURES — see above");
return pass ? 0 : 1;

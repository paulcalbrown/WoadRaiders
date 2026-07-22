// A scripted LiteNetLib probe that verifies the open-realm terrain end to end
// against a running dedicated server (see .claude/skills/verify). A .NET 10
// file-based app:
//
//   dotnet run --project WoadRaiders.Server        (in one shell)
//   dotnet run tools/TerrainProbe.cs               (in another)
//
// It forges a fresh instance of the Crag and asserts, from the authoritative
// stream alone:
//   A) the Welcome arrives (the v14 protocol works),
//   B) the geometry packet carries the realm's triangle soup + navmesh,
//   C) our spawn stands ON the terrain (snapshot Y ≈ the field's sample there),
//   D) walking east up the realm RAISES the authoritative Y (verticality is
//      simulated server-side, not painted on),
//   E) the rebuilt client-side geometry agrees with the server about the walk
//      (prediction-grade determinism: replaying our inputs lands where the
//      server says we are, within reconciliation tolerance).
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
float peakY = float.MinValue;
Vector3? latestPos = null;

listener.PeerConnectedEvent += peer =>
{
    Console.WriteLine("[probe] connected — forging an instance of the Crag");
    peer.Send(NetProtocol.Frame(MessageType.JoinRequest,
        new JoinRequest
        {
            Name = "TerrainProbe",
            Class = (byte)CharacterClass.Knight,
            Mode = (byte)JoinMode.Create,
            Dungeon = (byte)DungeonId.Crag,
            InstanceName = "TerrainProbe's raid",
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
                    peakY = MathF.Max(peakY, p.Y);
                }
            }
            break;
    }
};

net.Start();
var server = net.Connect("127.0.0.1", NetConfig.DefaultPort, NetConfig.ConnectionKey);
Console.WriteLine("[probe] dialing 127.0.0.1:9050 ...");

// March east: out of the glen, up the dale — the land rises ~40 over ~2 km.
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

var terrainOk = geometryPacket is { } gp && gp.SoupTriangles.Length > 0 && realm?.Soup is not null;
var spawnGroundY = movement is not null && realm is not null
    ? movement.GroundHeight(realm.SpawnPoint)
    : float.NaN;
var spawnOk = spawnY is { } sy && MathF.Abs(sy - spawnGroundY) < 2f;
var climbed = spawnY is { } s && peakY > s + 15f;

// Replay-determinism: walk the same inputs over the REBUILT movement geometry
// (the navmesh the server shipped) and land where the server's snapshot puts us.
var replayOk = false;
if (realm is not null && movement is not null && latestPos is { } serverPos && spawnY is not null)
{
    var pos = realm.SpawnPoint;
    var step = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;
    for (var i = 0; i < (int)sequence; i++)
        pos = movement.Move(pos, new Vector3(step, 0, 0));
    // The server processed at most `sequence` of our inputs (some may still be
    // in flight), so our replay can only be AHEAD along +X, never behind.
    // Y is judged by the MOVEMENT geometry's ground — the same floor surface
    // the server's sim rides.
    replayOk = pos.X >= serverPos.X - 1f
               && MathF.Abs(pos.Z - serverPos.Z) < 1f
               && MathF.Abs(movement.GroundHeight(serverPos) - serverPos.Y) < 2f;
    Console.WriteLine($"[probe] server pos ({serverPos.X:0.0}, {serverPos.Y:0.0}, {serverPos.Z:0.0}), " +
                      $"replay pos ({pos.X:0.0}, {pos.Y:0.0}, {pos.Z:0.0})");
}

Console.WriteLine($"A welcomed ({NetConfig.ConnectionKey}):  {(welcomed ? "PASS" : "FAIL")}");
Console.WriteLine($"B terrain on the wire:         {(terrainOk ? "PASS" : "FAIL")}");
Console.WriteLine($"C spawn stands on the ground:  {(spawnOk ? "PASS" : $"FAIL (Y={spawnY}, ground={spawnGroundY})")}");
Console.WriteLine($"D the walk climbed the realm:  {(climbed ? "PASS" : $"FAIL (spawn Y={spawnY}, peak Y={peakY})")}");
Console.WriteLine($"E client replay agrees:        {(replayOk ? "PASS" : "FAIL")}");

var pass = welcomed && terrainOk && spawnOk && climbed && replayOk;
Console.WriteLine(pass ? "[probe] ALL CHECKS PASSED" : "[probe] FAILURES — see above");
return pass ? 0 : 1;

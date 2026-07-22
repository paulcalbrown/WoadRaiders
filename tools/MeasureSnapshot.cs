// Sizing tool for a realm's SightRadius (Core.DungeonCatalog) — the oracle
// behind the wire budgets in docs/realms/. Sweeps the radius over a synthetic
// realm the size of Crypt v2 (7200 x 2800, 300 enemies, 120 ground items) and
// reports the snapshot's bytes, its unreliable chunk count, and the odds a whole
// snapshot survives a lossy link. Losing ANY chunk discards the whole update, so
// the chunk count — not the byte count — is the number to design against.
//
// The lesson it exists to keep re-teaching: a circular sight radius on a long,
// narrow realm covers far more of it than intuition says. Guess and you will
// pick a radius that filters nothing.
//
//   dotnet run tools/MeasureSnapshot.cs
#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj
#:property PublishAot=false

using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

const int Mtu = 1200; // a conservative usable payload per unreliable packet

var world = new GameWorld();
// Eight raiders in two clusters, as an 8-player party actually plays.
for (var i = 0; i < 5; i++)
    world.AddPlayer(i, $"P{i}").Position = new Vector3(2400 + i * 40, 0, 2000);
for (var i = 5; i < 8; i++)
    world.AddPlayer(i, $"P{i}").Position = new Vector3(2600 + i * 40, 0, 2200);

// 300 enemies spread over the v2 footprint.
var rng = new Random(7);
for (var i = 0; i < 300; i++)
    world.SpawnEnemy(new Vector3(rng.Next(0, 7200), rng.Next(-880, 1), rng.Next(1040, 3840)),
                     (EnemyType)(i % 3));
// Loot from a run in progress.
for (var i = 0; i < 120; i++)
    world.DropGold(10, new Vector3(rng.Next(0, 7200), rng.Next(-880, 1), rng.Next(1040, 3840)));

static (int bytes, int chunks) Measure(WorldSnapshotPacket snap)
{
    var w = new NetDataWriter();
    snap.Serialize(w);
    var budget = Mtu - SnapshotChunks.HeaderBytes;
    return (w.Length, Math.Max(1, (w.Length + budget - 1) / budget));
}

static double Integrity(int chunks, double loss) => Math.Pow(1 - loss, chunks);

var eye = world.Players[0].Position;
Console.WriteLine($"{"case",-34}{"bytes",8}{"chunks",8}{"@1% loss",10}{"@3% loss",10}");
var cases = new List<(string, WorldSnapshotPacket)> { ("unfiltered (300 enemies)", WorldSnapshot.From(world)) };
foreach (var r in new[] { 800f, 1000f, 1200f, 1600f, 2200f, 5000f })
    cases.Add(($"sight {r:0}", WorldSnapshot.Around(world, eye, r)));
foreach (var (label, snap) in cases)
{
    var (bytes, chunks) = Measure(snap);
    Console.WriteLine($"{label,-34}{bytes,8}{chunks,8}{Integrity(chunks, 0.01),10:P1}{Integrity(chunks, 0.03),10:P1}");
    Console.WriteLine($"{"",-34}{snap.Enemies.Length,8} enemies, {snap.GroundItems.Length} loot");
}

var perSecond = Measure(WorldSnapshot.Around(world, eye, 1200f)).bytes * NetConfig.SnapshotsPerSecond;
Console.WriteLine($"\nCrypt, 8 players: {perSecond * 8 / 1024.0:0} KB/s out per instance " +
                  $"(unfiltered would be {Measure(WorldSnapshot.From(world)).bytes * NetConfig.SnapshotsPerSecond * 8 / 1024.0:0} KB/s)");

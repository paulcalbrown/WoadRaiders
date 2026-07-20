// A scripted LiteNetLib probe that verifies the character-class feature end to
// end against a running dedicated server (see .claude/skills/verify). A .NET 10
// file-based app:
//
//   dotnet run --project WoadRaiders.Server        (in one shell)
//   dotnet run tools/ClassProbe.cs                 (in another)
//
// It forges a fresh instance of a dungeon (optionally named: dotnet run
// tools/ClassProbe.cs cairn) as a MAGE, walks toward the nearest enemy, and
// holds the trigger. It then asserts, from the authoritative stream alone:
//   A) the Welcome arrives (protocol version + join path work),
//   B) our player snapshot carries Class = Mage,
//   C) our spawn health is the mage's 70, not the knight's 100,
//   D) player-fired projectiles appear with Kind = MagicBolt,
//   E) an enemy's health drops (a ranged hit landed server-side),
//   F) the served geometry is the dungeon we asked for.
// Exit code 0 = all pass.

#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj

using System.Diagnostics;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;

const float MageMaxHealth = 70f;
const float ShootWithin = 380f; // stand and cast once this close

var dungeon = args.Length > 0 && Enum.TryParse<DungeonId>(args[0], ignoreCase: true, out var parsed)
    ? parsed
    : DungeonId.Crag;
var expectedScene = DungeonCatalog.Of(dungeon).ScenePath;

var listener = new EventBasedNetListener();
var net = new NetManager(listener) { AutoRecycle = true };
var assembler = new SnapshotAssembler();

int myId = -1;
uint sequence = 0;
var welcomed = false;
var sawMyClassAsMage = false;
var sawSpawnHealth = -1f;
var sawMagicBolt = false;
var enemyBaseline = new Dictionary<int, float>();
var sawEnemyHurt = false;
string? servedScene = null;
WorldSnapshotPacket? latest = null;

listener.PeerConnectedEvent += peer =>
{
    Console.WriteLine($"[probe] connected — forging an instance of {dungeon} as a Mage");
    peer.Send(NetProtocol.Frame(MessageType.JoinRequest,
        new JoinRequest
        {
            Name = "ClassProbe",
            Class = (byte)CharacterClass.Mage,
            Mode = (byte)JoinMode.Create,
            Dungeon = (byte)dungeon,
            InstanceName = "ClassProbe's raid",
        }),
        0, DeliveryMethod.ReliableOrdered);
};

listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
{
    switch ((MessageType)reader.GetByte())
    {
        case MessageType.RealmGeometry:
            var geometry = new RealmGeometryPacket();
            geometry.Deserialize(reader);
            servedScene = geometry.ScenePath;
            break;

        case MessageType.Welcome:
            var welcome = new WelcomePacket();
            welcome.Deserialize(reader);
            myId = welcome.PlayerId;
            welcomed = true;
            Console.WriteLine($"[probe] welcomed as player {myId}");
            break;

        case MessageType.WorldSnapshot:
            if (assembler.TryAdd(reader, out var snapshot))
                Ingest(snapshot);
            break;
    }
};

void Ingest(WorldSnapshotPacket snapshot)
{
    latest = snapshot;
    foreach (var p in snapshot.Players)
    {
        if (p.Id != myId)
            continue;
        if (p.Class == (byte)CharacterClass.Mage)
            sawMyClassAsMage = true;
        if (sawSpawnHealth < 0f)
            sawSpawnHealth = p.Health; // the first sighting is the untouched spawn health
    }

    foreach (var prj in snapshot.Projectiles)
        if (prj.Kind == (byte)ProjectileKind.MagicBolt)
            sawMagicBolt = true;

    foreach (var e in snapshot.Enemies)
    {
        if (enemyBaseline.TryGetValue(e.Id, out var baseline))
        {
            if (e.Health < baseline)
                sawEnemyHurt = true;
        }
        else
        {
            enemyBaseline[e.Id] = e.Health;
        }
    }
    // An enemy vanishing entirely (killed and pruned) also counts as hurt.
    if (enemyBaseline.Keys.Any(id => snapshot.Enemies.All(e => e.Id != id)))
        sawEnemyHurt = true;
}

// One bot tick: walk toward the nearest enemy until within casting comfort,
// then stand and hold the trigger aimed at it.
void SendIntent(NetPeer server)
{
    var me = latest?.Players.FirstOrDefault(p => p.Id == myId);
    if (latest is null || me is not { } self)
        return;

    var target = latest.Enemies
        .OrderBy(e => (e.X - self.X) * (e.X - self.X) + (e.Z - self.Z) * (e.Z - self.Z))
        .Cast<EnemySnapshot?>()
        .FirstOrDefault();
    if (target is not { } enemy)
        return;

    var dx = enemy.X - self.X;
    var dz = enemy.Z - self.Z;
    var dist = MathF.Sqrt(dx * dx + dz * dz);
    if (dist < 0.001f)
        return;

    var input = new InputPacket { Sequence = ++sequence };
    if (dist > ShootWithin)
    {
        input.MoveX = dx / dist;
        input.MoveZ = dz / dist;
    }
    else
    {
        input.Attack = true;
        input.AimX = dx / dist;
        input.AimZ = dz / dist;
    }
    server.Send(NetProtocol.Frame(MessageType.Input, input), 0, DeliveryMethod.ReliableOrdered);
}

net.Start();
var server = net.Connect("127.0.0.1", NetConfig.DefaultPort, NetConfig.ConnectionKey);
Console.WriteLine("[probe] dialing 127.0.0.1:9050 ...");

var clock = Stopwatch.StartNew();
var nextInput = TimeSpan.Zero;
while (clock.Elapsed < TimeSpan.FromSeconds(25))
{
    net.PollEvents();
    if (welcomed && clock.Elapsed >= nextInput)
    {
        SendIntent(server);
        nextInput = clock.Elapsed + TimeSpan.FromMilliseconds(33);
    }
    if (welcomed && sawMyClassAsMage && sawMagicBolt && sawEnemyHurt && sawSpawnHealth >= 0f)
        break; // everything observed — no need to run the clock out
    Thread.Sleep(5);
}
net.Stop();

var healthOk = MathF.Abs(sawSpawnHealth - MageMaxHealth) < 0.01f;
var sceneOk = servedScene == expectedScene;
Console.WriteLine($"A welcomed:              {(welcomed ? "PASS" : "FAIL")}");
Console.WriteLine($"B class byte = Mage:     {(sawMyClassAsMage ? "PASS" : "FAIL")}");
Console.WriteLine($"C spawn health = {MageMaxHealth}:    {(healthOk ? "PASS" : $"FAIL ({sawSpawnHealth})")}");
Console.WriteLine($"D MagicBolt in stream:   {(sawMagicBolt ? "PASS" : "FAIL")}");
Console.WriteLine($"E enemy took the hit:    {(sawEnemyHurt ? "PASS" : "FAIL")}");
Console.WriteLine($"F dungeon = {dungeon}:  {(sceneOk ? "PASS" : $"FAIL ({servedScene})")}");

var pass = welcomed && sawMyClassAsMage && healthOk && sawMagicBolt && sawEnemyHurt && sceneOk;
Console.WriteLine(pass ? "[probe] ALL CHECKS PASSED" : "[probe] FAILURES — see above");
return pass ? 0 : 1;

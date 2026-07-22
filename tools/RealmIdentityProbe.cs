// Verifies the identity-first join over a real socket, both ways round.
//
//   dotnet run tools/RealmIdentityProbe.cs [host]
//
// A raider that already ships the realm should wait for nothing; a raider that
// does not should still be sent it. The second half is the one that matters:
// the failure this design invites is a client wrongly believed, predicting on
// different stone from the server, and the symptom is rubber-banding with no
// visible cause. So the probe deliberately offers a WRONG digest and asserts the
// server corrects it rather than taking its word.
#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj
#:property PublishAot=false

using LiteNetLib;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

var host = args.Length > 0 ? args[0] : "127.0.0.1";
var realm = DungeonCatalog.Of(DungeonId.Crypt);

// The client's own copy, assembled exactly as WoadRaiders.Client's LocalRealms
// does: the baked map plus the navmesh artifact beside it.
var mapPath = Path.Combine("WoadRaiders.Client", "maps", realm.MapFile);
var navPath = Path.ChangeExtension(mapPath, ".navmesh");
if (!File.Exists(navPath))
{
    Console.WriteLine($"[probe] no {navPath} — run: dotnet run tools/GenerateRealm.cs Crypt");
    return 2;
}
var local = RealmSnapshot.From(RealmDefinitionFile.Load(mapPath), File.ReadAllBytes(navPath));
var digest = RealmSnapshot.Digest(local);
Console.WriteLine($"[probe] local {realm.MapFile}: digest {Convert.ToHexString(digest)[..16]}…");

var (matchWelcome, matchGeometry, matchBytes) = Join(digest);
Console.WriteLine($"[probe] offering the real digest  → UsedLocalRealm={matchWelcome?.UsedLocalRealm}, " +
                  $"geometry chunks={matchGeometry}");

// A stale build: same length, one bit different. The server must not take it.
var stale = (byte[])digest.Clone();
stale[0] ^= 0x01;
var (staleWelcome, staleGeometry, staleBytes) = Join(stale);
Console.WriteLine($"[probe] offering a stale digest   → UsedLocalRealm={staleWelcome?.UsedLocalRealm}, " +
                  $"geometry chunks={staleGeometry} ({staleBytes / 1024.0:0.0} KB)");

// And a client that ships nothing at all.
var (noneWelcome, noneGeometry, noneBytes) = Join(Array.Empty<byte>());
Console.WriteLine($"[probe] offering no digest        → UsedLocalRealm={noneWelcome?.UsedLocalRealm}, " +
                  $"geometry chunks={noneGeometry} ({noneBytes / 1024.0:0.0} KB)");

var a = matchWelcome is { UsedLocalRealm: true } && matchGeometry == 0;
var b = staleWelcome is { UsedLocalRealm: false } && staleGeometry >= 1;
var c = noneWelcome is { UsedLocalRealm: false } && noneGeometry >= 1;
var d = staleBytes == noneBytes && staleBytes > 0; // the fallback is the whole realm, unchanged

Console.WriteLine();
Console.WriteLine($"A a held realm is accepted, nothing sent: {(a ? "PASS" : "FAIL")}");
Console.WriteLine($"B a STALE digest is refused, realm rebuilt: {(b ? "PASS" : "FAIL")}");
Console.WriteLine($"C no digest at all, realm rebuilt:          {(c ? "PASS" : "FAIL")}");
Console.WriteLine($"D the fallback still carries everything:  {(d ? "PASS" : "FAIL")}");
var pass = a && b && c && d;
Console.WriteLine(pass
    ? $"[probe] ALL CHECKS PASSED — the Crypt's {noneBytes / 1024.0:0} KB opening wait is gone when the realm is held"
    : "[probe] FAILURES — see above");
return pass ? 0 : 1;

(WelcomePacket? Welcome, int GeometryChunks, int GeometryBytes) Join(byte[] offered)
{
    var listener = new EventBasedNetListener();
    var net = new NetManager(listener) { AutoRecycle = true };
    net.Start();
    net.Connect(host, NetConfig.DefaultPort, NetConfig.ConnectionKey);

    WelcomePacket? welcome = null;
    var assembler = new GeometryAssembler();
    var whole = false;
    var geometryCount = 0;
    var geometryBytes = 0;
    var joined = false;

    listener.NetworkReceiveEvent += (peer, reader, _, _) =>
    {
        var type = (MessageType)reader.GetByte();
        if (type == MessageType.RealmGeometry)
        {
            // Chunked since v21, so count the pieces AND rebuild — a realm that
            // arrives in fragments the client cannot reassemble is worse than
            // one that never arrived, because nothing says so.
            geometryCount++;
            geometryBytes += reader.AvailableBytes;
            if (assembler.TryAdd(reader, out _))
                whole = true;
        }
        else if (type == MessageType.Welcome)
        {
            var w = new WelcomePacket();
            w.Deserialize(reader);
            welcome = w;
        }
    };
    listener.PeerConnectedEvent += peer =>
    {
        joined = true;
        var join = new JoinRequest
        {
            Name = "Digest",
            Class = (byte)CharacterClass.Knight,
            Mode = (byte)JoinMode.Create,
            Dungeon = (byte)DungeonId.Crypt,
            RealmDigest = offered,
        };
        peer.Send(NetProtocol.Frame(MessageType.JoinRequest, join), 0, DeliveryMethod.ReliableOrdered);
    };

    // The welcome rides behind any geometry on the same reliable channel, so
    // once it lands the answer is complete either way.
    for (var i = 0; i < 600 && welcome is null; i++)
    {
        net.PollEvents();
        Thread.Sleep(10);
    }
    if (!joined)
        Console.WriteLine("[probe] never connected — is the server up?");
    net.Stop();
    // A realm was either not sent at all, or sent and rebuilt whole. "Some
    // chunks arrived" is never an acceptable outcome.
    if (geometryCount > 0 && !whole)
        Console.WriteLine($"[probe] {geometryCount} geometry chunks arrived but did not reassemble");
    return (welcome, whole ? geometryCount : 0, geometryBytes);
}

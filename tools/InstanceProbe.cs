// A scripted LiteNetLib probe that verifies dungeon-instance semantics end to
// end against a running dedicated server (see .claude/skills/verify). A .NET 10
// file-based app:
//
//   dotnet run --project WoadRaiders.Server        (in one shell)
//   dotnet run tools/InstanceProbe.cs              (in another)
//
// Four probe clients drive the create/list/join flow and assert, from the
// authoritative stream alone:
//   A) forging an instance yields a Welcome carrying its id,
//   B) a second client's instance list shows that raid (name, 1/8, dungeon),
//   C) joining by id lands in the SAME instance and both raiders share snapshots,
//   D) a third client's forge yields a DIFFERENT instance that only holds itself
//      (isolation — no cross-instance bleed),
//   E) the list reflects the filled instance (2/8),
//   F) joining a bogus id is denied with InstanceGone (connection survives).
// Exit code 0 = all pass. Run against a FRESH server for stable player counts.

#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj

using System.Diagnostics;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;

var a = new Probe("A");
var b = new Probe("B");
var c = new Probe("C");
var d = new Probe("D");
var probes = new[] { a, b, c, d };

var pass = true;
void Check(string label, bool ok)
{
    Console.WriteLine($"{label}: {(ok ? "PASS" : "FAIL")}");
    pass &= ok;
}

// Pump every probe's socket until the condition holds or the phase times out.
bool PumpUntil(Func<bool> done, double timeoutSeconds = 8)
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed.TotalSeconds < timeoutSeconds)
    {
        foreach (var probe in probes)
            probe.Poll();
        if (done())
            return true;
        Thread.Sleep(5);
    }
    return done();
}

Console.WriteLine("[probe] dialing 127.0.0.1:9050 ...");

// A forges a raid and is welcomed into it.
a.Start();
PumpUntil(() => a.ConnectedToServer);
a.Forge("Probe Alpha", DungeonId.Barrow);
PumpUntil(() => a.Welcomed);
Check("A forge welcomed with an instance id", a.Welcomed && a.InstanceId > 0);

// B browses and sees A's raid in the list.
b.Start();
PumpUntil(() => b.ConnectedToServer);
b.RequestList();
PumpUntil(() => b.LastList is not null);
var listed = b.LastList?.Instances.Where(e => e.Id == a.InstanceId).Cast<InstanceEntry?>().FirstOrDefault();
Check("B's list shows A's raid as 1/8 in the Barrow",
    listed is { } entry && entry.Name == "Probe Alpha" && entry.Players == 1
    && entry.MaxPlayers == NetConfig.MaxPlayersPerInstance && entry.Dungeon == (byte)DungeonId.Barrow);

// B joins that raid by id and both raiders share one world.
b.Join(a.InstanceId);
PumpUntil(() => b.Welcomed);
Check("B joined A's instance", b.Welcomed && b.InstanceId == a.InstanceId);
PumpUntil(() => b.LatestPlayerIds.Length == 2);
Check("B's snapshots hold both raiders",
    b.LatestPlayerIds.Length == 2
    && b.LatestPlayerIds.Contains(a.PlayerId) && b.LatestPlayerIds.Contains(b.PlayerId));
Check("B's snapshots carry raider names (for nameplates)",
    b.LatestPlayerNames.Contains("Probe-A") && b.LatestPlayerNames.Contains("Probe-B"));

// C forges its own raid — a different instance holding only C.
c.Start();
PumpUntil(() => c.ConnectedToServer);
c.Forge("Probe Gamma", DungeonId.Barrow);
PumpUntil(() => c.Welcomed);
Check("C forged a separate instance", c.Welcomed && c.InstanceId > 0 && c.InstanceId != a.InstanceId);
PumpUntil(() => c.SnapshotsSeen >= 5);
Check("C's world holds C alone (no cross-instance bleed)",
    c.SnapshotsSeen >= 5 && !c.SawForeignPlayer);

// D browses (sees the filled raid), then knocks on a door that isn't there.
d.Start();
PumpUntil(() => d.ConnectedToServer);
d.RequestList();
PumpUntil(() => d.LastList is not null);
var filled = d.LastList?.Instances.Where(e => e.Id == a.InstanceId).Cast<InstanceEntry?>().FirstOrDefault();
Check("D's list shows A's raid filled to 2/8", filled is { Players: 2 });
d.Join(999_999);
PumpUntil(() => d.Denial is not null);
Check("D's bogus join denied as gone",
    d.Denial is { } denial && denial.Reason == (byte)JoinDenyReason.InstanceGone && d.ConnectedToServer);

foreach (var probe in probes)
    probe.Stop();

Console.WriteLine(pass ? "[probe] ALL CHECKS PASSED" : "[probe] FAILURES — see above");
return pass ? 0 : 1;

/// <summary>One scripted client: connects, forges/joins/lists on command, and
/// records what the authoritative stream shows it.</summary>
sealed class Probe
{
    private readonly string _tag;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly SnapshotAssembler _assembler = new();
    private NetPeer? _server;

    public bool ConnectedToServer { get; private set; }
    public bool Welcomed { get; private set; }
    public int PlayerId { get; private set; } = -1;
    public int InstanceId { get; private set; } = -1;
    public InstanceListPacket? LastList { get; private set; }
    public JoinDeniedPacket? Denial { get; private set; }
    public int SnapshotsSeen { get; private set; }
    public int[] LatestPlayerIds { get; private set; } = [];
    public string[] LatestPlayerNames { get; private set; } = [];

    /// <summary>True if any post-welcome snapshot ever contained a player other than us.</summary>
    public bool SawForeignPlayer { get; private set; }

    public Probe(string tag)
    {
        _tag = tag;
        _net = new NetManager(_listener) { AutoRecycle = true };

        _listener.PeerConnectedEvent += peer =>
        {
            _server = peer;
            ConnectedToServer = true;
            Console.WriteLine($"[{_tag}] connected");
        };
        _listener.PeerDisconnectedEvent += (_, info) =>
        {
            ConnectedToServer = false;
            Console.WriteLine($"[{_tag}] disconnected ({info.Reason})");
        };
        _listener.NetworkReceiveEvent += (_, reader, _, _) =>
        {
            switch ((MessageType)reader.GetByte())
            {
                case MessageType.Welcome:
                    var welcome = new WelcomePacket();
                    welcome.Deserialize(reader);
                    PlayerId = welcome.PlayerId;
                    InstanceId = welcome.InstanceId;
                    Welcomed = true;
                    Console.WriteLine($"[{_tag}] welcomed as player {PlayerId} into instance #{InstanceId}");
                    break;

                case MessageType.InstanceList:
                    var list = new InstanceListPacket();
                    list.Deserialize(reader);
                    LastList = list;
                    break;

                case MessageType.JoinDenied:
                    var denial = new JoinDeniedPacket();
                    denial.Deserialize(reader);
                    Denial = denial;
                    Console.WriteLine($"[{_tag}] join denied ({(JoinDenyReason)denial.Reason})");
                    break;

                case MessageType.WorldSnapshot:
                    if (_assembler.TryAdd(reader, out var snapshot) && Welcomed)
                    {
                        SnapshotsSeen++;
                        LatestPlayerIds = snapshot.Players.Select(p => p.Id).ToArray();
                        LatestPlayerNames = snapshot.Players.Select(p => p.Name).ToArray();
                        if (snapshot.Players.Any(p => p.Id != PlayerId))
                            SawForeignPlayer = true;
                    }
                    break;
            }
        };
    }

    public void Start()
    {
        _net.Start();
        _net.Connect("127.0.0.1", NetConfig.DefaultPort, NetConfig.ConnectionKey);
    }

    public void Poll() => _net.PollEvents();
    public void Stop() => _net.Stop();

    public void RequestList() =>
        Send(MessageType.InstanceListRequest, new InstanceListRequestPacket());

    public void Forge(string instanceName, DungeonId dungeon) =>
        Send(MessageType.JoinRequest, new JoinRequest
        {
            Name = $"Probe-{_tag}",
            Class = (byte)CharacterClass.Knight,
            Mode = (byte)JoinMode.Create,
            Dungeon = (byte)dungeon,
            InstanceName = instanceName,
        });

    public void Join(int instanceId) =>
        Send(MessageType.JoinRequest, new JoinRequest
        {
            Name = $"Probe-{_tag}",
            Class = (byte)CharacterClass.Knight,
            Mode = (byte)JoinMode.Join,
            InstanceId = instanceId,
        });

    private void Send(MessageType type, LiteNetLib.Utils.INetSerializable packet) =>
        _server?.Send(NetProtocol.Frame(type, packet), 0, DeliveryMethod.ReliableOrdered);
}

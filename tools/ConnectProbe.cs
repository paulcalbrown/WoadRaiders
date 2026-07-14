// A scripted LiteNetLib probe that verifies the connect-refusal handshake end
// to end against a running dedicated server (see .claude/skills/verify). A
// .NET 10 file-based app:
//
//   dotnet run --project WoadRaiders.Server              (in one shell)
//   dotnet run tools/ConnectProbe.cs [host[:port]]       (in another)
//
// It dials the server three ways and asserts, from the wire alone:
//   A) a stale connection key is rejected, and the reject carries a
//      ConnectDenied payload naming the server's key and a message that
//      points at the download URL (the payload's format is frozen — this is
//      the check to re-run at every ConnectionKey bump),
//   B) connect data that is not even a string is rejected the same way,
//   C) the current key still connects.
// Exit code 0 = all pass.

#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj

using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;
using WoadRaiders.Shared;

var (host, port) = NetConfig.ParseEndpoint(args.Length > 0 ? args[0] : null);
var failures = 0;
Console.WriteLine($"[probe] target {host}:{port}");

// --- A: a stale build ---------------------------------------------------
var stale = Dial("stale key \"WoadRaiders.v0\"", net => net.Connect(host, port, "WoadRaiders.v0"));
Check(!stale.Connected, "a stale build is not let in");
Check(stale.Reason == DisconnectReason.ConnectionRejected, "the refusal is a connection reject");
Check(stale.Denial != null, "the reject carries a ConnectDenied payload");
Check(stale.Denial?.ServerKey == NetConfig.ConnectionKey,
    $"the payload names the server's key ({NetConfig.ConnectionKey})");
Check(stale.Denial?.Message.Contains(NetConfig.DownloadUrl) == true,
    "the message points at the download URL");

// --- B: hostile connect data --------------------------------------------
var junkData = new NetDataWriter();
junkData.Put(0xDEADBEEF); // not a LiteNetLib string — the server must still answer, not choke
var junk = Dial("non-string connect data", net => net.Connect(host, port, junkData));
Check(!junk.Connected, "junk connect data is not let in");
Check(junk.Denial != null, "even junk is told why (ConnectDenied payload)");

// --- C: the current build ------------------------------------------------
var current = Dial($"current key \"{NetConfig.ConnectionKey}\"",
    net => net.Connect(host, port, NetConfig.ConnectionKey));
Check(current.Connected, "the matching build connects");

Console.WriteLine(failures == 0 ? "[probe] ALL CHECKS PASSED" : $"[probe] {failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

// One dial, one outcome: connected, or refused (with the parsed denial, if any).
(bool Connected, ConnectDeniedPacket? Denial, DisconnectReason? Reason) Dial(
    string label, Action<NetManager> connect)
{
    var listener = new EventBasedNetListener();
    var net = new NetManager(listener) { AutoRecycle = true };
    bool connected = false, done = false;
    ConnectDeniedPacket? denial = null;
    DisconnectReason? reason = null;

    listener.PeerConnectedEvent += _ => { connected = true; done = true; };
    listener.PeerDisconnectedEvent += (_, info) =>
    {
        reason = info.Reason;
        if (info.AdditionalData is { AvailableBytes: > 0 } payload)
        {
            try
            {
                var packet = new ConnectDeniedPacket();
                packet.Deserialize(payload);
                denial = packet;
            }
            catch { /* an unparseable payload counts as none */ }
        }
        done = true;
    };

    net.Start();
    connect(net);
    var clock = Stopwatch.StartNew();
    while (!done && clock.Elapsed < TimeSpan.FromSeconds(5))
    {
        net.PollEvents();
        Thread.Sleep(10);
    }
    net.Stop();

    Console.WriteLine($"[probe] {label}: connected={connected}, reason={reason?.ToString() ?? "none"}, " +
        (denial is null ? "no denial payload" : $"denial key=\"{denial.ServerKey}\" message=\"{denial.Message}\""));
    return (connected, denial, reason);
}

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}

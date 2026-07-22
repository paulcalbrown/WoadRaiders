// Where does a single ReliableOrdered send actually stop working?
//
// tools/MeasureFragments.cs reasons from LiteNetLib's constants and concludes a
// ~37 MB "safe" ceiling from MaxSequence. That is a claim about a sliding
// window read off a field name, and it deserves a test before a spec is written
// against it: two NetManagers on loopback, one big payload, does it arrive whole.
//
//   dotnet run tools/MeasureReliableLimit.cs [maxMB]
#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj
#:property PublishAot=false

using System.Diagnostics;
using LiteNetLib;

var ceiling = args.Length > 0 ? int.Parse(args[0]) : 96;
const int Port = 9077;

var serverListener = new EventBasedNetListener();
var server = new NetManager(serverListener) { AutoRecycle = true, DisconnectTimeout = 120_000 };
serverListener.ConnectionRequestEvent += r => r.AcceptIfKey("size");
server.Start(Port);

var clientListener = new EventBasedNetListener();
var client = new NetManager(clientListener) { AutoRecycle = true, DisconnectTimeout = 120_000 };
byte[]? received = null;
clientListener.NetworkReceiveEvent += (_, reader, _, _) => received = reader.GetRemainingBytes();
client.Start();
client.Connect("127.0.0.1", Port, "size");

for (var i = 0; i < 400 && server.ConnectedPeersCount == 0; i++)
{
    server.PollEvents();
    client.PollEvents();
    Thread.Sleep(10);
}
if (server.ConnectedPeersCount == 0)
{
    Console.WriteLine("could not connect on loopback");
    return 2;
}
var peer = server.FirstPeer;

Console.WriteLine($"{"MB",6}{"fragments",12}{"result",14}{"seconds",10}{"MB/s",9}");
foreach (var mb in new[] { 52, 56, 60, 62, 64 })
{
    var payload = new byte[mb * 1024 * 1024];
    // A pattern, so a wrong reassembly is caught rather than a zeroed buffer.
    for (var i = 0; i < payload.Length; i++)
        payload[i] = (byte)(i * 31 + i / 977);

    received = null;
    var fragments = payload.Length / 1186 + 1;
    var sw = Stopwatch.StartNew();
    string result;
    try
    {
        peer.Send(payload, DeliveryMethod.ReliableOrdered);
        // Loopback still needs both ends pumped; give it generous wall-clock.
        while (received is null && sw.Elapsed < TimeSpan.FromSeconds(120))
        {
            server.PollEvents();
            client.PollEvents();
            Thread.Sleep(1);
        }
        result = received is null ? "TIMED OUT"
            : received.Length != payload.Length ? $"TRUNCATED {received.Length}"
            : received.AsSpan().SequenceEqual(payload) ? "intact" : "CORRUPT";
    }
    catch (Exception e)
    {
        result = e.GetType().Name; // TooBigPacketException is the documented refusal
    }
    sw.Stop();
    Console.WriteLine($"{mb,6}{fragments,12}{result,14}{sw.Elapsed.TotalSeconds,10:0.0}" +
                      $"{mb / Math.Max(0.001, sw.Elapsed.TotalSeconds),9:0.0}");
}

client.Stop();
server.Stop();
return 0;

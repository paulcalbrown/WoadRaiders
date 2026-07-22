// Can the transport actually carry a big realm? A budget the wire cannot deliver
// is worse than a small one — it fails at join time, on the rare path, in front
// of whoever was unlucky enough to lack the realm.
//
// LiteNetLib fragments ReliableOrdered sends and numbers the fragments in a
// ushort, so there is a hard ceiling. This finds it from the library itself
// rather than from a blog post, and reports what it means in megabytes.
//
//   dotnet run tools/MeasureFragments.cs
#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj
#:property PublishAot=false

using System.Reflection;
using LiteNetLib;

var asm = typeof(NetPeer).Assembly;
Console.WriteLine($"LiteNetLib {asm.GetName().Version}");

var constants = asm.GetType("LiteNetLib.NetConstants");
if (constants is null)
{
    Console.WriteLine("NetConstants not found — check the package version");
    return 1;
}

foreach (var field in constants.GetFields(BindingFlags.Public | BindingFlags.Static)
             .Where(f => f.IsLiteral)
             .OrderBy(f => f.Name))
{
    var name = field.Name;
    if (name.Contains("Fragment", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Packet", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Mtu", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Max", StringComparison.OrdinalIgnoreCase))
        Console.WriteLine($"  {name,-32} {field.GetRawConstantValue()}");
}

// TWO ceilings, and the lower one is not the one the library throws on.
//
//   HARD  — NetPeer refuses a send needing more than ushort.MaxValue fragments.
//   SAFE  — every fragment consumes a reliable SEQUENCE slot, and the sequence
//           space is MaxSequence (32768). A single message needing more
//           fragments than that wraps the sequence while still incomplete,
//           which is not a refusal — it is a stall or a corrupt reassembly, on
//           the rare path, at join time. Design against this one.
var maxSequence = Convert.ToInt32(constants.GetField("MaxSequence")!.GetRawConstantValue());
var fragHeader = Convert.ToInt32(constants.GetField("FragmentedHeaderTotalSize")!.GetRawConstantValue());

Console.WriteLine();
Console.WriteLine($"{"MTU",6}{"payload",10}{"hard (ushort)",16}{"SAFE (MaxSequence)",22}");
foreach (var mtu in new[] { 1200, 1432 })
{
    var payload = mtu - fragHeader - 4; // fragment header + channel/type framing
    Console.WriteLine($"{mtu,6}{payload,10}" +
                      $"{ushort.MaxValue * (long)payload / 1048576.0,15:0.0} MB" +
                      $"{maxSequence * (long)payload / 1048576.0,19:0.0} MB");
}
Console.WriteLine();
Console.WriteLine("Geometry rides ReliableOrdered on channel 0, shared with gameplay traffic,");
Console.WriteLine($"at ~0.9 MB/s (90 KB window / 100 ms RTT) — so the SAFE ceiling is also");
Console.WriteLine($"{maxSequence * 1180L / 1048576.0 / 0.9:0} seconds of waiting.");
return 0;

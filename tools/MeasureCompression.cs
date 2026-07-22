// What the fallback geometry send COSTS, stage by stage — and what brotli's
// compression level actually buys.
//
// This is the measurement behind REALM-C-026 and the level chosen in
// RealmGeometryPacket.Pack(). The v21 server compressed at SmallestSize, lazily,
// on the game loop: 15,026 ms for the shipping Crypt, during which every
// instance froze and nothing could connect. Re-run it whenever a realm grows or
// the packet's contents change.
//
//   dotnet run tools/MeasureCompression.cs -c Release
#:project C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Core/WoadRaiders.Core.csproj
#:project C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Shared/WoadRaiders.Shared.csproj
#:property PublishAot=false
using System.Diagnostics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

var path = "C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Client/maps/Crypt.json";
var sw = Stopwatch.StartNew();
var realm = RealmDefinitionFile.Load(path);
Console.WriteLine($"  load Crypt.json            {sw.ElapsedMilliseconds,7} ms   ({realm.Soup!.Triangles.Length / 3} triangles)");

sw.Restart();
var navData = NavMeshBuilder.BuildMeshData(realm.Soup!);
Console.WriteLine($"  BuildMeshData (Recast)     {sw.ElapsedMilliseconds,7} ms   <-- navmesh BAKE");
sw.Restart();
var nav = NavMeshBuilder.Serialize(navData);
Console.WriteLine($"  NavMesh Serialize          {sw.ElapsedMilliseconds,7} ms   ({nav.Length / 1024} KB)");

var packet = new RealmGeometryPacket
{
    SoupVertices = realm.Soup!.Vertices,
    SoupTriangles = realm.Soup!.Triangles,
    NavMesh = nav,
};
sw.Restart();
var w = new NetDataWriter();
packet.Serialize(w);
Console.WriteLine($"  RealmGeometryPacket ser.   {sw.ElapsedMilliseconds,7} ms   ({w.Length / 1024} KB on the wire)");
sw.Restart();
var chunks = GeometryChunks.Split(packet);
Console.WriteLine($"  GeometryChunks.Split       {sw.ElapsedMilliseconds,7} ms   ({chunks.Count} chunk(s))");

// What the compression level is actually buying. The realm is float and int
// arrays — highly structured — so the cheap levels may lose very little.
var raw = new byte[realm.Soup!.Vertices.Length * 4 + realm.Soup!.Triangles.Length * 4 + nav.Length + 12];
Buffer.BlockCopy(realm.Soup!.Vertices, 0, raw, 4, realm.Soup!.Vertices.Length * 4);
Buffer.BlockCopy(realm.Soup!.Triangles, 0, raw, 8 + realm.Soup!.Vertices.Length * 4, realm.Soup!.Triangles.Length * 4);
Console.WriteLine($"{Environment.NewLine}  raw payload: {raw.Length / 1024} KB{Environment.NewLine}");
Console.WriteLine("  level              compress      size    inflate");
foreach (var level in new[] { System.IO.Compression.CompressionLevel.SmallestSize,
                              System.IO.Compression.CompressionLevel.Optimal,
                              System.IO.Compression.CompressionLevel.Fastest })
{
    sw.Restart();
    using var packed = new MemoryStream();
    using (var b = new System.IO.Compression.BrotliStream(packed, level, leaveOpen: true))
        b.Write(raw, 0, raw.Length);
    var ms = sw.ElapsedMilliseconds;
    var bytes = packed.ToArray();
    sw.Restart();
    using (var src = new MemoryStream(bytes, false))
    using (var b = new System.IO.Compression.BrotliStream(src, System.IO.Compression.CompressionMode.Decompress))
    {
        var sink = new byte[raw.Length]; var filled = 0; int n;
        while (filled < raw.Length && (n = b.Read(sink, filled, raw.Length - filled)) > 0) filled += n;
    }
    Console.WriteLine($"  {level,-14} {ms,8} ms  {bytes.Length / 1024,6} KB  {sw.ElapsedMilliseconds,6} ms");
}

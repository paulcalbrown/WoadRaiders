// What a raider actually waits for on join, and which half of it a realm's
// triangle budget can even influence. The answer inverts with scale: at the
// shipping Crypt's 2,280 triangles the NAVMESH is ~88% of the payload and the
// triangle budget is nearly irrelevant; at CryptDesign v2's 70,000-triangle
// ceiling the soup is ~91% and the navmesh is noise.
//
// READ THE LAST ROW AS AN UPPER BOUND, NOT A PREDICTION. Its soup is random
// points, which is the worst case for both of the things that make a realm
// small: SoupBuilder welds only EXACT-bit-matching corners (random points share
// none) and brotli finds no structure. Real architecture — repeated instances of
// a shared mesh library, axis-aligned, on a half-module grid — welds and
// compresses far better: 131,425 real triangles measured at 0.51 MB on
// 2026-07-20, four times better than this synthetic manages at half the count.
//
//   dotnet run tools/MeasureJoin.cs
#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj
#:property PublishAot=false

using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

Console.WriteLine($"{"realm",-8}{"tris",8}{"soup raw",10}{"navmesh",10}{"packet",10}{"nav %",8}{"join @0.9MB/s",15}");
foreach (var info in DungeonCatalog.All)
{
    var path = Path.Combine("WoadRaiders.Client", "maps", info.MapFile);
    var realm = RealmDefinitionFile.Load(path);
    var soup = realm.Soup!;

    var navMesh = NavMeshBuilder.Serialize(NavMeshBuilder.BuildMeshData(soup));
    var packet = RealmSnapshot.From(realm, navMesh);
    var w = new NetDataWriter();
    packet.Serialize(w);

    // Raw soup on the wire before welding/compression: 3 floats a vertex + 3 ints a triangle.
    var soupRaw = soup.Vertices.Length * 4 + soup.Triangles.Length * 4;
    Console.WriteLine($"{info.MapFile.Replace(".json", ""),-8}{soup.Triangles.Length / 3,8}" +
                      $"{soupRaw / 1024.0,9:0.0}K{navMesh.Length / 1024.0,9:0.0}K{w.Length / 1024.0,9:0.0}K" +
                      $"{100.0 * navMesh.Length / (navMesh.Length + soupRaw),7:0}%{w.Length / 921600.0,14:0.00}s");
}

// And what CryptDesign v2 is budgeted at: the same realm's navmesh, but a soup
// grown to the spec's 70,000-triangle ceiling. Does the wire actually bind?
{
    var realm = RealmDefinitionFile.Load(Path.Combine("WoadRaiders.Client", "maps", "Crypt.json"));
    var navMesh = NavMeshBuilder.Serialize(NavMeshBuilder.BuildMeshData(realm.Soup!));

    // A synthetic soup of N triangles over the realm's own extent — the shape is
    // irrelevant, the byte count is not.
    const int Target = 70_000;
    var rng = new Random(11);
    var min = realm.Bounds.Min;
    var size = realm.Bounds.Size;
    var verts = new float[Target * 9];
    for (var i = 0; i < verts.Length; i += 3)
    {
        verts[i] = min.X + (float)rng.NextDouble() * size.X;
        verts[i + 1] = min.Y + (float)rng.NextDouble() * size.Y;
        verts[i + 2] = min.Z + (float)rng.NextDouble() * size.Z;
    }
    var tris = new int[Target * 3];
    for (var i = 0; i < tris.Length; i++)
        tris[i] = i;

    var big = RealmSnapshot.From(
        new RealmDefinition(realm.SpawnPoint, new TriangleSoup(verts, tris), Array.Empty<EnemySpawnPoint>()),
        navMesh);
    var bw = new NetDataWriter();
    big.Serialize(bw);
    Console.WriteLine($"{"v2 cap",-8}{Target,8}{(verts.Length * 4 + tris.Length * 4) / 1024.0,9:0.0}K" +
                      $"{navMesh.Length / 1024.0,9:0.0}K{bw.Length / 1024.0,9:0.0}K" +
                      $"{100.0 * navMesh.Length / (navMesh.Length + verts.Length * 4 + tris.Length * 4),7:0}%" +
                      $"{bw.Length / 921600.0,14:0.00}s");
}

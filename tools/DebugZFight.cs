// Find coplanar overlapping surfaces — the geometry cause of "texture
// flickering." Two faces at the same depth, pointing the same way, fighting for
// the same pixels: the GPU picks one or the other per frame and it shimmers as
// the camera moves. This bins triangles by facing and location and flags stacks
// that sit within a hair of each other in their shared plane.
//
//   dotnet run tools/DebugZFight.cs -c Release
#:project C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false
using System.Numerics;
using WoadRaiders.Core;

var realm = RealmDefinitionFile.Load("C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Client/maps/Crypt.json");
var soup = realm.Soup!;
Vector3 V(int i) => new(soup.Vertices[i * 3], soup.Vertices[i * 3 + 1], soup.Vertices[i * 3 + 2]);

// Bin every triangle by its FACING and the location IN its own plane, then flag
// stacks that sit within a hair of each other along the facing axis — two faces
// pointing the same way at the same depth, which is the z-fight. Up-faces are
// floors and stone tops; side-faces are walls (the corners, the layered
// facings). Reporting both is the only way to know which the eye actually sees.
//   bucket key: (facing 0=+X 1=-X 2=+Z 3=-Z 4=up, in-plane cell) -> offsets
var floorBins = new Dictionary<(int, int), List<float>>();
var wallBins = new Dictionary<(int, int, int), List<float>>();
var count = soup.Triangles.Length / 3;
for (var t = 0; t < count; t++)
{
    var a = V(soup.Triangles[t * 3]);
    var b = V(soup.Triangles[t * 3 + 1]);
    var c = V(soup.Triangles[t * 3 + 2]);
    var n = Vector3.Cross(b - a, c - a);
    if (n.LengthSquared() < 1e-6f) continue;
    n = Vector3.Normalize(n);
    var cx = (a.X + b.X + c.X) / 3f;
    var cy = (a.Y + b.Y + c.Y) / 3f;
    var cz = (a.Z + b.Z + c.Z) / 3f;
    if (n.Y > 0.87f)                              // FLOOR: bin XZ, stack Y
    {
        var key = ((int)MathF.Round(cx / 16f), (int)MathF.Round(cz / 16f));
        (floorBins.TryGetValue(key, out var ys) ? ys : floorBins[key] = new()).Add(cy);
    }
    else if (MathF.Abs(n.Y) < 0.2f)               // WALL: bin (facing, in-plane), stack depth
    {
        var facesX = MathF.Abs(n.X) > MathF.Abs(n.Z);
        var facing = facesX ? (n.X > 0 ? 0 : 1) : (n.Z > 0 ? 2 : 3);
        var inPlane = facesX ? cz : cx;
        var key = (facing, (int)MathF.Round(inPlane / 16f), (int)MathF.Round(cy / 16f));
        var depth = facesX ? cx : cz;
        (wallBins.TryGetValue(key, out var ds) ? ds : wallBins[key] = new()).Add(depth);
    }
}

int Overlaps(IEnumerable<List<float>> bins)
{
    var n = 0;
    foreach (var vals in bins)
    {
        vals.Sort();
        for (var i = 1; i < vals.Count; i++)
            if (vals[i] - vals[i - 1] is > 0.001f and < 1.5f) n++;
    }
    return n;
}
Console.WriteLine($"WALL overlaps (coplanar same-facing side faces): {Overlaps(wallBins.Values)}");
Console.WriteLine($"FLOOR overlaps (coplanar up faces):             {Overlaps(floorBins.Values)}\n");

var floors = floorBins;
var hits = new List<(float X, float Z, float Y, float Gap)>();
foreach (var (key, ys) in floors)
{
    ys.Sort();
    for (var i = 1; i < ys.Count; i++)
    {
        var gap = ys[i] - ys[i - 1];
        if (gap < 1.5f && gap > 0.001f)          // coplanar, not the same triangle
            hits.Add((key.Item1 * 16f, key.Item2 * 16f, ys[i], gap));
    }
}

Console.WriteLine($"the FLOOR overlaps by place:\n");
var clusters = new List<(Vector3 At, int Count, float MinGap)>();
foreach (var h in hits)
{
    var p = new Vector3(h.X, h.Y, h.Z);
    var found = false;
    for (var i = 0; i < clusters.Count; i++)
        if (MathF.Abs(clusters[i].At.X - p.X) < 200f && MathF.Abs(clusters[i].At.Z - p.Z) < 200f &&
            MathF.Abs(clusters[i].At.Y - p.Y) < 40f)
        {
            clusters[i] = (clusters[i].At, clusters[i].Count + 1, MathF.Min(clusters[i].MinGap, h.Gap));
            found = true;
            break;
        }
    if (!found) clusters.Add((p, 1, h.Gap));
}

if (clusters.Count == 0)
    Console.WriteLine("  none.");
foreach (var c in clusters.OrderByDescending(c => c.Count).Take(25))
    Console.WriteLine($"  ({c.At.X,6:0}, {c.At.Y,6:0}, {c.At.Z,6:0})  {c.Count,4} cell(s), gap as small as {c.MinGap:0.00}");
Console.WriteLine($"\n{hits.Count} overlapping floor cells in {clusters.Count} clusters.");

// Dump the exact vertices of coplanar overlapping faces in the Broken Porch, so
// the doubled geometry is named rather than guessed. Precise: real 2D overlap,
// not binning; skips the two triangles of a single quad (shared >= 2 verts).
#:project C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false
using System.Numerics;
using WoadRaiders.Core;
var soup = RealmDefinitionFile.Load("C:/Users/Paul/RiderProjects/WoadRaiders/WoadRaiders.Client/maps/Crypt.json").Soup!;
Vector3 V(int i) => new(soup.Vertices[i * 3], soup.Vertices[i * 3 + 1], soup.Vertices[i * 3 + 2]);

var tris = new List<(int Facing, float Depth, Vector2 Lo, Vector2 Hi, int A, int B, int C)>();
for (var t = 0; t < soup.Triangles.Length / 3; t++)
{
    int ia = soup.Triangles[t * 3], ib = soup.Triangles[t * 3 + 1], ic = soup.Triangles[t * 3 + 2];
    var a = V(ia); var b = V(ib); var c = V(ic);
    if (a.X < -10 || a.X > 730 || a.Z < 1830 || a.Z > 2570) continue;
    var n = Vector3.Cross(b - a, c - a); if (n.LengthSquared() < 1e-6f) continue; n = Vector3.Normalize(n);
    if (MathF.Abs(n.Y) > 0.2f) continue;
    var facesX = MathF.Abs(n.X) > MathF.Abs(n.Z);
    var facing = facesX ? (n.X > 0 ? 0 : 1) : (n.Z > 0 ? 2 : 3);
    var depth = facesX ? (a.X + b.X + c.X) / 3 : (a.Z + b.Z + c.Z) / 3;
    Vector2 P(Vector3 v) => facesX ? new Vector2(v.Z, v.Y) : new Vector2(v.X, v.Y);
    var p0 = P(a); var p1 = P(b); var p2 = P(c);
    tris.Add((facing, depth, Vector2.Min(p0, Vector2.Min(p1, p2)), Vector2.Max(p0, Vector2.Max(p1, p2)), ia, ib, ic));
}

int fights = 0, shown = 0;
for (var i = 0; i < tris.Count; i++)
for (var j = i + 1; j < tris.Count; j++)
{
    var x = tris[i]; var y = tris[j];
    if (x.Facing != y.Facing || MathF.Abs(x.Depth - y.Depth) > 0.4f) continue;
    var shared = 0;
    foreach (var v in new[] { y.A, y.B, y.C }) if (v == x.A || v == x.B || v == x.C) shared++;
    if (shared >= 2) continue;
    var ox = MathF.Min(x.Hi.X, y.Hi.X) - MathF.Max(x.Lo.X, y.Lo.X);
    var oy = MathF.Min(x.Hi.Y, y.Hi.Y) - MathF.Max(x.Lo.Y, y.Lo.Y);
    if (ox <= 2f || oy <= 2f) continue;
    fights++;
    if (shown++ >= 6) continue;
    var A = V(x.A); var B = V(x.B); var C = V(x.C);
    var D = V(y.A); var E = V(y.B); var F = V(y.C);
    Console.WriteLine($"overlap {ox:0}x{oy:0}, facing {x.Facing}, plane {x.Depth:0.0}:");
    Console.WriteLine($"   face 1: ({A.X:0},{A.Y:0},{A.Z:0}) ({B.X:0},{B.Y:0},{B.Z:0}) ({C.X:0},{C.Y:0},{C.Z:0})");
    Console.WriteLine($"   face 2: ({D.X:0},{D.Y:0},{D.Z:0}) ({E.X:0},{E.Y:0},{E.Z:0}) ({F.X:0},{F.Y:0},{F.Z:0})");
}
Console.WriteLine($"\ntotal real overlapping coplanar face pairs in the porch: {fights}");

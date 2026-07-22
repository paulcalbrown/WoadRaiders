using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The soup's spatial index, checked against the only reference that cannot
/// itself be wrong: testing every triangle. The BVH exists to make queries
/// cheap on geometry that clumps, and the one thing it must never do is
/// change an answer — server and client run these same queries over the same
/// bytes, so a pruning bug would not crash, it would silently desync
/// prediction from the authoritative sim.
/// </summary>
public class TriangleSoupTests
{
    // ---- brute force: the same maths, over ALL triangles, no index at all ----

    private static (Vector3 a, Vector3 b, Vector3 c) Corners(TriangleSoup s, int t)
    {
        Vector3 V(int i) => new(s.Vertices[i * 3], s.Vertices[i * 3 + 1], s.Vertices[i * 3 + 2]);
        return (V(s.Triangles[t * 3]), V(s.Triangles[t * 3 + 1]), V(s.Triangles[t * 3 + 2]));
    }

    private static float? HeightAt(TriangleSoup s, int t, float x, float z)
    {
        var (a, b, c) = Corners(s, t);
        var denom = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(denom) < 1e-9f)
            return null;
        var wa = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / denom;
        var wb = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / denom;
        var wc = 1f - wa - wb;
        if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f)
            return null;
        return wa * a.Y + wb * b.Y + wc * c.Y;
    }

    private static float NormalY(TriangleSoup s, int t)
    {
        var (a, b, c) = Corners(s, t);
        var n = Vector3.Cross(b - a, c - a);
        var len = n.Length();
        return len > 1e-12f ? n.Y / len : 0f;
    }

    private static bool RayTriangle(TriangleSoup s, int t, Vector3 origin, Vector3 d, out float hit)
    {
        hit = 0f;
        var (a, b, c) = Corners(s, t);
        var e1 = b - a;
        var e2 = c - a;
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;
        var inv = 1f / det;
        var q0 = origin - a;
        var u = Vector3.Dot(q0, p) * inv;
        if (u is < -1e-5f or > 1.00001f)
            return false;
        var q = Vector3.Cross(q0, e1);
        var v = Vector3.Dot(d, q) * inv;
        if (v < -1e-5f || u + v > 1.00001f)
            return false;
        hit = Vector3.Dot(e2, q) * inv;
        return hit > 0f;
    }

    private static bool BruteSegmentHits(TriangleSoup s, Vector3 from, Vector3 to, bool blockersOnly)
    {
        var d = to - from;
        for (var t = 0; t < s.Triangles.Length / 3; t++)
        {
            if (blockersOnly && MathF.Abs(NormalY(s, t)) > TriangleSoup.WallNormalY)
                continue;
            if (RayTriangle(s, t, from, d, out var h) && h is > 1e-4f and < 1f - 1e-4f)
                return true;
        }
        return false;
    }

    private static float? BruteGroundBelow(TriangleSoup s, float x, float z, float referenceY, float tolerance)
    {
        float? below = null, lowest = null;
        for (var t = 0; t < s.Triangles.Length / 3; t++)
        {
            if (NormalY(s, t) <= TriangleSoup.WallNormalY || HeightAt(s, t, x, z) is not { } y)
                continue;
            if (y <= referenceY + tolerance && (below is null || y > below))
                below = y;
            if (lowest is null || y < lowest)
                lowest = y;
        }
        return below ?? lowest;
    }

    private static bool BruteRaycast(TriangleSoup s, Vector3 origin, Vector3 dir, float max, out Vector3 hit)
    {
        hit = default;
        var d = dir * max;
        var best = float.MaxValue;
        for (var t = 0; t < s.Triangles.Length / 3; t++)
            if (RayTriangle(s, t, origin, d, out var f) && f > 1e-4f && f <= 1f && f < best)
                best = f;
        if (best == float.MaxValue)
            return false;
        hit = origin + d * best;
        return true;
    }

    // ---- the realms under test ----

    private static TriangleSoup Clumped()
    {
        // Detail concentrated in one corner of a wide floor: the shape that
        // made a uniform grid degenerate, and the shape a BVH must handle.
        var b = new SoupBuilder().AddBox(new Aabb(new Vector3(0, -10, 0), new Vector3(2000, 0, 2000)));
        var rng = new Random(4);
        for (var i = 0; i < 900; i++)
        {
            var o = new Vector3(500 + (float)rng.NextDouble() * 150, 0, 500 + (float)rng.NextDouble() * 150);
            b.AddBox(new Aabb(o, o + new Vector3(6, 12, 6)));
        }
        return b.Build();
    }

    private static TriangleSoup? Shipping(string map)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WoadRaiders.slnx")))
            dir = dir.Parent;
        var path = dir is null ? null : Path.Combine(dir.FullName, "WoadRaiders.Client", "maps", map);
        return path is not null && File.Exists(path) ? RealmDefinitionFile.Load(path).Soup : null;
    }

    public static IEnumerable<object[]> Realms()
    {
        yield return ["clumped", Clumped()];
        foreach (var map in new[] { "Crag.json", "Crypt.json" })
            if (Shipping(map) is { } soup)
                yield return [map, soup];
    }

    [Theory]
    [MemberData(nameof(Realms))]
    public void The_index_returns_exactly_what_testing_every_triangle_returns(string name, TriangleSoup soup)
    {
        var rng = new Random(9);
        var lo = soup.BoundsMin;
        var span = soup.BoundsMax - soup.BoundsMin;
        var triangles = soup.Triangles.Length / 3;

        Vector3 Anywhere() => new(
            lo.X + (float)rng.NextDouble() * span.X,
            lo.Y + (float)rng.NextDouble() * span.Y,
            lo.Z + (float)rng.NextDouble() * span.Z);

        // Near an actual VERTEX, jittered. A realm 7264 units across is mostly
        // air, so a uniformly random point is almost always nowhere near
        // anything and the ray from it tests the empty half of the tree. Anchored
        // sampling puts nearly every probe in contact with real geometry, which
        // is where the index and the brute force can actually disagree — so
        // FEWER rays here cover more than more rays did before.
        Vector3 NearGeometry()
        {
            var v = rng.Next(soup.Vertices.Length / 3) * 3;
            return new Vector3(soup.Vertices[v], soup.Vertices[v + 1], soup.Vertices[v + 2])
                 + new Vector3((float)rng.NextDouble() - 0.5f,
                               (float)rng.NextDouble() - 0.5f,
                               (float)rng.NextDouble() - 0.5f) * 120f;
        }

        // The brute force is O(triangles) and runs five times an iteration, so a
        // fixed sample count makes this test cost whatever the realm happens to
        // weigh — it was ~10 s when the Crypt held 2,280 triangles and four
        // MINUTES at 325,594, which is a test people stop running. Budget the
        // WORK instead of the samples, and the cost stays flat as realms grow.
        const long TriangleTestBudget = 120_000_000;
        var samples = (int)Math.Clamp(TriangleTestBudget / (5L * Math.Max(1, triangles)), 150, 1500);

        for (var i = 0; i < samples; i++)
        {
            var from = i % 2 == 0 ? NearGeometry() : Anywhere();
            var to = from + new Vector3(
                ((float)rng.NextDouble() - 0.5f) * span.X * 0.3f,
                ((float)rng.NextDouble() - 0.5f) * span.Y * 0.3f,
                ((float)rng.NextDouble() - 0.5f) * span.Z * 0.3f);

            Assert.Equal(BruteSegmentHits(soup, from, to, false), soup.SegmentHits(from, to));
            Assert.Equal(BruteSegmentHits(soup, from, to, true), soup.SegmentHits(from, to, blockersOnly: true));

            var p = i % 3 == 0 ? Anywhere() : NearGeometry();
            Assert.Equal(BruteGroundBelow(soup, p.X, p.Z, p.Y, SimConstants.StepHeight),
                         soup.GroundBelow(p.X, p.Z, p.Y, SimConstants.StepHeight));
            Assert.Equal(BruteGroundBelow(soup, p.X, p.Z, float.PositiveInfinity, 0f),
                         soup.TopSurfaceAt(p.X, p.Z));

            var dir = Vector3.Normalize(to - from is { } v && v.LengthSquared() > 1e-6f ? v : Vector3.UnitY);
            var reachable = BruteRaycast(soup, from, dir, 4000f, out var expected);
            Assert.Equal(reachable, soup.RaycastNearest(from, dir, 4000f, out var actual));
            if (reachable)
                Assert.True(Vector3.Distance(expected, actual) < 0.01f,
                    $"{name}: ray from {from} hit {actual}, brute force says {expected}");
        }
    }

    [Fact]
    public void The_tree_is_identical_on_every_peer_that_builds_it()
    {
        // Prediction only stays in step with the server because both sides
        // build the same structure from the same bytes.
        var soup = Clumped();
        var twin = new TriangleSoup((float[])soup.Vertices.Clone(), (int[])soup.Triangles.Clone());
        var rng = new Random(21);

        for (var i = 0; i < 400; i++)
        {
            var x = (float)rng.NextDouble() * 2000f;
            var z = (float)rng.NextDouble() * 2000f;
            Assert.Equal(soup.TopSurfaceAt(x, z), twin.TopSurfaceAt(x, z));
            var a = new Vector3(x, 20f, z);
            var b = a + new Vector3(120f, 0f, 90f);
            Assert.Equal(soup.SegmentHits(a, b), twin.SegmentHits(a, b));
        }
    }

    [Fact]
    public void Building_welds_corners_without_moving_a_single_triangle()
    {
        // Two boxes sharing a face: 24 triangles quoting 8 corners each, of
        // which only 12 positions are distinct once the shared face is folded.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, 0, 0), new Vector3(100, 100, 100)))
            .AddBox(new Aabb(new Vector3(100, 0, 0), new Vector3(200, 100, 100)))
            .Build();

        // Every triangle survives; the corners quoting them collapse from the
        // 48 the builder emits to the 12 positions two adjoining boxes have.
        Assert.Equal(24, soup.Triangles.Length / 3);
        Assert.Equal(12, soup.Vertices.Length / 3);

        // Welding renames corners; it must never move one, nor fold two of a
        // triangle's corners together into a degenerate sliver.
        var corners = new HashSet<(float, float, float)>();
        for (var v = 0; v < soup.Vertices.Length; v += 3)
            corners.Add((soup.Vertices[v], soup.Vertices[v + 1], soup.Vertices[v + 2]));
        foreach (var x in new[] { 0f, 100f, 200f })
        foreach (var y in new[] { 0f, 100f })
        foreach (var z in new[] { 0f, 100f })
            Assert.Contains((x, y, z), corners);

        for (var t = 0; t < soup.Triangles.Length; t += 3)
            Assert.Equal(3, new HashSet<int> { soup.Triangles[t], soup.Triangles[t + 1], soup.Triangles[t + 2] }.Count);
    }

    [Fact]
    public void A_soup_of_one_triangle_still_indexes_and_answers()
    {
        // Wound counter-clockwise seen from above, so its normal faces the sky
        // and it reads as ground; the same corners wound the other way are an
        // overhang, and TopSurfaceAt rightly refuses to stand anyone on them.
        float[] corners = [0, 0, 0, 100, 0, 0, 0, 0, 100];
        var ground = new TriangleSoup(corners, [0, 2, 1]);
        Assert.Equal(0f, ground.TopSurfaceAt(10f, 10f) ?? float.NaN, 3);
        Assert.Null(ground.TopSurfaceAt(900f, 900f));

        var overhang = new TriangleSoup(corners, [0, 1, 2]);
        Assert.Null(overhang.TopSurfaceAt(10f, 10f));
    }
}

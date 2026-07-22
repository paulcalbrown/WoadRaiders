// How much geometry a realm can actually hold, measured rather than reasoned.
// Answers what bounds BUDGET-001 (baked structural triangles) by timing the
// three things that scale with it: welding the soup, querying the BVH the
// simulation runs on, and baking the navmesh.
//
//   dotnet run tools/MeasureSoup.cs
//
// Geometry is STRUCTURED, not random — grid-snapped axis-aligned bays, the way
// real architecture is. That matters twice over: random points share no exact
// corners so they never weld, and they give the BVH no spatial coherence, so a
// random soup understates welding and overstates query cost at the same time.
#:project ../WoadRaiders.Core/WoadRaiders.Core.csproj
#:property PublishAot=false

using System.Diagnostics;
using System.Numerics;
using WoadRaiders.Core;

// The v2 footprint, held CONSTANT while triangles vary — navmesh cost tracks
// area, so letting the realm grow too would confound the two.
const float SpanX = 7200f, SpanZ = 2800f;

Console.WriteLine($"{"tris",9}{"welded",9}{"weld ms",9}{"LOS µs",9}{"ground µs",11}{"navmesh ms",12}{"nav KB",9}");

foreach (var target in new[] { 60_000, 250_000, 500_000, 1_000_000 })
{
    var soup = Structured(target, out var raw);
    var sw = Stopwatch.StartNew();
    var built = Rebuild(raw);
    var weldMs = sw.Elapsed.TotalMilliseconds;

    // Worst-case-ish queries: long diagonals across the whole realm, which is
    // what an aggro line-of-sight check across a hall looks like.
    var rng = new Random(5);
    var los = Time(2000, () =>
    {
        var a = new Vector3(rng.NextSingle() * SpanX, 30, rng.NextSingle() * SpanZ);
        var b = new Vector3(rng.NextSingle() * SpanX, 30, rng.NextSingle() * SpanZ);
        built.SegmentHits(a, b, blockersOnly: true);
    });
    var ground = Time(2000, () =>
        built.GroundBelow(rng.NextSingle() * SpanX, rng.NextSingle() * SpanZ, 200f, SimConstants.StepHeight));

    sw.Restart();
    var nav = NavMeshBuilder.Serialize(NavMeshBuilder.BuildMeshData(built));
    var navMs = sw.Elapsed.TotalMilliseconds;

    Console.WriteLine($"{built.Triangles.Length / 3,9}{built.Vertices.Length / 3,9}{weldMs,9:0}" +
                      $"{los,9:0.00}{ground,11:0.00}{navMs,12:0}{nav.Length / 1024.0,9:0}");
}

static double Time(int reps, Action body)
{
    for (var i = 0; i < 200; i++) body();       // warm the JIT and the caches
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < reps; i++) body();
    return sw.Elapsed.TotalMilliseconds * 1000.0 / reps;
}

static TriangleSoup Rebuild((float[] v, int[] t) raw) => new(raw.v, raw.t);

// A floor plus a lattice of wall bays on the half-module grid: coincident
// corners weld, and the BVH gets the spatial locality real masonry has.
static TriangleSoup Structured(int targetTriangles, out (float[], int[]) raw)
{
    var builder = new SoupBuilder();
    builder.AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(SpanX, 0, SpanZ)));

    // A FIXED set of wall lines, tessellated more finely as the budget grows —
    // so triangle count rises while the walkable floor between them does not.
    // Carpeting the floor with extra bays instead would confound the two: the
    // realm simply stops being walkable and the navmesh collapses to nothing,
    // which is a measurement of the generator, not of the pipeline.
    const int Lines = 14;                       // wall runs across the realm
    var perLine = Math.Max(1, (targetTriangles - 12) / 12 / Lines);
    for (var line = 0; line < Lines; line++)
    {
        var z = (line + 1) * SpanZ / (Lines + 1);
        var segment = SpanX / perLine;
        for (var s = 0; s < perLine; s++)
            builder.AddBox(new Aabb(new Vector3(s * segment, 0, z),
                                    new Vector3((s + 1) * segment, 80, z + 20)));
    }
    var soup = builder.Build();
    raw = (soup.Vertices, soup.Triangles);
    return soup;
}

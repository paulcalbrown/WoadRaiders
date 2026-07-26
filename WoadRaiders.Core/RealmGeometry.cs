using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace WoadRaiders.Core;

/// <summary>
/// The shipping <see cref="IRealmGeometry"/>, baked from a
/// <see cref="RealmDefinition"/>'s soup: movement is NAVMESH-ONLY —
/// <see cref="Move"/> walks the baked Detour surface (moveAlongSurface;
/// polygon boundaries act as walls and produce sliding) and never leaves it.
/// The only way off a surface is a baked off-mesh link: <see cref="FindLink"/>
/// names the crossing a blocked push would board and the sim executes it as a
/// LinkTraversal arc. Every link end is on the mesh by construction (the bake
/// scout verified it), so "on the mesh" is an invariant no sequence of moves
/// can break — the stuck-player states the old soup escape hatches could
/// reach (off-mesh floor rides, island landings) cannot be represented.
///
/// The soup keeps its non-movement jobs: sight lines, cursor rays, the
/// projectile ground query, and refining the mesh's voxel-rough height onto
/// the exact triangle underfoot (<see cref="SurfaceY"/> — the mesh picks the
/// layer, the soup gives the true ground). The bake-time physics that used to
/// live here as escape hatches survives in <see cref="ScoutMover"/>, where
/// the drop-link scouts still walk it.
///
/// A mover's radius is baked into the mesh, not checked at query time, so wide
/// movers (the boss, radius 30) get their own baked mesh: each Move picks the
/// narrowest agent class that fits the caller's radius, falling back to the
/// widest baked. <see cref="TryFindPath"/> walks the same polygon graph, so a
/// route is only ever planned where that mover's feet can follow.
///
/// Deterministic by construction: server and client prediction run this same
/// code over identical baked bytes, so predicted movement can never drift
/// from the authoritative sim. Not thread-safe: one instance per simulation.
/// </summary>
public sealed class RealmGeometry : IRealmGeometry
{
    // FindNearestPoly search box around a character position: generous on XZ
    // (erosion can pull the mesh a radius away from where feet stand), a step
    // or two on Y.
    private static readonly RcVec3f SnapExtents = new(24f, 48f, 24f);

    private const float CeilingReach = 4000f; // far enough to clear any realm's roof
    private const int MaxPathPolys = 256;

    private readonly record struct AgentClass(float Radius, DtNavMeshQuery Query, BakedLink[] Links);

    /// <summary>One baked off-mesh connection: A is the lip a scout stepped off,
    /// B where it landed; two-way links (boardings) cross in either direction.</summary>
    private readonly record struct BakedLink(Vector3 A, Vector3 B, bool Bidirectional);

    private readonly AgentClass[] _classes; // ascending by baked radius
    private readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();
    private readonly TriangleSoup _soup;
    private readonly long[] _visited = new long[64];
    private readonly long[] _pathPolys = new long[MaxPathPolys];
    private readonly DtStraightPath[] _straight = new DtStraightPath[MaxPathPolys];

    public Vector3 SpawnPoint { get; }

    /// <summary>A realm with the one standard agent class (CharacterRadius).</summary>
    public RealmGeometry(DtNavMesh navMesh, TriangleSoup soup, Vector3 spawnPoint)
        : this(soup, spawnPoint, (SimConstants.CharacterRadius, navMesh)) { }

    /// <summary>
    /// A realm with one baked mesh per mover width — e.g. the character mesh
    /// plus a boss mesh. Every mesh must be baked from the same soup.
    /// </summary>
    public RealmGeometry(TriangleSoup soup, Vector3 spawnPoint, params (float radius, DtNavMesh mesh)[] meshes)
    {
        if (meshes.Length == 0)
            throw new ArgumentException("a navmesh realm needs at least one baked agent class");
        _classes = meshes
            .OrderBy(m => m.radius)
            .Select(m => new AgentClass(m.radius, new DtNavMeshQuery(m.mesh), ExtractLinks(m.mesh)))
            .ToArray();
        _soup = soup;
        SpawnPoint = spawnPoint;
    }

    /// <summary>
    /// Resolve a move by walking the navmesh surface: the result slides along
    /// polygon boundaries and lands on the true triangle surface — climbs,
    /// stairs, and refusals all come from the bake for this mover's width.
    /// The mesh is never left: a push the surface refuses is a wall unless a
    /// baked link (<see cref="FindLink"/>) carries it, and that crossing is
    /// the sim's to execute, not this method's.
    /// </summary>
    public Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius)
    {
        if (delta.X == 0f && delta.Z == 0f)
            return position;
        var query = QueryFor(radius);
        if (!TrySnap(query, position, out var startRef, out var start))
        {
            // Unreachable when every transition is mesh-walk or link: a mover
            // with no polygon in snap reach escaped the invariant somewhere.
            OffMeshRefusals++;
            return position;
        }
        var target = new RcVec3f(position.X + delta.X, start.Y, position.Z + delta.Z);

        var status = query.MoveAlongSurface(startRef, start, target, _filter,
                                            out var result, _visited.AsSpan(), out var visitedCount, _visited.Length);
        if (!status.Succeeded())
            return position;
        var endRef = visitedCount > 0 ? _visited[visitedCount - 1] : startRef;
        return new Vector3(result.X, SurfaceY(result.X, result.Z, HeightOn(query, endRef, result)), result.Z);
    }

    /// <summary>
    /// How many times <see cref="Move"/> refused because the mover had no
    /// polygon within snap reach. Under navmesh-only movement this should
    /// stay 0 forever; a nonzero count is a broken invariant worth logging.
    /// </summary>
    public int OffMeshRefusals { get; private set; }

    /// <summary>
    /// The walkable route between two points, string-pulled to corner
    /// waypoints (the destination is the last). When the destination is
    /// unreachable the route ends at the nearest reachable ground — walk it
    /// and you are as close as this mover can get. False only when either
    /// end is nowhere near the mesh. This is the planner the straight-line
    /// steering in enemy pursuit and click-to-move cannot do without: sliding
    /// along walls escapes nothing concave.
    /// </summary>
    public bool TryFindPath(Vector3 from, Vector3 to, IList<Vector3> waypoints,
                            float radius = SimConstants.CharacterRadius)
    {
        waypoints.Clear();
        var query = QueryFor(radius);
        if (!TrySnap(query, from, out var startRef, out var start) ||
            !TrySnap(query, to, out var endRef, out var end))
            return false;

        var status = query.FindPath(startRef, endRef, start, end, _filter,
                                    _pathPolys.AsSpan(), out var polyCount, MaxPathPolys);
        if (!status.Succeeded() || polyCount == 0)
            return false;

        status = query.FindStraightPath(start, end, _pathPolys.AsSpan(), polyCount,
                                        _straight.AsSpan(), out var straightCount, MaxPathPolys, 0);
        if (!status.Succeeded() || straightCount == 0)
            return false;

        for (var i = 0; i < straightCount; i++)
        {
            var p = _straight[i].pos;
            waypoints.Add(new Vector3(p.X, SurfaceY(p.X, p.Z, p.Y), p.Z));
        }
        return true;
    }

    /// <summary>
    /// How aligned the push must be with a crossing's ground-plane direction
    /// to board it: cos 45°. A graze — climbing a stair whose open edge is a
    /// seeded rim, with a little sideways drift — must slide along the rim,
    /// not be plucked off it; only a push genuinely INTO the crossing boards.
    /// </summary>
    private const float MinBoardAlignment = 0.7f;

    /// <summary>
    /// The link a mover pushing over a rim would board: within LinkBoardRadius
    /// at the mover's own floor level, its crossing aligned with the push
    /// (<see cref="MinBoardAlignment"/>). Among candidates, one that CONTINUES
    /// at the mover's own level (a boarding hop across a broken join) beats
    /// one that plunges — a blocked climb must not read as a wish to fall —
    /// then the nearest lip wins. One-way links (drops) board only at their
    /// lip; two-way links (boardings) at either end. The returned code encodes
    /// the orientation — (index << 1) | reversed — so it alone names the
    /// crossing.
    /// </summary>
    public int FindLink(Vector3 position, Vector3 desiredDir, float radius = SimConstants.CharacterRadius)
    {
        var links = LinksFor(radius);
        var len = MathF.Sqrt(desiredDir.X * desiredDir.X + desiredDir.Z * desiredDir.Z);
        if (len < 1e-6f)
            return -1;
        var dx = desiredDir.X / len;
        var dz = desiredDir.Z / len;

        var best = -1;
        var bestSq = SimConstants.LinkBoardRadius * SimConstants.LinkBoardRadius;
        var bestRise = float.MaxValue;
        for (var i = 0; i < links.Length; i++)
        {
            Consider(links[i].A, links[i].B, i << 1);
            if (links[i].Bidirectional)
                Consider(links[i].B, links[i].A, (i << 1) | 1);
        }
        return best;

        void Consider(Vector3 from, Vector3 to, int code)
        {
            if (MathF.Abs(from.Y - position.Y) > SimConstants.LinkBoardHeadroom)
                return; // some other floor's rim
            var ex = from.X - position.X;
            var ez = from.Z - position.Z;
            var d2 = ex * ex + ez * ez;
            if (d2 >= SimConstants.LinkBoardRadius * SimConstants.LinkBoardRadius)
                return;
            var cx = to.X - from.X;
            var cz = to.Z - from.Z;
            var clen = MathF.Sqrt(cx * cx + cz * cz);
            if (clen > 1e-3f && (cx * dx + cz * dz) / clen < MinBoardAlignment)
                return; // grazing, not crossing — stay on this surface
            // Prefer staying at your own level; break ties by nearest lip.
            var rise = MathF.Abs(to.Y - position.Y);
            if (rise > bestRise + 0.5f || (rise > bestRise - 0.5f && d2 >= bestSq))
                return;
            best = code;
            bestSq = d2;
            bestRise = rise;
        }
    }

    public bool TryGetLink(int code, out Vector3 from, out Vector3 to, float radius = SimConstants.CharacterRadius)
    {
        from = default;
        to = default;
        var links = LinksFor(radius);
        var index = code >> 1;
        if (code < 0 || index >= links.Length)
            return false;
        var link = links[index];
        var reversed = (code & 1) != 0;
        if (reversed && !link.Bidirectional)
            return false;
        from = reversed ? link.B : link.A;
        to = reversed ? link.A : link.B;
        return true;
    }

    public bool HasLineOfSight(Vector3 from, Vector3 to) => !_soup.SegmentHits(from, to);

    /// <summary>
    /// The surface under a world point — the ground a projectile hugs and the
    /// camera clears — resolved against the asker's own height, so a bolt in
    /// the chasm follows the pit floor rather than the deck bridging overhead.
    /// Clamped into the soup's bounds so queries just past the realm's edge
    /// keep answering.
    /// </summary>
    public float GroundHeight(Vector3 near)
    {
        var cx = Math.Clamp(near.X, _soup.BoundsMin.X + 0.01f, _soup.BoundsMax.X - 0.01f);
        var cz = Math.Clamp(near.Z, _soup.BoundsMin.Z + 0.01f, _soup.BoundsMax.Z - 0.01f);
        return _soup.GroundBelow(cx, cz, near.Y, SimConstants.StepHeight) ?? 0f;
    }

    /// <summary>
    /// The roof over a point, straight up through the soup — structure or the
    /// underside of a floor above. Nothing overhead means open sky.
    /// </summary>
    public float CeilingHeight(Vector3 above) =>
        _soup.RaycastNearest(above, Vector3.UnitY, CeilingReach, out var hit)
            ? hit.Y
            : float.PositiveInfinity;

    /// <summary>
    /// Cursor picking: the nearest surface the ray strikes, snapped onto the
    /// walkable mesh so the order always lands somewhere feet can actually go.
    /// </summary>
    public bool RaycastGround(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit)
    {
        hit = default;
        if (direction.LengthSquared() < 1e-8f)
            return false;
        direction = Vector3.Normalize(direction);
        if (!_soup.RaycastNearest(origin, direction, maxDistance, out var surface))
            return false;
        var query = _classes[0].Query;
        var status = query.FindNearestPoly(ToRc(surface), SnapExtents, _filter,
                                           out var polyRef, out var pt, out _);
        if (!status.Succeeded() || polyRef == 0)
            return false;
        hit = new Vector3(pt.X, SurfaceY(pt.X, pt.Z, HeightOn(query, polyRef, pt)), pt.Z);
        return true;
    }

    /// <summary>The narrowest baked class this mover fits; the widest is the ceiling.</summary>
    private DtNavMeshQuery QueryFor(float radius)
    {
        foreach (var c in _classes)
            if (c.Radius >= radius - 0.01f)
                return c.Query;
        return _classes[^1].Query;
    }

    /// <summary>The same class dispatch as <see cref="QueryFor"/>, for the link table.</summary>
    private BakedLink[] LinksFor(float radius)
    {
        foreach (var c in _classes)
            if (c.Radius >= radius - 0.01f)
                return c.Links;
        return _classes[^1].Links;
    }

    /// <summary>
    /// The baked off-mesh connections, read out in tile order — identical on
    /// every peer holding the same bytes, so an index here is a wire-stable
    /// link identity. pos[0]/pos[1] are the scout's lip and landing from the
    /// bake (the landing already refined onto exact ground).
    /// </summary>
    private static BakedLink[] ExtractLinks(DtNavMesh mesh)
    {
        var links = new List<BakedLink>();
        for (var i = 0; i < mesh.GetMaxTiles(); i++)
        {
            if (mesh.GetTile(i)?.data?.offMeshCons is not { } cons)
                continue;
            foreach (var con in cons)
                links.Add(new BakedLink(
                    new Vector3(con.pos[0].X, con.pos[0].Y, con.pos[0].Z),
                    new Vector3(con.pos[1].X, con.pos[1].Y, con.pos[1].Z),
                    (con.flags & 1) != 0)); // bit 0 = DT_OFFMESH_CON_BIDIR
        }
        return links.ToArray();
    }

    private bool TrySnap(DtNavMeshQuery query, Vector3 position, out long polyRef, out RcVec3f onMesh)
    {
        var status = query.FindNearestPoly(ToRc(position), SnapExtents, _filter,
                                           out polyRef, out onMesh, out _);
        return status.Succeeded() && polyRef != 0;
    }

    /// <summary>moveAlongSurface leaves Y unprojected; the poly's detail height is closer.</summary>
    private static float HeightOn(DtNavMeshQuery query, long polyRef, RcVec3f pos) =>
        query.GetPolyHeight(polyRef, pos, out var h).Succeeded() ? h : pos.Y;

    /// <summary>
    /// The navmesh's height is voxel-rough (rasterization rounds surfaces up to
    /// the next cell). It still identifies WHICH surface the feet are on, so
    /// refine against the exact triangle nearest that height — the mesh picks
    /// the layer, the soup gives the true ground.
    /// </summary>
    private float SurfaceY(float x, float z, float navY) =>
        _soup.SurfaceNear(x, z, navY, 2f * NavMeshBuilder.CellHeight + 0.5f) ?? navY;

    private static RcVec3f ToRc(Vector3 v) => new(v.X, v.Y, v.Z);
}

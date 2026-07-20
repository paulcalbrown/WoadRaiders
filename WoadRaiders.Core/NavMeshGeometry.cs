using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace WoadRaiders.Core;

/// <summary>
/// The mesh-based counterpart of <see cref="DungeonGeometry"/>: movement is
/// clamped to a baked Detour navmesh (moveAlongSurface — polygon boundaries
/// act as walls and produce sliding), while sight lines, cursor rays, and the
/// projectile ground query test the exact <see cref="TriangleSoup"/> the mesh
/// was baked from. Ledge drops — legal at any height under the sim's rules but
/// disconnected edges on a navmesh — transfer to the surface directly below
/// the blocked target when it lies more than StepHeight down.
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
public sealed class NavMeshGeometry : IDungeonGeometry
{
    // FindNearestPoly search box around a character position: generous on XZ
    // (erosion can pull the mesh a radius away from where feet stand), a step
    // or two on Y.
    private static readonly RcVec3f SnapExtents = new(24f, 48f, 24f);

    // The drop probe looks for ground far below the blocked target — but only
    // directly below: a landing off to the side means a wall, not a ledge.
    private static readonly RcVec3f DropExtents = new(2f, 4096f, 2f);
    private const float DropSnapTolerance = 2f;
    private const float ClampEpsilon = 0.05f;
    private const float CeilingReach = 4000f; // far enough to clear any realm's roof
    private const int MaxPathPolys = 256;

    private readonly record struct AgentClass(float Radius, DtNavMeshQuery Query);

    private readonly AgentClass[] _classes; // ascending by baked radius
    private readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();
    private readonly TriangleSoup _soup;
    private readonly long[] _visited = new long[64];
    private readonly long[] _pathPolys = new long[MaxPathPolys];
    private readonly DtStraightPath[] _straight = new DtStraightPath[MaxPathPolys];

    public Vector3 SpawnPoint { get; }

    /// <summary>A realm with the one standard agent class (CharacterRadius).</summary>
    public NavMeshGeometry(DtNavMesh navMesh, TriangleSoup soup, Vector3 spawnPoint)
        : this(soup, spawnPoint, (SimConstants.CharacterRadius, navMesh)) { }

    /// <summary>
    /// A realm with one baked mesh per mover width — e.g. the character mesh
    /// plus a boss mesh. Every mesh must be baked from the same soup.
    /// </summary>
    public NavMeshGeometry(TriangleSoup soup, Vector3 spawnPoint, params (float radius, DtNavMesh mesh)[] meshes)
    {
        if (meshes.Length == 0)
            throw new ArgumentException("a navmesh realm needs at least one baked agent class");
        _classes = meshes
            .OrderBy(m => m.radius)
            .Select(m => new AgentClass(m.radius, new DtNavMeshQuery(m.mesh)))
            .ToArray();
        _soup = soup;
        SpawnPoint = spawnPoint;
    }

    /// <summary>
    /// Resolve a move by walking the navmesh surface: the result slides along
    /// polygon boundaries and lands on the true triangle surface — climbs,
    /// stairs, and refusals all come from the bake for this mover's width.
    /// </summary>
    public Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius)
    {
        if (delta.X == 0f && delta.Z == 0f)
            return position;
        var query = QueryFor(radius);
        if (!TrySnap(query, position, out var startRef, out var start))
            return position; // off the mesh entirely — nowhere legal to go
        var target = new RcVec3f(position.X + delta.X, start.Y, position.Z + delta.Z);

        var status = query.MoveAlongSurface(startRef, start, target, _filter,
                                            out var result, _visited.AsSpan(), out var visitedCount, _visited.Length);
        if (!status.Succeeded())
            return position;
        var endRef = visitedCount > 0 ? _visited[visitedCount - 1] : startRef;
        var landed = new Vector3(result.X, SurfaceY(result.X, result.Z, HeightOn(query, endRef, result)), result.Z);

        // Clamped short of the target? The mesh only describes what is safe to
        // WALK — the sim also permits what the bake cannot express: ledge
        // drops of any size, and riding terrain steeper than the climbable
        // grade downhill (or inching it up to StepHeight per step).
        var dx = target.X - landed.X;
        var dz = target.Z - landed.Z;
        if (dx * dx + dz * dz > ClampEpsilon * ClampEpsilon)
        {
            if (TryStepUp(query, position, target.X, target.Z, radius, out var boarded))
                return boarded;
            if (TryLedgeDrop(query, position.Y, target.X, target.Z, out var dropped))
                return dropped;
            if (TryFloorRide(position, target.X, target.Z, radius, out var rode))
                return rode;
            // Off-mesh (mid-descent), the snap can yank the walker back onto a
            // rim it already left — never move farther than the tick asked.
            var jumpX = landed.X - position.X;
            var jumpZ = landed.Z - position.Z;
            if (jumpX * jumpX + jumpZ * jumpZ > (delta.X * delta.X + delta.Z * delta.Z) * 4f + 1f)
                return position;
        }
        return landed;
    }

    /// <summary>
    /// Boarding by footprint reach: the sim's WalkSurface counts any surface
    /// within StepHeight above whose edge the body cylinder overlaps — that is
    /// how a walker steps from a slope onto a bridge deck whose slab starts a
    /// few units ahead. The mesh's edge additionally sits an eroded radius in
    /// from the physical edge, so the reach allows for both. Only genuine
    /// rises board (10..StepHeight — smaller steps are span-connected in the
    /// bake), only forward of travel, and never through a standing solid.
    /// </summary>
    private bool TryStepUp(DtNavMeshQuery query, Vector3 from, float targetX, float targetZ, float radius, out Vector3 landing)
    {
        landing = default;
        var dx = targetX - from.X;
        var dz = targetZ - from.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6f)
            return false;
        // Search centred a body radius past the target so the surface ahead —
        // not the slope behind — is the nearest candidate.
        var reach = radius * 2f + 12f;
        var centre = new RcVec3f(targetX + dx / len * radius, from.Y + SimConstants.StepHeight, targetZ + dz / len * radius);
        var status = query.FindNearestPoly(centre, new RcVec3f(reach, 8f, reach), _filter,
                                           out var polyRef, out var pt, out _);
        if (!status.Succeeded() || polyRef == 0)
            return false;
        if (pt.Y > from.Y + SimConstants.StepHeight + 0.01f || pt.Y < from.Y + 10f)
            return false; // not a legal boarding rise
        if ((pt.X - from.X) * dx + (pt.Z - from.Z) * dz <= 0f)
            return false; // behind or beside the walk — not where this step goes
        var px = pt.X - targetX;
        var pz = pt.Z - targetZ;
        if (px * px + pz * pz > reach * reach)
            return false; // beyond any footprint's touch
        var clearance = from.Y + SimConstants.StepHeight + 0.5f;
        if (_soup.SegmentHits(new Vector3(from.X, clearance, from.Z), new Vector3(pt.X, clearance, pt.Z), structureOnly: true))
            return false; // a wall stands between — reaching over it is not boarding
        landing = new Vector3(pt.X, SurfaceY(pt.X, pt.Z, pt.Y), pt.Z);
        return true;
    }

    /// <summary>
    /// The bake's slope cutoff cannot express the sim's asymmetry: a floor of
    /// any steepness is descendable, and any grade rising no more than
    /// StepHeight per step is inchable. When the mesh clamps, ride the raw
    /// floor instead — provided no STRUCTURE stands across the way at step
    /// height (the ground itself is never a wall; its rise is gated above).
    /// </summary>
    private bool TryFloorRide(Vector3 from, float targetX, float targetZ, float radius, out Vector3 landing)
    {
        landing = default;
        if (_soup.FloorHeightAt(targetX, targetZ) is not { } ground ||
            ground > from.Y + SimConstants.StepHeight)
            return false;
        var dx = targetX - from.X;
        var dz = targetZ - from.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6f)
            return false;
        // Three clearance rails at step height — down the middle and along
        // each shoulder — probing a body radius past the target, so the
        // centre can neither hug a wall nor squeeze a cylinder through a
        // gap narrower than its body.
        var clearance = from.Y + SimConstants.StepHeight + 0.5f;
        var ox = dx / len * radius;
        var oz = dz / len * radius;
        for (var rail = -1; rail <= 1; rail++)
        {
            var sx = -oz * rail;
            var sz = ox * rail;
            if (_soup.SegmentHits(new Vector3(from.X + sx, clearance, from.Z + sz),
                                  new Vector3(targetX + ox + sx, clearance, targetZ + oz + sz),
                                  structureOnly: true))
                return false;
        }
        landing = new Vector3(targetX, ground, targetZ);
        return true;
    }

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

    public bool HasLineOfSight(Vector3 from, Vector3 to) => !_soup.SegmentHits(from, to);

    /// <summary>
    /// The floor under a world XZ point — the ground a projectile hugs and the
    /// camera clears. Clamped into the soup's bounds so queries just past the
    /// realm's edge keep answering.
    /// </summary>
    public float GroundHeight(float x, float z)
    {
        var cx = Math.Clamp(x, _soup.BoundsMin.X + 0.01f, _soup.BoundsMax.X - 0.01f);
        var cz = Math.Clamp(z, _soup.BoundsMin.Z + 0.01f, _soup.BoundsMax.Z - 0.01f);
        return _soup.FloorHeightAt(cx, cz) ?? 0f;
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

    private bool TrySnap(DtNavMeshQuery query, Vector3 position, out long polyRef, out RcVec3f onMesh)
    {
        var status = query.FindNearestPoly(ToRc(position), SnapExtents, _filter,
                                           out polyRef, out onMesh, out _);
        return status.Succeeded() && polyRef != 0;
    }

    /// <summary>moveAlongSurface leaves Y unprojected; the poly's detail height is closer.</summary>
    private static float HeightOn(DtNavMeshQuery query, long polyRef, RcVec3f pos) =>
        query.GetPolyHeight(polyRef, pos, out var h).Succeeded() ? h : pos.Y;

    private bool TryLedgeDrop(DtNavMeshQuery query, float fromY, float x, float z, out Vector3 landing)
    {
        landing = default;
        var status = query.FindNearestPoly(new RcVec3f(x, fromY, z), DropExtents, _filter,
                                           out var polyRef, out var pt, out _);
        if (!status.Succeeded() || polyRef == 0)
            return false;
        var dx = pt.X - x;
        var dz = pt.Z - z;
        if (dx * dx + dz * dz > DropSnapTolerance * DropSnapTolerance)
            return false; // nothing directly below — the clamp was a wall
        if (pt.Y > fromY - SimConstants.StepHeight)
            return false; // level or rising ground is never a drop
        landing = new Vector3(pt.X, SurfaceY(pt.X, pt.Z, HeightOn(query, polyRef, pt)), pt.Z);
        return true;
    }

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

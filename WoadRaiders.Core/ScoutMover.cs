using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace WoadRaiders.Core;

/// <summary>
/// The sim's PHYSICAL movement rules — mesh walking plus the transitions no
/// polygon can express: boarding a surface a footprint reaches, dropping off
/// a ledge of any height, and riding ground steeper than the walkable grade.
///
/// BAKE-TIME ONLY. These used to be <see cref="RealmGeometry.Move"/>'s escape
/// hatches; the runtime is navmesh-only now — a mover leaves a surface solely
/// through a baked off-mesh link — and this class is where the old physics
/// survives so <see cref="NavMeshBuilder"/>'s drop-link scouts can still
/// discover, by walking, every crossing those rules allow. The scout finds
/// them at bake time; players get exactly the links it earned, and no others.
/// Nothing at runtime may call this: a hatch behind the runtime Move is
/// precisely the stuck-player bug this split exists to kill.
///
/// The rules are frozen as they shipped (character-radius clearances, the
/// same snap boxes) so a rebake reproduces the links the sim used to permit.
/// Not thread-safe: one instance per bake.
/// </summary>
public sealed class ScoutMover
{
    private static readonly RcVec3f SnapExtents = new(24f, 48f, 24f);

    // The drop probe looks for ground far below the blocked target — but only
    // directly below: a landing off to the side means a wall, not a ledge.
    private static readonly RcVec3f DropExtents = new(2f, 4096f, 2f);
    private const float DropSnapTolerance = 2f;
    private const float ClampEpsilon = 0.05f;

    private readonly DtNavMeshQuery _query;
    private readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();
    private readonly TriangleSoup _soup;
    private readonly long[] _visited = new long[64];

    public ScoutMover(DtNavMesh navMesh, TriangleSoup soup)
    {
        _query = new DtNavMeshQuery(navMesh);
        _soup = soup;
    }

    /// <summary>
    /// Resolve a move under the full physical rules: walk the mesh surface,
    /// and when the mesh clamps, board what the footprint reaches, drop what
    /// lies below, or ride the raw floor — the transitions the runtime only
    /// permits through the links this mover discovers.
    /// </summary>
    public Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius) =>
        Move(position, delta, out _, radius);

    /// <summary>
    /// As <see cref="Move(Vector3,Vector3,float)"/>, and reports whether a
    /// physical HATCH (step-up, ledge drop, floor ride) produced the result
    /// rather than plain mesh walking — the difference between CROSSING
    /// something and merely walking somewhere, which is what separates a
    /// drop-link scout's crossing from its stroll (a scout that walks up a
    /// staircase has not "boarded" it, however high it ends up).
    /// </summary>
    public Vector3 Move(Vector3 position, Vector3 delta, out bool hatched, float radius = SimConstants.CharacterRadius)
    {
        hatched = false;
        if (delta.X == 0f && delta.Z == 0f)
            return position;
        if (!TrySnap(position, out var startRef, out var start))
            return position; // off the mesh entirely — nowhere legal to go
        var target = new RcVec3f(position.X + delta.X, start.Y, position.Z + delta.Z);

        var status = _query.MoveAlongSurface(startRef, start, target, _filter,
                                             out var result, _visited.AsSpan(), out var visitedCount, _visited.Length);
        if (!status.Succeeded())
            return position;
        var endRef = visitedCount > 0 ? _visited[visitedCount - 1] : startRef;
        var landed = new Vector3(result.X, SurfaceY(result.X, result.Z, HeightOn(endRef, result)), result.Z);

        var dx = target.X - landed.X;
        var dz = target.Z - landed.Z;
        if (dx * dx + dz * dz > ClampEpsilon * ClampEpsilon)
        {
            hatched = true;
            if (TryStepUp(position, target.X, target.Z, radius, out var boarded))
                return boarded;
            if (TryLedgeDrop(position.Y, target.X, target.Z, out var dropped))
                return dropped;
            if (TryFloorRide(position, target.X, target.Z, radius, out var rode))
                return rode;
            hatched = false;
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
    /// Boarding by footprint reach: any surface within StepHeight above whose
    /// edge the body cylinder overlaps — how a walker steps from a slope onto
    /// a bridge deck that starts a few units ahead. Only genuine rises board
    /// (10..StepHeight), only forward of travel, never through a standing solid.
    /// </summary>
    private bool TryStepUp(Vector3 from, float targetX, float targetZ, float radius, out Vector3 landing)
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
        var status = _query.FindNearestPoly(centre, new RcVec3f(reach, 8f, reach), _filter,
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
        if (_soup.SegmentHits(new Vector3(from.X, clearance, from.Z), new Vector3(pt.X, clearance, pt.Z), blockersOnly: true))
            return false; // a wall stands between — reaching over it is not boarding
        landing = new Vector3(pt.X, SurfaceY(pt.X, pt.Z, pt.Y), pt.Z);
        return true;
    }

    /// <summary>
    /// A floor of any steepness is descendable, and any grade rising no more
    /// than StepHeight per step is inchable. When the mesh clamps, ride the
    /// raw floor — provided no STRUCTURE stands across the way at step height.
    /// </summary>
    private bool TryFloorRide(Vector3 from, float targetX, float targetZ, float radius, out Vector3 landing)
    {
        landing = default;
        if (_soup.GroundBelow(targetX, targetZ, from.Y, SimConstants.StepHeight) is not { } ground ||
            ground > from.Y + SimConstants.StepHeight)
            return false;
        var dx = targetX - from.X;
        var dz = targetZ - from.Z;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6f)
            return false;
        // Three clearance rails at step height — down the middle and along
        // each shoulder — probing a body radius past the target.
        var clearance = from.Y + SimConstants.StepHeight + 0.5f;
        var ox = dx / len * radius;
        var oz = dz / len * radius;
        for (var rail = -1; rail <= 1; rail++)
        {
            var sx = -oz * rail;
            var sz = ox * rail;
            if (_soup.SegmentHits(new Vector3(from.X + sx, clearance, from.Z + sz),
                                  new Vector3(targetX + ox + sx, clearance, targetZ + oz + sz),
                                  blockersOnly: true))
                return false;
        }
        landing = new Vector3(targetX, ground, targetZ);
        return true;
    }

    private bool TryLedgeDrop(float fromY, float x, float z, out Vector3 landing)
    {
        landing = default;
        var status = _query.FindNearestPoly(new RcVec3f(x, fromY, z), DropExtents, _filter,
                                            out var polyRef, out var pt, out _);
        if (!status.Succeeded() || polyRef == 0)
            return false;
        var dx = pt.X - x;
        var dz = pt.Z - z;
        if (dx * dx + dz * dz > DropSnapTolerance * DropSnapTolerance)
            return false; // nothing directly below — the clamp was a wall
        if (pt.Y > fromY - SimConstants.StepHeight)
            return false; // level or rising ground is never a drop
        landing = new Vector3(pt.X, SurfaceY(pt.X, pt.Z, HeightOn(polyRef, pt)), pt.Z);
        return true;
    }

    private bool TrySnap(Vector3 position, out long polyRef, out RcVec3f onMesh)
    {
        var status = _query.FindNearestPoly(new RcVec3f(position.X, position.Y, position.Z), SnapExtents, _filter,
                                            out polyRef, out onMesh, out _);
        return status.Succeeded() && polyRef != 0;
    }

    private float HeightOn(long polyRef, RcVec3f pos) =>
        _query.GetPolyHeight(polyRef, pos, out var h).Succeeded() ? h : pos.Y;

    private float SurfaceY(float x, float z, float navY) =>
        _soup.SurfaceNear(x, z, navY, 2f * NavMeshBuilder.CellHeight + 0.5f) ?? navY;
}

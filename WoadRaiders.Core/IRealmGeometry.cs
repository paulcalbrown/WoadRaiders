using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// The seam between the simulation and whatever describes the realm's shape.
/// The shipping provider is <see cref="RealmGeometry"/> — a baked Detour
/// navmesh over the realm's triangle soup. A world with no geometry at all
/// keeps the open-arena defaults below. Coordinates are world-space, Y-up.
/// </summary>
public interface IRealmGeometry
{
    /// <summary>Where new/respawning players are placed.</summary>
    Vector3 SpawnPoint { get; }

    /// <summary>
    /// Resolve an attempted move: apply <paramref name="delta"/> to
    /// <paramref name="position"/>, colliding with the realm (sliding along
    /// walls) and keeping the result on a walkable surface.
    /// <paramref name="radius"/> is the mover's collision cylinder radius
    /// (bosses are wider than regular characters).
    /// </summary>
    Vector3 Move(Vector3 position, Vector3 delta, float radius = SimConstants.CharacterRadius);

    /// <summary>
    /// True when nothing solid blocks the straight line between two points
    /// (both world-space, typically at eye height). Attacks and aggro use this
    /// so enemies do not spot or strike players through walls. Open-arena
    /// providers can keep the default: always visible.
    /// </summary>
    bool HasLineOfSight(Vector3 from, Vector3 to) => true;

    /// <summary>
    /// The height of the surface underfoot at <paramref name="near"/> — what a
    /// player projectile hugs so a shot follows a slope instead of burying
    /// itself in the first rise, and what the camera keeps clear of.
    ///
    /// Takes a whole point, not an XZ pair, because a built realm stacks
    /// walkable levels: under the Crypt's chasm bridge the deck and the pit
    /// floor share an XZ, and only the asker's own height says which of them
    /// is THEIR ground. Flat providers keep the default: 0.
    /// </summary>
    float GroundHeight(Vector3 near) => 0f;

    /// <summary>
    /// The underside of whatever roofs a world-space point — the headroom the
    /// chase camera has before it would climb out of the chamber the raider is
    /// standing in. Sight lines alone cannot answer this: a built realm roofs
    /// each space at its own height, so where a low room abuts a
    /// tall corridor the gap between their roofs is a slot a camera can see
    /// clean through. Open sky — and providers with no geometry — have none.
    /// </summary>
    float CeilingHeight(Vector3 above) => float.PositiveInfinity;

    /// <summary>
    /// Where a ray first meets the walkable world — the client's cursor
    /// picking. False when the ray escapes without landing (open-arena
    /// providers keep the default; the caller falls back to a flat plane).
    /// </summary>
    bool RaycastGround(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit)
    {
        hit = default;
        return false;
    }

    /// <summary>
    /// Plan a walkable route between two points into <paramref name="waypoints"/>
    /// (the destination, or the closest reachable ground, is the last entry).
    /// Enemy pursuit follows these instead of sliding blindly along walls. The
    /// default is the straight line — correct for open arenas and the flat
    /// fallback; providers with real routing (the navmesh) override it.
    /// </summary>
    bool TryFindPath(Vector3 from, Vector3 to, IList<Vector3> waypoints, float radius = SimConstants.CharacterRadius)
    {
        waypoints.Clear();
        waypoints.Add(to);
        return true;
    }
}

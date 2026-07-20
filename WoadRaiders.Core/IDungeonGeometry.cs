using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// The seam between the simulation and whatever describes the realm's shape.
/// The shipping provider is <see cref="NavMeshGeometry"/> — a baked Detour
/// navmesh over the realm's triangle soup. A world with no geometry at all
/// keeps the open-arena defaults below. Coordinates are world-space, Y-up.
/// </summary>
public interface IDungeonGeometry
{
    /// <summary>Where new/respawning players are placed.</summary>
    Vector3 SpawnPoint { get; }

    /// <summary>
    /// Resolve an attempted move: apply <paramref name="delta"/> to
    /// <paramref name="position"/>, colliding with the dungeon (sliding along
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
    /// The height of the base ground plane (the terrain — no solids) at a world
    /// XZ point. Player projectiles hug this so a shot follows a slope instead
    /// of burying itself in the first rise. Flat providers keep the default: 0.
    /// </summary>
    float GroundHeight(float x, float z) => 0f;

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

using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// The seam between the simulation and whatever describes the dungeon's shape.
/// Today the provider is a flat tile grid (<see cref="DungeonMap"/>); later it can
/// be mesh-based geometry (glTF module kit + collision primitives / BepuPhysics)
/// without touching any gameplay code. Coordinates are world-space, Y-up.
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
}

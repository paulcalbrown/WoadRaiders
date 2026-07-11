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
    /// </summary>
    Vector3 Move(Vector3 position, Vector3 delta);

    /// <summary>A random walkable position (enemy/loot placement).</summary>
    Vector3 RandomSpawnPosition(Random rng);
}

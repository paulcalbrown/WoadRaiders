using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// A generated dungeon as a grid of floor/wall tiles, centred on the world origin.
/// Shared verbatim between server and clients so movement, collision, and
/// prediction all agree. Pure data + queries — no engine, no networking.
/// </summary>
public sealed class DungeonMap
{
    public int Width { get; }
    public int Height { get; }
    public float TileSize { get; }
    public Vector2 SpawnPoint { get; }

    private readonly bool[] _floor;                 // row-major [y * Width + x]; true = walkable
    private readonly List<Vector2> _floorWorld = new();

    public DungeonMap(int width, int height, float tileSize, bool[] floor, Vector2 spawnPoint)
    {
        Width = width;
        Height = height;
        TileSize = tileSize;
        _floor = floor;
        SpawnPoint = spawnPoint;

        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            if (floor[y * width + x])
                _floorWorld.Add(TileCenter(x, y));
    }

    public int FloorCount => _floorWorld.Count;

    public bool IsFloorTile(int tx, int ty) =>
        tx >= 0 && ty >= 0 && tx < Width && ty < Height && _floor[ty * Width + tx];

    /// <summary>True if a world point sits on a floor tile.</summary>
    public bool IsWalkable(Vector2 world)
    {
        var (tx, ty) = WorldToTile(world);
        return IsFloorTile(tx, ty);
    }

    public Vector2 TileCenter(int tx, int ty) => new(
        (tx - Width / 2f + 0.5f) * TileSize,
        (ty - Height / 2f + 0.5f) * TileSize);

    public (int TileX, int TileY) WorldToTile(Vector2 world) => (
        (int)MathF.Floor(world.X / TileSize + Width / 2f),
        (int)MathF.Floor(world.Y / TileSize + Height / 2f));

    /// <summary>A random floor tile's world centre (for enemy/loot placement).</summary>
    public Vector2 RandomFloorPosition(Random rng) => _floorWorld[rng.Next(_floorWorld.Count)];
}

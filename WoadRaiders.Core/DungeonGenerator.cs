using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Procedural room-and-corridor generation (Gauntlet-style), emitting the same
/// <see cref="DungeonGeometry"/> a hand-crafted Godot scene exports to — so
/// authored and generated maps are interchangeable everywhere downstream.
/// Internally it still carves a grid, then converts wall tiles into merged
/// world-space boxes. Deterministic for a given seed.
/// </summary>
public static class DungeonGenerator
{
    public const int Width = 44;
    public const int Height = 26;
    public const float TileSize = 40f;
    public const float WallHeight = 70f;

    private const int MinRoomSize = 5;
    private const int MaxRoomSize = 10;
    private const int MaxRooms = 10;
    private const int RoomAttempts = 80;
    private const int CorridorWidth = 2;   // wide halls so hordes and the party can move through
    private const int ExtraEnemySpawns = 16;

    private readonly record struct Room(int X, int Y, int W, int H)
    {
        public (int X, int Y) Center => (X + W / 2, Y + H / 2);
    }

    public static DungeonGeometry Generate(int seed)
    {
        var rng = new Random(seed);
        var floor = new bool[Width * Height];
        var rooms = new List<Room>();

        // Place non-overlapping rooms (a 1-tile buffer keeps a wall between them).
        for (var attempt = 0; attempt < RoomAttempts && rooms.Count < MaxRooms; attempt++)
        {
            var w = rng.Next(MinRoomSize, MaxRoomSize + 1);
            var h = rng.Next(MinRoomSize, MaxRoomSize + 1);
            var x = rng.Next(1, Width - w - 1);
            var y = rng.Next(1, Height - h - 1);
            var room = new Room(x, y, w, h);

            if (rooms.Any(other => Overlaps(other, room)))
                continue;

            CarveRoom(floor, room);
            rooms.Add(room);
        }

        if (rooms.Count == 0)
        {
            var fallback = new Room(Width / 2 - 3, Height / 2 - 3, 6, 6);
            CarveRoom(floor, fallback);
            rooms.Add(fallback);
        }

        // Connect each room to the nearest earlier one (guarantees connectivity)...
        for (var i = 1; i < rooms.Count; i++)
        {
            var nearest = 0;
            var best = int.MaxValue;
            for (var j = 0; j < i; j++)
            {
                var d = CenterDistanceSq(rooms[i], rooms[j]);
                if (d < best)
                {
                    best = d;
                    nearest = j;
                }
            }
            CarveCorridor(floor, rooms[i].Center, rooms[nearest].Center, rng);
        }

        // ...plus a few extra links so the level loops instead of being a strict tree.
        var extraLinks = rooms.Count > 2 ? rng.Next(2, 4) : 0;
        for (var k = 0; k < extraLinks; k++)
        {
            var a = rng.Next(rooms.Count);
            var b = rng.Next(rooms.Count);
            if (a != b)
                CarveCorridor(floor, rooms[a].Center, rooms[b].Center, rng);
        }

        // Spawn in the first room; enemies in the other rooms + random floor tiles.
        var (cx, cy) = rooms[0].Center;
        var spawn = TileCenter(cx, cy);

        var enemySpawns = new List<Vector3>();
        for (var i = 1; i < rooms.Count; i++)
        {
            var (ex, ey) = rooms[i].Center;
            enemySpawns.Add(TileCenter(ex, ey));
        }

        var floorTiles = new List<(int X, int Y)>();
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
            if (floor[y * Width + x])
                floorTiles.Add((x, y));
        for (var i = 0; i < ExtraEnemySpawns && floorTiles.Count > 0; i++)
        {
            var (tx, ty) = floorTiles[rng.Next(floorTiles.Count)];
            enemySpawns.Add(TileCenter(tx, ty));
        }

        return new DungeonGeometry(spawn, BuildWallBoxes(floor), enemySpawns);
    }

    /// <summary>Convert wall tiles into world-space boxes, greedily merging runs per row.</summary>
    private static List<Aabb> BuildWallBoxes(bool[] floor)
    {
        var boxes = new List<Aabb>();
        for (var y = 0; y < Height; y++)
        {
            var x = 0;
            while (x < Width)
            {
                if (floor[y * Width + x]) { x++; continue; }

                var runStart = x;
                while (x < Width && !floor[y * Width + x])
                    x++;

                boxes.Add(new Aabb(
                    new Vector3(TileEdge(runStart, Width), 0f, TileEdge(y, Height)),
                    new Vector3(TileEdge(x, Width), WallHeight, TileEdge(y + 1, Height))));
            }
        }
        return boxes;
    }

    private static float TileEdge(int t, int gridExtent) => (t - gridExtent / 2f) * TileSize;

    private static Vector3 TileCenter(int tx, int ty) => new(
        (tx - Width / 2f + 0.5f) * TileSize,
        0f,
        (ty - Height / 2f + 0.5f) * TileSize);

    private static bool Overlaps(Room a, Room b) =>
        a.X - 1 < b.X + b.W && a.X + a.W + 1 > b.X &&
        a.Y - 1 < b.Y + b.H && a.Y + a.H + 1 > b.Y;

    private static int CenterDistanceSq(Room a, Room b)
    {
        var (ax, ay) = a.Center;
        var (bx, by) = b.Center;
        return (ax - bx) * (ax - bx) + (ay - by) * (ay - by);
    }

    private static void CarveRoom(bool[] floor, Room r)
    {
        for (var y = r.Y; y < r.Y + r.H; y++)
        for (var x = r.X; x < r.X + r.W; x++)
            SetFloor(floor, x, y);
    }

    private static void CarveCorridor(bool[] floor, (int X, int Y) a, (int X, int Y) b, Random rng)
    {
        if (rng.Next(2) == 0)
        {
            CarveH(floor, a.X, b.X, a.Y);
            CarveV(floor, a.Y, b.Y, b.X);
        }
        else
        {
            CarveV(floor, a.Y, b.Y, a.X);
            CarveH(floor, a.X, b.X, b.Y);
        }
    }

    private static void CarveH(bool[] floor, int x0, int x1, int y)
    {
        for (var x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
        for (var w = 0; w < CorridorWidth; w++)
            SetFloor(floor, x, y + w);
    }

    private static void CarveV(bool[] floor, int y0, int y1, int x)
    {
        for (var y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
        for (var w = 0; w < CorridorWidth; w++)
            SetFloor(floor, x + w, y);
    }

    // Only carve interior tiles so the outer border stays solid rock.
    private static void SetFloor(bool[] floor, int x, int y)
    {
        if (x >= 1 && y >= 1 && x < Width - 1 && y < Height - 1)
            floor[y * Width + x] = true;
    }
}

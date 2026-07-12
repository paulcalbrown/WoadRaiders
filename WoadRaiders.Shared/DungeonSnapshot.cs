using WoadRaiders.Core;

namespace WoadRaiders.Shared;

/// <summary>
/// Projects a <see cref="DungeonGeometry"/> into the <see cref="DungeonGeometryPacket"/>
/// sent to a client on join. The dungeon is immutable once loaded, so the server
/// builds this once and reuses it for every connection. Lives in Shared — the
/// wire-protocol seam — alongside <see cref="WorldSnapshot"/>, so both world→packet
/// projections sit together and are testable in isolation.
/// </summary>
public static class DungeonSnapshot
{
    public static DungeonGeometryPacket From(DungeonGeometry dungeon)
    {
        var boxes = new float[dungeon.Solids.Count * 6];
        for (var i = 0; i < dungeon.Solids.Count; i++)
        {
            var s = dungeon.Solids[i];
            boxes[i * 6 + 0] = s.Min.X;
            boxes[i * 6 + 1] = s.Min.Y;
            boxes[i * 6 + 2] = s.Min.Z;
            boxes[i * 6 + 3] = s.Max.X;
            boxes[i * 6 + 4] = s.Max.Y;
            boxes[i * 6 + 5] = s.Max.Z;
        }

        return new DungeonGeometryPacket
        {
            SpawnX = dungeon.SpawnPoint.X,
            SpawnY = dungeon.SpawnPoint.Y,
            SpawnZ = dungeon.SpawnPoint.Z,
            ScenePath = dungeon.ScenePath ?? "",
            Boxes = boxes,
        };
    }
}

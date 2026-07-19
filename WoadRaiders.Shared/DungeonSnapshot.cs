using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Shared;

/// <summary>
/// Projects a <see cref="DungeonGeometry"/> into the <see cref="DungeonGeometryPacket"/>
/// sent to a client on join, and rebuilds one from it on the receiving side. The
/// dungeon is immutable once loaded, so the server builds the packet once and
/// reuses it for every connection. Lives in Shared — the wire-protocol seam —
/// alongside <see cref="WorldSnapshot"/>, so both world↔packet projections sit
/// together and are testable in isolation (including as a round trip).
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

        var packet = new DungeonGeometryPacket
        {
            SpawnX = dungeon.SpawnPoint.X,
            SpawnY = dungeon.SpawnPoint.Y,
            SpawnZ = dungeon.SpawnPoint.Z,
            ScenePath = dungeon.ScenePath ?? "",
            Boxes = boxes,
        };

        if (dungeon.Terrain is { } t)
        {
            packet.HasTerrain = true;
            packet.TerrainOriginX = t.OriginX;
            packet.TerrainOriginZ = t.OriginZ;
            packet.TerrainCellSize = t.CellSize;
            packet.TerrainWidth = t.Width;
            packet.TerrainDepth = t.Depth;
            packet.TerrainHeights = t.Heights.ToArray();
        }

        return packet;
    }

    /// <summary>
    /// Rebuilds a <see cref="DungeonGeometry"/> from the packet — the client-side
    /// inverse of <see cref="From"/>, so prediction collides against exactly what
    /// the server does (the terrain floats cross the wire bit-exact). Enemy
    /// spawns are server-only and never on the wire, so the rebuilt geometry
    /// carries none.
    /// </summary>
    public static DungeonGeometry ToGeometry(DungeonGeometryPacket packet)
    {
        var solids = new List<Aabb>(packet.Boxes.Length / 6);
        for (var i = 0; i + 5 < packet.Boxes.Length; i += 6)
            solids.Add(new Aabb(
                new Vector3(packet.Boxes[i], packet.Boxes[i + 1], packet.Boxes[i + 2]),
                new Vector3(packet.Boxes[i + 3], packet.Boxes[i + 4], packet.Boxes[i + 5])));

        var terrain = packet.HasTerrain
            ? new HeightField(packet.TerrainOriginX, packet.TerrainOriginZ, packet.TerrainCellSize,
                              packet.TerrainWidth, packet.TerrainDepth, packet.TerrainHeights)
            : null;

        return new DungeonGeometry(
            new Vector3(packet.SpawnX, packet.SpawnY, packet.SpawnZ),
            solids, Array.Empty<EnemySpawnPoint>(), terrain)
        {
            ScenePath = string.IsNullOrEmpty(packet.ScenePath) ? null : packet.ScenePath,
        };
    }

    /// <summary>
    /// A fingerprint over everything the packet carries, so a client can tell
    /// whether a re-sent geometry is the same map or a different one (a reconnect
    /// can land on a restarted server serving a new arena). Process-local only —
    /// System.HashCode is seeded per process — which is exactly the lifetime of
    /// the comparison; never persist or send it.
    /// </summary>
    public static int Fingerprint(DungeonGeometryPacket packet)
    {
        var hash = new HashCode();
        hash.Add(packet.SpawnX);
        hash.Add(packet.SpawnY);
        hash.Add(packet.SpawnZ);
        hash.Add(packet.ScenePath);
        foreach (var f in packet.Boxes)
            hash.Add(f);
        hash.Add(packet.HasTerrain);
        if (packet.HasTerrain)
        {
            hash.Add(packet.TerrainOriginX);
            hash.Add(packet.TerrainOriginZ);
            hash.Add(packet.TerrainCellSize);
            hash.Add(packet.TerrainWidth);
            hash.Add(packet.TerrainDepth);
            foreach (var h in packet.TerrainHeights)
                hash.Add(h);
        }
        return hash.ToHashCode();
    }
}

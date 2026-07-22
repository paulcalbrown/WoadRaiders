using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Shared;

/// <summary>
/// Projects the authoritative <see cref="GameWorld"/> into a broadcastable
/// <see cref="WorldSnapshotPacket"/>. This lives in Shared — the wire-protocol
/// seam — rather than in the server loop, so it is unit-testable in isolation
/// (build a world, map it, round-trip it), and it is where interest management
/// hooks in.
///
/// INTEREST MANAGEMENT. <see cref="Around"/> sends one raider only what is
/// within sight of them; <see cref="From"/> sends the whole world. The filter is
/// what decouples a realm's population from the wire: snapshots ride Unreliable
/// and are split across MTU-sized chunks, and losing ANY chunk discards the
/// whole snapshot — so an unfiltered world does not fail gradually as it grows,
/// it degrades by dropping a rising fraction of ALL its updates. Nine chunks at
/// 3% packet loss lands three snapshots in four. Filtering holds the chunk count
/// flat no matter how many enemies the realm holds.
///
/// PLAYERS ARE NEVER FILTERED. There are at most eight, they are the thing a
/// co-op raid is about, and a warband that cannot see its own scattered members
/// cannot regroup. Everything else — enemies, loot, bolts — is bounded by the
/// realm's own sight radius (Core.DungeonCatalog).
/// </summary>
public static class WorldSnapshot
{
    /// <summary>
    /// One raider's view: the whole warband, and everything else within
    /// <paramref name="sightRadius"/> of <paramref name="eye"/>.
    /// </summary>
    public static WorldSnapshotPacket Around(GameWorld world, Vector3 eye, float sightRadius)
    {
        var snapshot = From(world);
        var reach = sightRadius * sightRadius;
        bool Near(float x, float y, float z) =>
            Vector3.DistanceSquared(eye, new Vector3(x, y, z)) <= reach;

        snapshot.Enemies = snapshot.Enemies.Where(e => Near(e.X, e.Y, e.Z)).ToArray();
        snapshot.GroundItems = snapshot.GroundItems.Where(g => Near(g.X, g.Y, g.Z)).ToArray();
        snapshot.Projectiles = snapshot.Projectiles.Where(p => Near(p.X, p.Y, p.Z)).ToArray();
        return snapshot;
    }

    /// <summary>The entire world, unfiltered — what the tests and tools read, and
    /// what <see cref="Around"/> narrows.</summary>
    public static WorldSnapshotPacket From(GameWorld world) => new()
    {
        ServerTick = world.Tick,
        Players = world.Players.Values.Select(p => new PlayerSnapshot
        {
            Id = p.Id,
            Name = p.Name,
            X = p.Position.X,
            Y = p.Position.Y,
            Z = p.Position.Z,
            Health = p.Health,
            LastProcessedInput = p.LastProcessedInput,
            Attacking = p.IsAttacking,
            AttackAnim = p.AttackAnimRemaining,
            AttackCooldown = p.AttackCooldown,
            Class = (byte)p.Class,
        }).ToArray(),
        Enemies = world.Enemies.Values.Select(e => new EnemySnapshot
        {
            Id = e.Id,
            X = e.Position.X,
            Y = e.Position.Y,
            Z = e.Position.Z,
            Health = e.Health,
            Attacking = e.IsAttacking,
            Type = (byte)e.Type,
        }).ToArray(),
        GroundItems = world.GroundItems.Values.Select(g => new GroundItemSnapshot
        {
            Id = g.Id,
            X = g.Position.X,
            Y = g.Position.Y,
            Z = g.Position.Z,
            Rarity = (byte)(g.Item?.Rarity ?? ItemRarity.Common), // equipment rarity; gold/potions ignore it
            Kind = (byte)g.Kind,
            Type = (byte)(g.Item?.Type ?? default(ItemType)),     // which weapon mesh; gold/potions ignore it
        }).ToArray(),
        Projectiles = world.Projectiles.Values.Select(p => new ProjectileSnapshot
        {
            Id = p.Id,
            X = p.Position.X,
            Y = p.Position.Y,
            Z = p.Position.Z,
            Kind = (byte)p.Kind,
        }).ToArray(),
        PortalOpen = world.Portal is not null,
        PortalX = world.Portal?.X ?? 0f,
        PortalY = world.Portal?.Y ?? 0f,
        PortalZ = world.Portal?.Z ?? 0f,
    };
}

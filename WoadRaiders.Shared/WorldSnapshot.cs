using System.Linq;
using WoadRaiders.Core;

namespace WoadRaiders.Shared;

/// <summary>
/// Projects the authoritative <see cref="GameWorld"/> into a broadcastable
/// <see cref="WorldSnapshotPacket"/>. This lives in Shared — the wire-protocol
/// seam — rather than in the server loop, so it is unit-testable in isolation
/// (build a world, map it, round-trip it) and it is the single place a future
/// interest-management or delta-compression pass would hook in.
/// </summary>
public static class WorldSnapshot
{
    /// <summary>Snapshot the entire world for every client (no per-client filtering yet).</summary>
    public static WorldSnapshotPacket From(GameWorld world) => new()
    {
        ServerTick = world.Tick,
        Players = world.Players.Values.Select(p => new PlayerSnapshot
        {
            Id = p.Id,
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

using System;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// Slab-built movement fixtures for tests that need real geometry: a flat
/// stone floor, optionally with structure walls standing on it. Baked with
/// both agent classes (character and boss width) exactly as the server bakes
/// realms, so wide-radius behaviour is testable too.
/// </summary>
public static class TestRealms
{
    /// <summary>A flat floor slab centred on the origin, top face at y = 0.</summary>
    public static TriangleSoup Flat(float halfExtent = 600f) => new SoupBuilder()
        .AddBox(new Aabb(new Vector3(-halfExtent, -20, -halfExtent), new Vector3(halfExtent, 0, halfExtent)))
        .Build();

    /// <summary>Movement geometry over a soup, baked for both agent widths.</summary>
    public static RealmGeometry Geo(TriangleSoup soup, Vector3 spawn = default) =>
        new(soup, spawn,
            (SimConstants.CharacterRadius, NavMeshBuilder.Build(soup)),
            (EnemyArchetypes.Of(EnemyType.Boss).Radius,
             NavMeshBuilder.Build(soup, EnemyArchetypes.Of(EnemyType.Boss).Radius)));

    /// <summary>An open flat realm — the slab world's stand-in for "no walls anywhere".</summary>
    public static RealmGeometry Open() => Geo(Flat());

    /// <summary>A flat floor with structure walls standing on it.</summary>
    public static RealmGeometry WithWalls(params Aabb[] walls)
    {
        var builder = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-600, -20, -600), new Vector3(600, 0, 600)));
        foreach (var wall in walls)
            builder.AddBox(wall);
        return Geo(builder.Build());
    }
}

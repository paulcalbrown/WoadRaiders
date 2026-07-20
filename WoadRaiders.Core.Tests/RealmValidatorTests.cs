using System;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The realm playability bar, judged on the navmesh the server will move on:
/// the boss and every camp must be reachable by a complete route, and nowhere
/// the spawn can reach may be stranded — falls are detours, never graves.
/// </summary>
public class RealmValidatorTests
{
    private static RealmDefinition Realm(TriangleSoup? soup, Vector3 spawn, Vector3? boss,
                                         params EnemySpawnPoint[] camps) =>
        new(spawn, soup, camps) { BossSpawn = boss };

    [Fact]
    public void A_sound_realm_passes()
    {
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(800, 0, 800)), floor: true)
            .Build();
        var realm = Realm(soup, new Vector3(100, 0, 400), new Vector3(700, 0, 400),
            new EnemySpawnPoint(new Vector3(400, 0, 200), EnemyType.Minion),
            new EnemySpawnPoint(new Vector3(400, 0, 600), EnemyType.Mage));

        Assert.Empty(RealmValidator.Validate(realm));
    }

    [Fact]
    public void An_unreachable_camp_is_reported()
    {
        // A camp on a 100-high pedestal no route can climb.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(800, 0, 800)), floor: true)
            .AddBox(new Aabb(new Vector3(500, 0, 600), new Vector3(700, 100, 800)), floor: true)
            .Build();
        var realm = Realm(soup, new Vector3(100, 0, 400), new Vector3(700, 0, 100),
            new EnemySpawnPoint(new Vector3(600, 100, 700), EnemyType.Rogue));

        var issues = RealmValidator.Validate(realm);
        Assert.Contains(issues, i => i.Contains("Rogue camp") && i.Contains("not reachable"));
    }

    [Fact]
    public void A_reachable_pit_with_no_way_back_to_the_boss_is_stranding()
    {
        // A lower yard you can drop into off the main floor's edge — but its
        // 100-unit walls have no stair back up, and the boss stands above.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, 80, 0), new Vector3(400, 100, 800)), floor: true)
            .AddBox(new Aabb(new Vector3(400, -20, 0), new Vector3(800, 0, 800)), floor: true)
            .Build();
        var realm = Realm(soup, new Vector3(100, 100, 400), new Vector3(300, 100, 400));

        var issues = RealmValidator.Validate(realm);
        Assert.Contains(issues, i => i.Contains("stranded"));
    }

    [Fact]
    public void A_drop_shortcut_that_still_reaches_the_boss_is_fine()
    {
        // The same two-level build, but a ramp climbs back to the upper floor:
        // the drop is a detour, not a grave.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, 80, 0), new Vector3(400, 100, 800)), floor: true)
            .AddBox(new Aabb(new Vector3(400, -20, 0), new Vector3(800, 0, 800)), floor: true)
            .AddQuad(new Vector3(500, 0, 0), new Vector3(500, 0, 200),
                     new Vector3(400, 100, 200), new Vector3(400, 100, 0), floor: true)
            .Build();
        var realm = Realm(soup, new Vector3(100, 100, 500), new Vector3(300, 100, 500));

        Assert.Empty(RealmValidator.Validate(realm));
    }

    [Fact]
    public void A_soupless_or_bossless_map_fails_outright()
    {
        var flat = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(400, 0, 400)), floor: true)
            .Build();

        Assert.Contains(RealmValidator.Validate(Realm(null, Vector3.Zero, Vector3.Zero)),
            i => i.Contains("no geometry"));
        Assert.Contains(RealmValidator.Validate(Realm(flat, new Vector3(50, 0, 50), boss: null)),
            i => i.Contains("no boss"));
    }
}

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
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(800, 0, 800)))
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
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(800, 0, 800)))
            .AddBox(new Aabb(new Vector3(500, 0, 600), new Vector3(700, 100, 800)))
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
            .AddBox(new Aabb(new Vector3(0, 80, 0), new Vector3(400, 100, 800)))
            .AddBox(new Aabb(new Vector3(400, -20, 0), new Vector3(800, 0, 800)))
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
            .AddBox(new Aabb(new Vector3(0, 80, 0), new Vector3(400, 100, 800)))
            .AddBox(new Aabb(new Vector3(400, -20, 0), new Vector3(800, 0, 800)))
            .AddQuad(new Vector3(500, 0, 0), new Vector3(500, 0, 200),
                     new Vector3(400, 100, 200), new Vector3(400, 100, 0))
            .Build();
        var realm = Realm(soup, new Vector3(100, 100, 500), new Vector3(300, 100, 500));

        Assert.Empty(RealmValidator.Validate(realm));
    }

    [Fact]
    public void An_oubliette_too_small_for_any_sample_grid_is_still_caught()
    {
        // A 140-unit sunken well in an upland plate: one drop link in off its
        // rim, 100-unit walls, no way out. The old stranding sweep sampled a
        // 200-unit grid and judged where routed walkers LANDED — a trap this
        // size could sit between its points. The flood fill proves stranding
        // over every polygon the spawn can reach, so size cannot hide it.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, 80, 0), new Vector3(800, 100, 30)))      // upland ring…
            .AddBox(new Aabb(new Vector3(0, 80, 170), new Vector3(800, 100, 800)))
            .AddBox(new Aabb(new Vector3(0, 80, 30), new Vector3(30, 100, 170)))
            .AddBox(new Aabb(new Vector3(170, 80, 30), new Vector3(800, 100, 170)))
            .AddBox(new Aabb(new Vector3(30, -20, 30), new Vector3(170, 0, 170)))    // …the well floor
            .Build();
        var realm = Realm(soup, new Vector3(400, 100, 400), new Vector3(600, 100, 600));

        var issues = RealmValidator.Validate(realm);
        var stranding = Assert.Single(issues, i => i.Contains("stranded"));
        // The clump centre names the well, so a designer can walk to the trap.
        var centre = System.Text.RegularExpressions.Regex.Match(stranding, @"^\((-?\d+),(-?\d+)\)");
        Assert.True(centre.Success, $"no centre in: {stranding}");
        Assert.InRange(int.Parse(centre.Groups[1].Value), 80, 120);
        Assert.InRange(int.Parse(centre.Groups[2].Value), 80, 120);
    }

    [Fact]
    public void A_declared_flight_that_walks_passes()
    {
        // A gentle ramp joins two floors; the declared flight walks both ways
        // on plain mesh, so the realm is sound.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(300, 0, 400)))
            .AddQuad(new Vector3(300, 0, 0), new Vector3(500, 50, 0),
                     new Vector3(500, 50, 400), new Vector3(300, 0, 400))
            .AddBox(new Aabb(new Vector3(500, 30, 0), new Vector3(800, 50, 400)))
            .Build();
        var realm = new RealmDefinition(new Vector3(100, 0, 200), soup, Array.Empty<EnemySpawnPoint>())
        {
            BossSpawn = new Vector3(700, 50, 200),
            Stairs = new[] { new StairRun(new Vector3(250, 0, 200), new Vector3(550, 50, 200)) },
        };

        Assert.Empty(RealmValidator.Validate(realm));
    }

    [Fact]
    public void A_flight_that_stalls_or_rides_a_link_fails()
    {
        // The Cliff shape: a declared "flight" up a sheer 300 face. Climbing
        // stalls at the wall; descending boards the rim's baked drop link.
        // Both are exactly what the walk check exists to catch — no
        // reachability proof can see either.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(200, 0, 400)))
            .AddBox(new Aabb(new Vector3(200, -20, 0), new Vector3(400, 300, 400)))
            .Build();
        var realm = new RealmDefinition(new Vector3(300, 300, 200), soup, Array.Empty<EnemySpawnPoint>())
        {
            BossSpawn = new Vector3(350, 300, 200), // beside the spawn: the realm is otherwise sound
            Stairs = new[] { new StairRun(new Vector3(100, 0, 200), new Vector3(280, 300, 200)) },
        };

        var issues = RealmValidator.Validate(realm);
        Assert.Contains(issues, i => i.Contains("flight 0 climbing") && i.Contains("stalls"));
        Assert.Contains(issues, i => i.Contains("flight 0 descending") && i.Contains("rides a baked link"));
    }

    [Fact]
    public void A_soupless_or_bossless_map_fails_outright()
    {
        var flat = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(400, 0, 400)))
            .Build();

        Assert.Contains(RealmValidator.Validate(Realm(null, Vector3.Zero, Vector3.Zero)),
            i => i.Contains("no geometry"));
        Assert.Contains(RealmValidator.Validate(Realm(flat, new Vector3(50, 0, 50), boss: null)),
            i => i.Contains("no boss"));
    }
}

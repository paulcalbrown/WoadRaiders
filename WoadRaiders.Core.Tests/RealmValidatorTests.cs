using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The shared realm playability checks (used by the generator and by the
/// hand-made .tscn import pipeline): comfortable reachability, sealed
/// borders, and no stranding pits — on small synthetic heightfields.
/// </summary>
public class RealmValidatorTests
{
    private const int N = 12;      // 12x12 samples over a 440x440 world
    private const float Cell = 40f;

    /// <summary>A field built per sample from (i, j) — the tests' terrain kit.</summary>
    private static HeightField Field(Func<int, int, float> h)
    {
        var heights = new float[N * N];
        for (var j = 0; j < N; j++)
            for (var i = 0; i < N; i++)
                heights[j * N + i] = h(i, j);
        return new HeightField(0, 0, Cell, N, N, heights);
    }

    /// <summary>A sealed bowl: flat 0 inside, an 800-high rim on the outer three rings.</summary>
    private static float Bowl(int i, int j) =>
        i < 3 || j < 3 || i >= N - 3 || j >= N - 3 ? 800f : 0f;

    private static DungeonGeometry Realm(HeightField field, Vector3? boss = null,
                                         IReadOnlyList<EnemySpawnPoint>? spawns = null,
                                         params Aabb[] solids) =>
        new(new Vector3(200, 0, 200), solids, spawns ?? Array.Empty<EnemySpawnPoint>(), field)
        {
            BossSpawn = boss,
        };

    [Fact]
    public void A_sound_realm_passes_clean()
    {
        var realm = Realm(Field(Bowl), boss: new Vector3(280, 0, 280),
            spawns: new[] { new EnemySpawnPoint(new Vector3(160, 0, 280), EnemyType.Minion) });
        Assert.Empty(RealmValidator.Validate(realm));
    }

    [Fact]
    public void A_map_without_terrain_is_not_a_realm()
    {
        var flat = new DungeonGeometry(Vector3.Zero, Array.Empty<Aabb>(), Array.Empty<EnemySpawnPoint>());
        var issues = RealmValidator.Validate(flat);
        Assert.Contains(issues, i => i.Contains("no terrain"));
    }

    [Fact]
    public void Unsealed_borders_are_reported_as_a_leak()
    {
        var realm = Realm(Field((_, _) => 0f), boss: new Vector3(280, 0, 280));
        var issues = RealmValidator.Validate(realm);
        Assert.Contains(issues, i => i.Contains("leak"));
    }

    [Fact]
    public void A_pit_you_can_fall_into_but_never_leave_is_a_stranding()
    {
        // The bowl, with a 2x2 shaft dropped 400 in its floor: anyone can fall
        // in (drops are free), nobody climbs a 10-grade wall back out.
        var realm = Realm(Field((i, j) => i is 7 or 8 && j is 7 or 8 ? -400f : Bowl(i, j)),
            boss: new Vector3(160, 0, 160));
        var issues = RealmValidator.Validate(realm);
        Assert.Contains(issues, i => i.Contains("stranding"));
    }

    [Fact]
    public void An_unreachable_camp_is_reported()
    {
        // A marker on a lone 400-high pillar: no grade reaches its top.
        var realm = Realm(Field((i, j) => i == 8 && j == 8 ? 400f : Bowl(i, j)),
            boss: new Vector3(160, 0, 160),
            spawns: new[] { new EnemySpawnPoint(new Vector3(320, 400, 320), EnemyType.Mage) });
        var issues = RealmValidator.Validate(realm);
        Assert.Contains(issues, i => i.Contains("Mage camp") && i.Contains("not comfortably reachable"));
    }

    [Fact]
    public void A_bridge_deck_carries_the_route_across_a_trench()
    {
        // A 400-deep trench row splits the bowl; the boss waits across it.
        var field = Field((i, j) => j == 6 && i >= 3 && i < N - 3 ? -400f : Bowl(i, j));
        var boss = new Vector3(200, 0, 320);

        // Without a bridge, the boss is out of comfortable reach.
        var severed = RealmValidator.Validate(Realm(field, boss));
        Assert.Contains(severed, i => i.Contains("boss"));

        // A deck spanning the trench at grade restores the route. (The trench
        // floor itself remains a stranding pit — reported separately, exactly
        // as the real gorge would be without its scree ramp.)
        var deck = new Aabb(new Vector3(180, -14, 200), new Vector3(220, 0, 280));
        var bridged = RealmValidator.Validate(Realm(field, boss, solids: deck));
        Assert.DoesNotContain(bridged, i => i.Contains("boss"));
        Assert.Contains(bridged, i => i.Contains("stranding"));
    }
}

using System;
using System.IO;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The realm's movement rules, judged on slab-built geometry: baking soups
/// into Detour meshes and moving on them through <see cref="RealmGeometry"/>.
/// Climb-or-refuse, ledge drops, deck boarding, routing around walls, and the
/// properties multiplayer depends on: deterministic bakes and a serialized
/// form both peers share bit-exact.
/// </summary>
public class RealmGeometryTests
{
    private const float TickStep = SimConstants.PlayerMoveSpeed * SimConstants.TickDelta;

    // A flat stone floor over [0,400]², top face at y=0.
    private static TriangleSoup Flat() => new SoupBuilder()
        .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(400, 0, 400)))
        .Build();

    // The west half flat at 0, then a ramp rising 50 over the east 200 —
    // grade 0.25, gentle walking.
    private static TriangleSoup Ramp() => new SoupBuilder()
        .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(200, 0, 400)))
        .AddQuad(new Vector3(200, 0, 0), new Vector3(400, 50, 0),
                 new Vector3(400, 50, 400), new Vector3(200, 0, 400))
        .Build();

    // A low floor and a high plateau meeting at a sheer face at x=200 —
    // unclimbable up, one leap down.
    private static TriangleSoup Cliff() => new SoupBuilder()
        .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(200, 0, 400)))
        .AddBox(new Aabb(new Vector3(200, -20, 0), new Vector3(400, 300, 400)))
        .Build();

    // A pit floor at y=-400 with a bridge deck slabbed across it at y=0 —
    // two walkable levels sharing an XZ, the shape the Crypt's chasm has.
    private static TriangleSoup DeckOverPit() => new SoupBuilder()
        .AddBox(new Aabb(new Vector3(0, -420, 0), new Vector3(400, -400, 400)))
        .AddBox(new Aabb(new Vector3(0, -20, 150), new Vector3(400, 0, 250)))
        .Build();

    // A floor running east into a face pitched at the given angle. Both
    // shipping realms are built from axis-aligned boxes — every triangle in
    // them is level or sheer — so tilted ground is exercised only here.
    private static TriangleSoup Pitched(float degrees)
    {
        var run = 300f;
        var rise = run * MathF.Tan(degrees * MathF.PI / 180f);
        return new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-300, -20, 0), new Vector3(0, 0, 400)))
            .AddQuad(new Vector3(0, 0, 0), new Vector3(run, rise, 0),
                     new Vector3(run, rise, 400), new Vector3(0, 0, 400))
            .Build();
    }

    [Theory]
    [InlineData(60f)]
    [InlineData(70f)]
    [InlineData(80f)]
    [InlineData(89f)]
    public void Ground_steeper_than_the_navmesh_allows_is_still_ground_to_descend(float degrees)
    {
        // The threshold that separates ground from wall is near-vertical
        // (~87°), NOT the navmesh's 67.8° walkable cutoff. The sim lets a
        // mover ride DOWN a floor of any grade, so a 70° or 80° face has to
        // keep answering as a surface even though the bake refuses to make
        // walkable polygons out of it. Unify the two and long descents that
        // the rules allow quietly become walls.
        var soup = Pitched(degrees);
        var expected = 150f * MathF.Tan(degrees * MathF.PI / 180f);

        var surface = soup.TopSurfaceAt(150f, 200f);
        if (degrees < 87f)
        {
            Assert.NotNull(surface);
            Assert.Equal(expected, surface!.Value, 1);
            Assert.False(soup.SegmentHits(new Vector3(140, expected + 30, 200),
                                          new Vector3(160, expected + 30, 200), blockersOnly: true),
                $"a {degrees}° face is ground to ride, not a wall to stop at");
        }
        else
        {
            // Effectively sheer: a wall, and it blocks a body's clearance probe.
            Assert.True(soup.SegmentHits(new Vector3(-40, 40, 200), new Vector3(40, 40, 200), blockersOnly: true),
                $"a {degrees}° face should stop a body");
        }
    }

    [Fact]
    public void A_walker_gets_down_a_face_the_bake_calls_unwalkable()
    {
        // 75° is far past the navmesh's 67.8° limit, so the mesh ends at the
        // lip and the sim's own fallbacks have to carry the walker down. This
        // pins the OUTCOME — a steep face is a way down, not a trap — rather
        // than which fallback does it; unify the wall threshold with the
        // bake's slope limit and the walker stalls at the lip forever.
        var rise = 300f * MathF.Tan(75f * MathF.PI / 180f);
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-400, -20, 0), new Vector3(0, 0, 400)))        // floor below
            .AddQuad(new Vector3(0, 0, 0), new Vector3(300, rise, 0),
                     new Vector3(300, rise, 400), new Vector3(0, 0, 400))               // the face
            .AddBox(new Aabb(new Vector3(300, rise - 20, 0), new Vector3(700, rise, 400))) // plateau above
            .Build();
        var geo = new RealmGeometry(NavMeshBuilder.Build(soup), soup, new Vector3(500, rise, 200));

        var pos = new Vector3(500, rise, 200);
        for (var i = 0; i < 200; i++)
            pos = geo.Move(pos, new Vector3(-TickStep, 0, 0));

        Assert.True(pos.X < 300f, $"the walker never left the plateau — the face read as a wall (X={pos.X})");
        Assert.True(pos.Y < rise - 50f, $"the face should have taken height off the walker, got Y={pos.Y} of {rise:0}");

        // Where it STOPS is a separate limit, and a real one: Move snaps to the
        // navmesh before doing anything, so a walker who lands beyond the snap
        // extents of any polygon has no legal move left and stands there. A
        // face longer than that reach is therefore a one-way shelf, not a
        // slide to the bottom. Both shipping realms avoid it by construction —
        // their drops land back on mesh — but a designer tilting a long slab
        // would meet it, so it is pinned here rather than left as folklore.
        Assert.True(pos.X > 0f,
            $"unexpected: the walker rode the whole face to the floor (X={pos.X}). If Move learned to " +
            "carry a mover across off-mesh ground, this test should become the stronger assertion.");
    }

    private static float RampHeight(float x) => x <= 200f ? 0f : (x - 200f) * 0.25f;

    private static RealmGeometry Geo(TriangleSoup soup) =>
        new(NavMeshBuilder.Build(soup), soup, new Vector3(50, 0, 200));

    [Fact]
    public void The_slope_limit_derives_from_the_step_height_rule()
    {
        // StepHeight per player tick-step ≈ grade 2.45 ≈ 67.8°.
        Assert.InRange(NavMeshBuilder.MaxWalkableSlopeDegrees, 67f, 69f);
    }

    [Fact]
    public void The_ground_under_a_stacked_realm_is_the_asker_s_own_level()
    {
        var geo = Geo(DeckOverPit());

        // Standing in the pit, under the deck: the ground is the pit floor,
        // not the deck bridging 400 units overhead.
        Assert.Equal(-400f, geo.GroundHeight(new Vector3(200, -400, 200)), 1);

        // Standing ON the deck at the same XZ: the ground is the deck.
        Assert.Equal(0f, geo.GroundHeight(new Vector3(200, 0, 200)), 1);

        // Off to the side there is only one level, whoever asks.
        Assert.Equal(-400f, geo.GroundHeight(new Vector3(200, -400, 50)), 1);
        Assert.Equal(-400f, geo.GroundHeight(new Vector3(200, 0, 50)), 1);
    }

    [Fact]
    public void A_player_bolt_flying_under_a_deck_hugs_the_pit_floor()
    {
        // The regression this guards: a Y-less ground query answered every
        // shot in the chasm with the DECK's height, so a terrain-following
        // bolt read the deck as a cliff face risen in its path and died on
        // the tick it spawned — while enemy bolts, which aim in full 3D,
        // fired back freely.
        var world = new GameWorld(new Random(1)) { Geometry = Geo(DeckOverPit()) };
        var player = world.AddPlayer(1, "raider", CharacterClass.Ranger);
        player.Position = new Vector3(120, -400, 200); // in the pit, under the deck

        world.SetInput(1, new PlayerInput { Attack = true, AimX = 1f, AimZ = 0f });
        world.Step();
        var boltId = Assert.Single(world.Projectiles.Keys);

        world.SetInput(1, new PlayerInput { AimX = 1f, AimZ = 0f }); // release, so one bolt is in flight
        for (var i = 0; i < 20; i++)
            world.Step();

        Assert.True(world.Projectiles.TryGetValue(boltId, out var bolt),
            "the bolt died in flight — the deck overhead was read as a cliff face");
        Assert.True(bolt.Position.X > 140, $"the bolt should have flown east, but sits at {bolt.Position.X:0}");
        Assert.InRange(bolt.Position.Y, -400f, -400f + SimConstants.EyeHeight + 1f);
    }

    [Fact]
    public void A_flat_realm_bakes_and_rides_at_zero()
    {
        var geo = Geo(Flat());
        var pos = new Vector3(50, 0, 200);
        for (var i = 0; i < 40; i++)
            pos = geo.Move(pos, new Vector3(TickStep, 0, 0));

        Assert.True(pos.X > 330f, $"open floor should not impede walking, got X={pos.X}");
        Assert.InRange(pos.Y, -1f, 1f);
    }

    [Fact]
    public void Walking_a_gentle_ramp_rides_its_surface()
    {
        var geo = Geo(Ramp());
        var pos = new Vector3(50, 0, 200);
        for (var i = 0; i < 45; i++)
        {
            pos = geo.Move(pos, new Vector3(TickStep, 0, 0));
            Assert.True(MathF.Abs(pos.Y - RampHeight(pos.X)) < 3.5f,
                $"tick {i}: Y={pos.Y} is off the ramp surface at X={pos.X} (expected {RampHeight(pos.X)})");
        }

        // The mesh ends before the eastern rim (agent-radius erosion): near the
        // top, still on the slope's real surface.
        Assert.True(pos.X > 378f, $"the ramp should carry the walker east, got X={pos.X}");
    }

    [Fact]
    public void A_sheer_face_refuses_the_climb()
    {
        var geo = Geo(Cliff());
        var pos = new Vector3(50, 0, 200);
        for (var i = 0; i < 60; i++)
            pos = geo.Move(pos, new Vector3(TickStep, 0, 0));

        // Floor-riding carries feet to the very toe of the face; the 300-unit
        // rise is a wall. Nobody climbs, nobody teleports past.
        Assert.InRange(pos.X, 150f, 205f);
        Assert.True(pos.Y < 5f, $"nobody walks up a sheer face, got Y={pos.Y}");
    }

    [Fact]
    public void A_ledge_drop_is_still_allowed()
    {
        var geo = Geo(Cliff());

        // Leaping off the plateau: the mesh has no edge down the face, but
        // drops of any size are legal — the transfer lands under the target.
        var fall = geo.Move(new Vector3(310, 300, 200), new Vector3(-160, 0, 0));
        Assert.Equal(150f, fall.X, 0);
        Assert.Equal(200f, fall.Z, 0);
        Assert.InRange(fall.Y, -1f, 1f);
    }

    [Fact]
    public void Decks_step_up_and_walls_stall()
    {
        // A deck low enough to step onto (top 15 ≤ StepHeight) and a wall.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(400, 0, 400)))
            .AddBox(new Aabb(new Vector3(150, 0, 150), new Vector3(250, 15, 250)))
            .AddBox(new Aabb(new Vector3(300, 0, 100), new Vector3(340, 50, 300)))
            .Build();
        var geo = Geo(soup);
        var pos = new Vector3(50, 0, 200);
        var rodeDeck = false;

        for (var i = 0; i < 60; i++)
        {
            pos = geo.Move(pos, new Vector3(TickStep, 0, 0));
            if (pos.X is > 170f and < 230f && pos.Y is > 13f and < 17f)
                rodeDeck = true;
        }

        Assert.True(rodeDeck, "the walker never rode the deck top");
        Assert.InRange(pos.X, 270f, 300f); // stalled at the wall, an eroded radius early
        Assert.True(pos.Y < 5f, $"back on the floor after the deck, got Y={pos.Y}");
    }

    [Fact]
    public void Sight_lines_block_on_structure_and_clear_in_the_open()
    {
        var geo = Geo(Cliff());

        // Up through the plateau's sheer face: blocked.
        var lowEye = new Vector3(50, SimConstants.EyeHeight, 200);
        var highEye = new Vector3(350, 300 + SimConstants.EyeHeight, 200);
        Assert.False(geo.HasLineOfSight(lowEye, highEye));

        // Across the open low floor: clear.
        Assert.True(geo.HasLineOfSight(lowEye, new Vector3(150, SimConstants.EyeHeight, 250)));
    }

    [Fact]
    public void Cursor_rays_land_on_the_walkable_surface()
    {
        var geo = Geo(Ramp());
        var origin = new Vector3(250, 200, 200);
        var direction = Vector3.Normalize(new Vector3(0.5f, -1f, 0f));

        Assert.True(geo.RaycastGround(origin, direction, 600, out var hit));
        Assert.InRange(hit.X, 320f, 345f);
        Assert.True(MathF.Abs(hit.Y - RampHeight(hit.X)) < 3f,
            $"cursor landed at Y={hit.Y}, the ramp there is {RampHeight(hit.X)}");
    }

    [Fact]
    public void A_path_routes_around_what_a_straight_line_cannot()
    {
        // A long wall with one gap at its southern end. Sliding along it never
        // finds the gap; the path planner must.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(400, 0, 400)))
            .AddBox(new Aabb(new Vector3(200, 0, 0), new Vector3(220, 50, 300)))
            .Build();
        var geo = Geo(soup);
        var start = new Vector3(100, 0, 150);
        var target = new Vector3(300, 0, 150);

        var stalled = start;
        for (var i = 0; i < 60; i++)
            stalled = geo.Move(stalled, Vector3.Normalize((target - stalled) with { Y = 0 }) * TickStep);
        Assert.True(stalled.X < 200f, $"straight-line steering should stall at the wall, got X={stalled.X}");

        var waypoints = new System.Collections.Generic.List<Vector3>();
        Assert.True(geo.TryFindPath(start, target, waypoints), "no path around the wall");
        var arrived = WalkPath(geo, start, waypoints, 400);
        Assert.True(((arrived - target) with { Y = 0 }).Length() < 15f,
            $"the routed walker should reach the far side, got ({arrived.X:F0},{arrived.Z:F0})");
    }

    [Fact]
    public void A_wide_boss_is_refused_where_characters_slip_through()
    {
        // A 55-unit doorway: room for a character (radius 14), not for the
        // boss (radius 30). One realm, two baked agent classes.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(400, 0, 400)))
            .AddBox(new Aabb(new Vector3(200, 0, 0), new Vector3(220, 60, 172.5f)))
            .AddBox(new Aabb(new Vector3(200, 0, 227.5f), new Vector3(220, 60, 400)))
            .Build();
        var geo = new RealmGeometry(soup, new Vector3(50, 0, 200),
            (SimConstants.CharacterRadius, NavMeshBuilder.Build(soup)),
            (30f, NavMeshBuilder.Build(soup, agentRadius: 30f)));

        var character = new Vector3(100, 0, 200);
        var boss = new Vector3(100, 0, 200);
        for (var i = 0; i < 60; i++)
        {
            character = geo.Move(character, new Vector3(TickStep, 0, 0));
            boss = geo.Move(boss, new Vector3(TickStep, 0, 0), radius: 30f);
        }
        Assert.True(character.X > 300f, $"the character fits the doorway, got X={character.X}");
        Assert.True(boss.X < 200f, $"the boss must not fit the doorway, got X={boss.X}");

        // Planning at boss width ends at the nearest reachable ground, not beyond the door.
        var waypoints = new System.Collections.Generic.List<Vector3>();
        Assert.True(geo.TryFindPath(new Vector3(100, 0, 200), new Vector3(300, 0, 200), waypoints, radius: 30f));
        Assert.True(waypoints[^1].X < 200f,
            $"a boss-width route must stop short of the doorway, got X={waypoints[^1].X}");
    }

    [Fact]
    public void An_aggroed_enemy_routes_through_the_gap_to_its_prey()
    {
        // The wall-with-a-gap land, run through the REAL enemy AI: the prey
        // stands across the wall, the straight line is blocked, and the hunt
        // must thread the gap on its cached route.
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(0, -20, 0), new Vector3(400, 0, 400)))
            .AddBox(new Aabb(new Vector3(200, 0, 0), new Vector3(220, 50, 300)))
            .Build();
        var world = new GameWorld { Geometry = Geo(soup) };
        var player = world.AddPlayer(1, "Prey");
        player.Position = new Vector3(300, 0, 150);
        var enemy = world.SpawnEnemy(new Vector3(100, 0, 150));
        enemy.Aggroed = true; // the wall denies line-of-sight aggro; the hunt itself is under test

        float DistanceToPrey() => ((player.Position - enemy.Position) with { Y = 0 }).Length();
        for (var i = 0; i < 30 * SimConstants.TickRate && DistanceToPrey() > 60f; i++)
            world.Step();

        Assert.True(DistanceToPrey() <= 60f,
            $"the enemy should have rounded the wall to ({player.Position.X:F0},{player.Position.Z:F0}), " +
            $"got stuck at ({enemy.Position.X:F0},{enemy.Position.Z:F0})");
    }

    [Fact]
    public void Bakes_are_deterministic()
    {
        var soup = Ramp();
        var first = NavMeshBuilder.Serialize(NavMeshBuilder.BuildMeshData(soup));
        var second = NavMeshBuilder.Serialize(NavMeshBuilder.BuildMeshData(soup));
        Assert.True(first.AsSpan().SequenceEqual(second), "two bakes of the same soup produced different bytes");
    }

    [Fact]
    public void A_serialized_mesh_round_trips_into_a_walkable_realm()
    {
        var soup = Ramp();
        var bytes = NavMeshBuilder.Serialize(NavMeshBuilder.BuildMeshData(soup));

        var geo = new RealmGeometry(NavMeshBuilder.Deserialize(bytes), soup, new Vector3(50, 0, 200));
        var pos = geo.Move(new Vector3(50, 0, 200), new Vector3(TickStep, 0, 0));
        Assert.True(pos.X > 50f, "the round-tripped mesh refused a plain step east");
    }

    [Fact]
    public void The_crag_routes_and_walks_spawn_to_boss_court()
    {
        var realm = LoadRealm("Crag.json");
        if (realm?.Soup is not { } soup)
            return; // outside the repo layout, or the realm is not yet regenerated
        Assert.NotNull(realm.BossSpawn);
        var nav = new RealmGeometry(NavMeshBuilder.Build(soup), soup, realm.SpawnPoint);

        var boss = realm.BossSpawn!.Value;
        var waypoints = new System.Collections.Generic.List<Vector3>();
        Assert.True(nav.TryFindPath(realm.SpawnPoint, boss, waypoints), "no route from spawn to the boss court");
        var pos = WalkPath(nav, realm.SpawnPoint, waypoints, 4000);

        Assert.True(((pos - boss) with { Y = 0 }).Length() < 40f,
            $"the route should end at the boss court {boss}, walker reached {pos}");
        Assert.True(MathF.Abs(pos.Y - nav.GroundHeight(pos)) < 10f,
            $"Y={pos.Y} is off the floor ({nav.GroundHeight(pos)}) at {pos.X},{pos.Z}");
    }

    [Fact]
    public void The_sunken_crypt_routes_and_descends_to_its_boss()
    {
        var realm = LoadRealm("Crypt.json");
        if (realm?.Soup is not { } soup)
            return; // outside the repo layout, or the realm is not yet regenerated
        Assert.NotNull(realm.BossSpawn);
        var nav = new RealmGeometry(NavMeshBuilder.Build(soup), soup, realm.SpawnPoint);

        var boss = realm.BossSpawn!.Value;
        var waypoints = new System.Collections.Generic.List<Vector3>();
        Assert.True(nav.TryFindPath(realm.SpawnPoint, boss, waypoints), "no route from the door to the boss");
        var pos = WalkPath(nav, realm.SpawnPoint, waypoints, 4000);

        Assert.True(((pos - boss) with { Y = 0 }).Length() < 40f,
            $"the route should end at the boss {boss}, walker reached {pos}");
        Assert.True(pos.Y < realm.SpawnPoint.Y - 40f,
            $"the crypt descends — boss Y={pos.Y} should sit well under the door Y={realm.SpawnPoint.Y}");
    }

    /// <summary>Steer waypoint to waypoint through Move, the way an AI follower would.</summary>
    private static Vector3 WalkPath(RealmGeometry geo, Vector3 start,
                                    System.Collections.Generic.IReadOnlyList<Vector3> waypoints,
                                    int maxTicks, float radius = SimConstants.CharacterRadius)
    {
        var pos = start;
        var next = 0;
        for (var i = 0; i < maxTicks && next < waypoints.Count; i++)
        {
            var wp = waypoints[next];
            var toWp = new Vector3(wp.X - pos.X, 0, wp.Z - pos.Z);
            if (toWp.Length() <= TickStep)
            {
                next++;
                continue;
            }
            pos = geo.Move(pos, Vector3.Normalize(toWp) * TickStep, radius);
        }
        return pos;
    }

    [Fact]
    public void Open_sky_has_no_ceiling()
    {
        Assert.Equal(float.PositiveInfinity, TestRealms.Open().CeilingHeight(new Vector3(0, 20, 0)));
    }

    [Fact]
    public void A_roof_slab_is_the_ceiling_over_the_floor_beneath_it()
    {
        var geo = TestRealms.WithWalls(new Aabb(new Vector3(-200, 150, -200), new Vector3(200, 174, 200)));
        Assert.Equal(150f, geo.CeilingHeight(new Vector3(0, 20, 0)), 1f);
        // Step outside the roof's footprint and the sky opens again.
        Assert.Equal(float.PositiveInfinity, geo.CeilingHeight(new Vector3(400, 20, 0)));
    }

    [Fact]
    public void The_ceiling_is_the_lowest_thing_overhead_not_the_highest()
    {
        var geo = TestRealms.WithWalls(
            new Aabb(new Vector3(-200, 150, -200), new Vector3(200, 174, 200)),
            new Aabb(new Vector3(-200, 300, -200), new Vector3(200, 324, 200)));
        Assert.Equal(150f, geo.CeilingHeight(new Vector3(0, 20, 0)), 1f);
    }

    private static RealmDefinition? LoadRealm(string mapFile)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "WoadRaiders.Client", "maps", mapFile);
            if (File.Exists(candidate))
                return RealmDefinitionFile.Load(candidate);
        }
        return null;
    }
}

using System;
using System.IO;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The realm's movement rules, judged on built geometry: baking soups
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

    // A pit floor at y=-400 with a bridge deck laid across it at y=0 —
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
    public void A_short_steep_face_descends_by_link()
    {
        // 80° is far past the navmesh's 67.8° limit, so the mesh ends at the
        // lip — but a face this short is one the sim's physical rules ride
        // down whole, so the bake's scout finishes the descent and earns a
        // drop link. Movement is navmesh-only: the walk stalls at the lip and
        // the stall is a crossing, not a trap. The OUTCOME pinned is the same
        // as it always was — a steep face is a way down — only the mechanism
        // moved from Move's hatches to a baked link.
        var rise = 60f;
        var run = rise / MathF.Tan(80f * MathF.PI / 180f);
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-400, -20, 0), new Vector3(0, 0, 400)))          // floor below
            .AddQuad(new Vector3(0, 0, 0), new Vector3(run, rise, 0),
                     new Vector3(run, rise, 400), new Vector3(0, 0, 400))                 // the face
            .AddBox(new Aabb(new Vector3(run, rise - 20, 0), new Vector3(run + 400, rise, 400))) // plateau above
            .Build();
        var geo = new RealmGeometry(NavMeshBuilder.Build(soup), soup, new Vector3(200, rise, 200));

        var pos = new Vector3(200, rise, 200);
        for (var i = 0; i < 40; i++)
            pos = geo.Move(pos, new Vector3(-TickStep, 0, 0));
        Assert.True(pos.Y > rise - 5f,
            $"the mesh-only walk should stall on the plateau's lip, got ({pos.X:0},{pos.Y:0})");

        // The push that stalled boards a baked link that lands at the foot.
        var code = geo.FindLink(pos, new Vector3(-1, 0, 0));
        Assert.True(code >= 0, "no drop link down the face — a descendable grade became a trap");
        Assert.True(geo.TryGetLink(code, out _, out var to));
        Assert.True(to.Y < 5f, $"the crossing should land on the floor below, got Y={to.Y:0}");
        Assert.True(to.X < 0f, $"the landing should sit out on the low floor's mesh, got X={to.X:0}");
    }

    [Fact]
    public void A_face_too_tall_to_ride_is_a_clean_wall_not_a_freeze()
    {
        // Under the old physics this 1100-unit 75° face was the live
        // stuck-player bug: floor-riding carried a walker over the lip and
        // partway down, where the mesh fell out of snap reach and Move
        // refused every input FOREVER — frozen mid-face. The bake's scout
        // runs the same physics, cannot finish the ride either, and earns no
        // link; navmesh-only movement then makes the honest call: the face
        // is a wall. The walker never leaves the mesh, never half-descends,
        // and can always walk away.
        var rise = 300f * MathF.Tan(75f * MathF.PI / 180f);
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-400, -20, 0), new Vector3(0, 0, 400)))        // floor below
            .AddQuad(new Vector3(0, 0, 0), new Vector3(300, rise, 0),
                     new Vector3(300, rise, 400), new Vector3(0, 0, 400))               // the face
            .AddBox(new Aabb(new Vector3(300, rise - 20, 0), new Vector3(700, rise, 400))) // plateau above
            .Build();
        var geo = new RealmGeometry(NavMeshBuilder.Build(soup), soup, new Vector3(500, rise, 200));

        var pos = new Vector3(500, rise, 200);
        for (var i = 0; i < 60; i++)
            pos = geo.Move(pos, new Vector3(-TickStep, 0, 0));

        // Stalled ON the plateau, at height — no limbo partway down.
        Assert.True(pos.X > 300f && pos.Y > rise - 5f,
            $"the walk must hold the plateau, got ({pos.X:0},{pos.Y:0})");
        // No link: the scout could not finish this descent, so nobody may start it.
        Assert.Equal(-1, geo.FindLink(pos, new Vector3(-1, 0, 0)));
        // And the walker is FREE — walking away works. That freedom is the
        // whole point of the rework; the old physics froze here.
        var back = geo.Move(pos, new Vector3(TickStep, 0, 0));
        Assert.True(back.X > pos.X + TickStep * 0.5f, "the walker must be free to leave the lip");
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

        // The 300-unit rise is a wall: the walk stalls where the floor's mesh
        // ends (an eroded radius before the toe). Nobody climbs, and there is
        // no upward link to board — drops never reverse.
        Assert.InRange(pos.X, 150f, 205f);
        Assert.True(pos.Y < 5f, $"nobody walks up a sheer face, got Y={pos.Y}");
        Assert.Equal(-1, geo.FindLink(pos, new Vector3(1, 0, 0)));
    }

    [Fact]
    public void A_ledge_drop_crosses_a_baked_link()
    {
        var geo = Geo(Cliff());

        // Leaping off the plateau: movement is navmesh-only, so the push
        // itself stalls at the rim — Move never leaves the mesh...
        var stalled = geo.Move(new Vector3(310, 300, 200), new Vector3(-160, 0, 0));
        Assert.True(stalled.X > 205f, $"the mesh-only move must not leave the plateau, got X={stalled.X:0}");
        Assert.InRange(stalled.Y, 295f, 305f);

        // ...and boards the drop link the rim scout earned. The landing sits
        // ON the low floor's mesh, nudged out of the eroded band at the
        // cliff's base — a landing IN the band would strand a mesh-only
        // mover the moment it set down.
        var code = geo.FindLink(stalled, new Vector3(-1, 0, 0));
        Assert.True(code >= 0, "no drop link off the plateau rim");
        Assert.True(geo.TryGetLink(code, out _, out var to));
        Assert.InRange(to.Y, -1f, 1f);
        Assert.True(to.X <= 185.5f, $"the landing must sit on the floor's mesh, got X={to.X:0}");
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
    public void A_baked_drop_rim_offers_a_link_and_only_downhill()
    {
        // The deck's rim scouts fall to the pit floor and land on open mesh,
        // earning one-way drop links (a sheer cliff's falls land inside the
        // eroded band at its base, where the scout finds no mesh — so it is
        // the DECK fixture that bakes links). A mover on the deck pushing
        // over the rim must find one to board; the same spot pushing inboard
        // — and the pit floor looking back up — must not. Lips seed every
        // ~50-60 units along the rim, so scan rather than bet on one seed.
        var soup = DeckOverPit();
        var geo = new RealmGeometry(NavMeshBuilder.Build(soup), soup, new Vector3(200, 0, 200));

        var code = -1;
        var stand = Vector3.Zero;
        for (var x = 40f; x <= 360f && code < 0; x += 10f)
        {
            stand = new Vector3(x, 0f, 233f); // on the deck, at its north rim
            code = geo.FindLink(stand, new Vector3(0, 0, 1));
        }
        Assert.True(code >= 0, "no boardable link anywhere along the deck's drop rim");
        Assert.True(geo.TryGetLink(code, out var from, out var to));
        Assert.True(from.Y > -20f, $"the lip should sit at deck level, got Y={from.Y:0}");
        Assert.True(to.Y < -380f, $"the landing should sit on the pit floor, got Y={to.Y:0}");
        Assert.True(to.Z > from.Z, "the crossing should carry the push out past the rim");

        // Pushing inboard from the same spot boards nothing (drops never reverse).
        Assert.Equal(-1, geo.FindLink(stand, new Vector3(0, 0, -1)));
        // Mid-deck is beyond any lip's board radius.
        Assert.Equal(-1, geo.FindLink(new Vector3(stand.X, 0, 200f), new Vector3(0, 0, 1)));
        // From the pit floor, every lip fails the headroom check and a
        // one-way landing is not a boardable end.
        Assert.Equal(-1, geo.FindLink(to, new Vector3(0, 0, 1)));

        // Codes are validated, never trusted: the reversed orientation of a
        // one-way link and out-of-range indices both refuse.
        Assert.False(geo.TryGetLink(code | 1, out _, out _));
        Assert.False(geo.TryGetLink(-1, out _, out _));
        Assert.False(geo.TryGetLink(1 << 20, out _, out _));
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

    [Fact]
    public void The_east_fault_flight_climbs_out_of_the_pit()
    {
        // The regression that shipped as "players fall through the stairs on
        // the far side of the pit": the span's kerbstone parapet ran across
        // the east flight's mouth, the join never meshed, and the only baked
        // crossings at the lip were plunges back to the pit floor — the old
        // physics fell there too (TryLedgeDrop through the tread gap), the
        // rework just made it constant. A straight climb must top out on the
        // deck, and a grazing drift must not be plucked off the rim by the
        // stair-edge drop links (FindLink's alignment gate).
        var realm = LoadRealm("Crypt.json");
        if (realm?.Soup is not { } soup)
            return; // outside the repo layout, or the realm is not yet regenerated
        var nav = new RealmGeometry(NavMeshBuilder.Build(soup), soup, realm.SpawnPoint);

        foreach (var driftX in new[] { 0f, -0.2f })
        {
            var world = new GameWorld { Geometry = nav };
            var player = world.AddPlayer(1, "climber");
            player.Position = new Vector3(6980, -880, 2560); // the flight's foot
            uint seq = 0;
            for (var t = 0; t < 30 * SimConstants.TickRate && player.Position.Y < -405f; t++)
            {
                world.SetInput(1, new PlayerInput { MoveX = driftX, MoveZ = -1f, Sequence = ++seq });
                world.Step();
            }
            Assert.True(player.Position.Y > -405f,
                $"drift {driftX}: the climb should top out on the deck, stalled at " +
                $"({player.Position.X:0},{player.Position.Y:0},{player.Position.Z:0})");
        }

        // And the flight must not be mountable from BESIDE or UNDER it: a
        // push into the flank from the pit floor stays on the pit floor —
        // no link may hoist a mover more than a step, and none may be
        // boarded through the treads (the second live regression).
        foreach (var push in new[] { new Vector3(1f, 0, 0), new Vector3(1f, 0, -1f) })
        {
            var world = new GameWorld { Geometry = nav };
            var player = world.AddPlayer(1, "lurker");
            player.Position = new Vector3(6880, -880, 2430); // beside the flight's foot
            uint seq = 0;
            var peak = player.Position.Y;
            for (var t = 0; t < 30 * 10; t++)
            {
                world.SetInput(1, new PlayerInput { MoveX = push.X, MoveZ = push.Z, Sequence = ++seq });
                world.Step();
                peak = MathF.Max(peak, player.Position.Y);
            }
            Assert.True(peak <= -880f + SimConstants.StepHeight + 1f,
                $"push ({push.X:0.#},{push.Z:0.#}) from beside the flight hoisted the mover to Y={peak:0}");
        }

        // Descending with slow, camera-drifted input (the third live report:
        // a careful walk down, 60° off the stair axis) must never board a
        // plunge — the conviction and commitment gates make grazes slide
        // along the rim instead of falling through the flight.
        foreach (var (speed, driftDeg) in new[] { (0.2f, -60f), (1f, -45f) })
        {
            var world = new GameWorld { Geometry = nav };
            var player = world.AddPlayer(1, "descender");
            // On the flight itself, just below its mouth — starting on the
            // deck instead sends a drifted push across the open west rim,
            // which is a genuine walk-off, not a stair descent.
            var start = new Vector3(6980, 0, 1400);
            player.Position = start with { Y = nav.GroundHeight(start with { Y = -390f }) };
            var a = driftDeg * MathF.PI / 180f;
            var push = new Vector3(MathF.Sin(a), 0, MathF.Cos(a)) * speed;
            uint seq = 0;
            for (var t = 0; t < 30 * 30; t++)
            {
                var before = player.Position.Y;
                world.SetInput(1, new PlayerInput { MoveX = push.X, MoveZ = push.Z, Sequence = ++seq });
                world.Step();
                Assert.False(player.Link.Active && player.Link.To.Y < before - 40f,
                    $"speed {speed} drift {driftDeg}: the descent boarded a plunge at Y={before:0}");
            }
        }
    }

    /// <summary>Steer waypoint to waypoint through Move, the way a follower
    /// would: mesh-only steps, and where a step stalls at a rim the route
    /// planned through, board the baked link and set down at its end.</summary>
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
            var dir = Vector3.Normalize(toWp);
            var moved = geo.Move(pos, dir * TickStep, radius);
            if ((moved - pos).LengthSquared() < 0.01f)
            {
                var code = geo.FindLink(pos, dir, radius);
                if (code >= 0 && geo.TryGetLink(code, out _, out var landing, radius))
                {
                    pos = landing;
                    continue;
                }
            }
            pos = moved;
        }
        return pos;
    }

    [Fact]
    public void Open_sky_has_no_ceiling()
    {
        Assert.Equal(float.PositiveInfinity, TestRealms.Open().CeilingHeight(new Vector3(0, 20, 0)));
    }

    [Fact]
    public void A_roof_is_the_ceiling_over_the_floor_beneath_it()
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

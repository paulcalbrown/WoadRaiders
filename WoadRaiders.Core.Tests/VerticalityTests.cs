using System;
using System.Linq;
using System.Numerics;
using WoadRaiders.Core;

namespace WoadRaiders.Core.Tests;

/// <summary>
/// The realm's verticality rules: the smooth heightfield base plane, the
/// step-height climb limit, ground-riding movement, terrain-aware sight
/// lines, cursor raycasts, and terrain-following projectiles.
/// </summary>
public class VerticalityTests
{
    // A 5x5 field over [0,400]²: flat west half at 0, a gentle rise to 50 at
    // the east edge (a walkable ramp: +25 per 100-unit cell).
    private static HeightField Ramp() => new(0, 0, 100, 5, 5,
        Enumerable.Range(0, 25).Select(i => (i % 5) switch { 3 => 25f, 4 => 50f, _ => 0f }).ToArray());

    // A cliff: flat at 0 until x=200, then 300 up over one cell — slope 3.0,
    // beyond the steepest walkable grade (StepHeight per player tick-step ≈ 2.45).
    private static HeightField Cliff() => new(0, 0, 100, 5, 5,
        Enumerable.Range(0, 25).Select(i => (i % 5) >= 3 ? 300f : 0f).ToArray());

    private static DungeonGeometry Geo(HeightField field, params Aabb[] solids) =>
        new(new Vector3(50, 0, 200), solids, Array.Empty<EnemySpawnPoint>(), field);

    [Fact]
    public void Bilinear_sampling_interpolates_and_clamps()
    {
        var field = Ramp();
        Assert.Equal(0f, field.Sample(100, 200), 3);
        Assert.Equal(25f, field.Sample(300, 200), 3);
        Assert.Equal(12.5f, field.Sample(250, 200), 3);   // halfway between samples
        Assert.Equal(50f, field.Sample(400, 200), 3);
        Assert.Equal(50f, field.Sample(4000, 200), 3);    // clamped past the rim
        Assert.Equal(0f, field.Sample(-500, -500), 3);
    }

    [Fact]
    public void HeightField_rejects_bad_input()
    {
        Assert.Throws<ArgumentException>(() => new HeightField(0, 0, 40, 1, 5, new float[5]));
        Assert.Throws<ArgumentException>(() => new HeightField(0, 0, 0, 2, 2, new float[4]));
        Assert.Throws<ArgumentException>(() => new HeightField(0, 0, 40, 2, 2, new float[3]));
        Assert.Throws<ArgumentException>(() => new HeightField(0, 0, 40, 2, 2,
            new[] { 0f, float.NaN, 0f, 0f }));
    }

    [Fact]
    public void Walking_a_gentle_slope_rides_the_ground()
    {
        var geo = Geo(Ramp());
        var pos = new Vector3(50, 0, 200);

        // Player-speed steps east up the ramp: Y must climb with the land.
        for (var i = 0; i < 60; i++)
            pos = geo.Move(pos, new Vector3(SimConstants.PlayerMoveSpeed * SimConstants.TickDelta, 0, 0));

        Assert.Equal(50f, pos.Y, 1);   // stood on the summit
        Assert.True(pos.X > 390f, $"the ramp should not impede walking, got X={pos.X}");
    }

    [Fact]
    public void A_cliff_refuses_the_climb_but_permits_the_drop()
    {
        var geo = Geo(Cliff());

        // Charging the cliff face stalls: each tick-step would rise ~22 — over
        // StepHeight — so the toe is as far as anyone gets.
        var pos = new Vector3(150, 0, 200);
        for (var i = 0; i < 60; i++)
            pos = geo.Move(pos, new Vector3(SimConstants.PlayerMoveSpeed * SimConstants.TickDelta, 0, 0));
        Assert.True(pos.Y < 60f, $"nobody walks up a cliff, got Y={pos.Y}");

        // Leaping off the top is always allowed — one move, straight down to the floor.
        var fall = geo.Move(new Vector3(310, 300, 200), new Vector3(-160, 0, 0));
        Assert.Equal(0f, fall.Y, 0);
    }

    [Fact]
    public void Solid_tops_within_step_height_are_stairs()
    {
        // A knee-high slab (16 — under StepHeight 18), then a 32-high one: a
        // second 16-rise step FROM the first, but a wall from the bare ground.
        var geo = Geo(
            new HeightField(0, 0, 100, 5, 5, new float[25]),
            new Aabb(new Vector3(100, 0, 150), new Vector3(200, 16, 250)),
            new Aabb(new Vector3(200, 0, 150), new Vector3(300, 32, 250)));

        var pos = new Vector3(60, 0, 200);
        pos = geo.Move(pos, new Vector3(60, 0, 0));
        Assert.Equal(16f, pos.Y, 3); // stepped up onto the low slab

        pos = geo.Move(pos, new Vector3(100, 0, 0));
        Assert.Equal(32f, pos.Y, 3); // and up again: 16 → 32 is another legal step

        // From the ground, though, 32 is over StepHeight — a wall, not a stair.
        var charge = geo.Move(new Vector3(260, 0, 300), new Vector3(0, 0, -60));
        Assert.Equal(0f, charge.Y, 3);
        Assert.True(charge.Z >= 250f + SimConstants.CharacterRadius - 0.1f,
            $"a 32-high slab is a wall from the ground, got Z={charge.Z}");
    }

    [Fact]
    public void A_ledge_over_step_height_is_a_wall()
    {
        var geo = Geo(
            new HeightField(0, 0, 100, 5, 5, new float[25]),
            new Aabb(new Vector3(100, 0, 150), new Vector3(200, 30, 250))); // 30-high ledge

        var pos = new Vector3(60, 0, 200);
        for (var i = 0; i < 30; i++)
            pos = geo.Move(pos, new Vector3(8, 0, 0));

        Assert.Equal(0f, pos.Y, 3);
        Assert.True(pos.X <= 100f - SimConstants.CharacterRadius + 0.1f,
            $"a 30-high ledge is a wall, got X={pos.X}");
    }

    [Fact]
    public void A_landing_without_headroom_is_refused()
    {
        // A deck at 10 (steppable) under a beam leaving less than body height.
        var geo = Geo(
            new HeightField(0, 0, 100, 5, 5, new float[25]),
            new Aabb(new Vector3(100, 0, 150), new Vector3(200, 10, 250)),   // the step
            new Aabb(new Vector3(100, 40, 150), new Vector3(200, 60, 250))); // the beam: 30 of headroom

        var pos = geo.Move(new Vector3(80, 0, 200), new Vector3(30, 0, 0));
        Assert.Equal(80f, pos.X, 3); // refused — a character is 44 tall
    }

    [Fact]
    public void Terrain_blocks_sight_lines_over_the_crest()
    {
        // A 60-high knoll between two watchers at eye height on flat ground.
        var heights = new float[25];
        heights[2 * 5 + 2] = 60f; // wait: row-major [z * W + x] — the centre sample
        var geo = Geo(new HeightField(0, 0, 100, 5, 5, heights));

        var eye = new Vector3(0, SimConstants.EyeHeight, 0);
        var west = new Vector3(50, 0, 200) + eye;
        var east = new Vector3(350, 0, 200) + eye;
        Assert.False(geo.HasLineOfSight(west, east)); // the knoll crest tops eye height
        Assert.True(geo.HasLineOfSight(west + new Vector3(0, 80, 0), east + new Vector3(0, 80, 0)));
    }

    [Fact]
    public void Cursor_rays_land_on_terrain_and_bridge_decks()
    {
        var deck = new Aabb(new Vector3(150, 96, 150), new Vector3(250, 100, 250));
        var geo = Geo(Ramp(), deck);

        // Straight down onto the west flat: lands at ground 0.
        Assert.True(geo.RaycastGround(new Vector3(80, 500, 200), -Vector3.UnitY, 1000, out var hit));
        Assert.Equal(0f, hit.Y, 1);

        // Straight down over the deck: lands on the deck top, not the ground under it.
        Assert.True(geo.RaycastGround(new Vector3(200, 500, 200), -Vector3.UnitY, 1000, out hit));
        Assert.Equal(100f, hit.Y, 1);

        // A ray that never meets the land reports a miss.
        Assert.False(geo.RaycastGround(new Vector3(0, 500, 0), Vector3.UnitY, 1000, out _));
    }

    [Fact]
    public void Player_bolts_follow_the_slope()
    {
        var world = new GameWorld(new Random(7)) { Geometry = Geo(Ramp()) };
        var mage = world.AddPlayer(1, "m", CharacterClass.Mage);
        mage.Position = new Vector3(50, 0, 200);

        // Fire east, straight up the ramp.
        world.SetInput(1, new PlayerInput { AimX = 1, Attack = true, Sequence = 1 });
        world.Step();
        var bolt = Assert.Single(world.Projectiles.Values);

        for (var i = 0; i < 15 && world.Projectiles.Count > 0; i++)
            world.Step();

        // Partway up the ramp the bolt has climbed with the ground: it hugs
        // ground + eye height instead of burying itself in the rise.
        Assert.True(world.Projectiles.Count == 0 || bolt.Position.Y > SimConstants.EyeHeight + 5f,
            $"the bolt should ride the slope, got Y={bolt.Position.Y}");
    }

    [Fact]
    public void Player_bolts_hold_level_over_a_sharp_drop()
    {
        // Flat at 200 until x=200, then a pit at 0 (a gorge crossing).
        var heights = Enumerable.Range(0, 25).Select(i => (i % 5) >= 3 ? 0f : 200f).ToArray();
        var world = new GameWorld(new Random(7)) { Geometry = Geo(new HeightField(0, 0, 100, 5, 5, heights)) };
        var mage = world.AddPlayer(1, "m", CharacterClass.Mage);
        mage.Position = new Vector3(120, 200, 200);

        world.SetInput(1, new PlayerInput { AimX = 1, Attack = true, Sequence = 1 });
        world.Step();
        var bolt = Assert.Single(world.Projectiles.Values);
        var launchY = bolt.Position.Y;

        for (var i = 0; i < 12 && world.Projectiles.ContainsKey(bolt.Id); i++)
            world.Step();

        // Over the pit the ground fell away far beyond a step: the bolt sails
        // level, Gauntlet-style, rather than diving into the gorge.
        Assert.True(bolt.Position.Y > launchY - SimConstants.StepHeight - 1f,
            $"the bolt should hold level over the drop, got Y={bolt.Position.Y} from {launchY}");
    }

    [Fact]
    public void Enemy_bolts_aim_down_the_hill_in_3d()
    {
        // The mage holds a 120-high shelf; the player stands on the floor below
        // it — inside attack range (3D distance) with sight over the lip.
        var heights = Enumerable.Range(0, 25).Select(i => (i % 5) <= 1 ? 120f : 0f).ToArray();
        var world = new GameWorld(new Random(7)) { Geometry = Geo(new HeightField(0, 0, 100, 5, 5, heights)) };

        var player = world.AddPlayer(1, "p");
        player.Position = new Vector3(200, 0, 200);
        world.SpawnEnemy(new Vector3(80, 120, 200), EnemyType.Mage);

        for (var i = 0; i < 8 && world.Projectiles.Count == 0; i++)
            world.Step();

        var bolt = Assert.Single(world.Projectiles.Values);
        Assert.True(bolt.Velocity.Y < -1f, $"the bolt should dive at its target, got dY={bolt.Velocity.Y}");
        Assert.False(bolt.FollowsTerrain); // enemy bolts fly dead straight at the mark
    }
}

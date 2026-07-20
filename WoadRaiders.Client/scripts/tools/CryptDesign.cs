using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb;

namespace WoadRaiders.Client;

/// <summary>
/// The Sunken Crypt's DESIGN — a TRUE interior: rooms with flat stone floors,
/// standing walls, linteled doorways, and real roofs overhead, sinking chamber
/// by chamber into the earth. No landscape anywhere; the necropolis is masonry
/// all the way down.
///
///   undercroft (0) → descent stair → hall of the dead → processional stair →
///   the span over the chasm (a bridge, and a long stair back out of the pit)
///   → east landing → deep stair → the catacombs → the low gallery → the
///   Mausoleum
///
/// Dropping off the span into the chasm is a legal fall — the chasm stair
/// climbs back to the landing, so the fall is a detour, never a grave.
///
/// THE PLAN IS DRAWN SMALL AND BUILT LARGE. Every chamber below is stated in
/// PLAN units — the realm's original, legible floor plan — and <see cref="S"/>
/// multiplies them out to world units, giving ten times the floor area of the
/// first cut. Architecture (wall thickness, doors, ceilings, pillars) is stated
/// in WORLD units instead and does NOT scale with the plan: a grander hall
/// wants a grander door, but a wall is still a wall and a raider is still 44
/// units tall. Wanting a different size of crypt means changing <see cref="S"/>
/// alone — the dressing rides along, because it is laid out from the chambers
/// this file records rather than from coordinates of its own.
/// </summary>
public sealed partial class CryptDesign : IRealmDesign
{
    public string Name => "Crypt";

    /// <summary>Plan units to world units. 3.16 ≈ √10, so ten times the floor area.</summary>
    private const float Scale = 3.16f;

    // Architecture, in WORLD units — deliberately not scaled with the plan.
    private const float WallThickness = 60f;
    private const float RoomHeight = 620f;      // chambers stand tall; the chase camera comes inside
    private const float CorridorHeight = 380f;  // passages stay lower, and press in
    private const float DoorWidth = 420f;
    private const float DoorHeight = 300f;
    private const float PillarHalf = 65f;
    private const float PillarSpacing = 620f;

    /// <summary>
    /// How high the boss's dais stands off the Mausoleum floor. A WORLD
    /// constant, and it MUST stay under <see cref="SimConstants.StepHeight"/>
    /// (18): the dais is a low plate the raider walks onto, and a dais taller
    /// than a step is a plinth that entombs the boss — nothing can climb it,
    /// and every cell in the realm reports as stranded.
    /// </summary>
    private const float DaisRise = 16f;

    private static readonly Color OldStone = new(0.34f, 0.34f, 0.38f);
    private static readonly Color DarkStone = new(0.24f, 0.24f, 0.29f);
    private static readonly Color BoneWhite = new(0.58f, 0.56f, 0.50f);

    private RealmScene _scene = null!;
    private Material _floor = null!;
    private Material _wall = null!;
    private Material _bone = null!;

    /// <summary>
    /// A chamber the plan laid down, in WORLD units, kept so the dressing and
    /// the lighting can walk the rooms instead of restating their coordinates.
    /// </summary>
    private readonly record struct Chamber(
        string Name, float X0, float Z0, float X1, float Z1, float FloorY, float TopY)
    {
        public float MidX => (X0 + X1) * 0.5f;
        public float MidZ => (Z0 + Z1) * 0.5f;
        public float Width => X1 - X0;
        public float Depth => Z1 - Z0;
    }

    /// <summary>A stair corridor between chambers, likewise in WORLD units.</summary>
    private readonly record struct Passage(
        float X0, float Z0, float X1, float Z1, float LowY, float TopY, bool AlongZ);

    private readonly List<Chamber> _chambers = new();
    private readonly List<Passage> _passages = new();

    /// <summary>The chamber the plan recorded under this name.</summary>
    private Chamber Named(string name) => _chambers.Find(c => c.Name == name);

    /// <summary>Plan units → world units.</summary>
    private static float S(float plan) => plan * Scale;

    public RealmScene Build()
    {
        _scene = new RealmScene();
        _floor = StoneSurfaces.Cut(OldStone, grain: 340f, seed: 1301, roughness: 0.94f, relief: 1.1f);
        _wall = StoneSurfaces.Cut(DarkStone, grain: 470f, seed: 1607, roughness: 0.96f, relief: 1.8f);
        _bone = StoneSurfaces.Cut(BoneWhite, grain: 220f, seed: 1913, roughness: 0.82f, relief: 0.9f);

        // ------------------------------------------------------- the chambers
        // The undercroft — the way in.
        Room("undercroft", 200, 1500, 900, 2100, floorY: 0, doorEast: 1800);
        // The descent stair, walled like the corridor it is.
        Corridor(900, 1700, 1500, 1900, fromY: 0, toY: -80);
        // The hall of the dead.
        Room("hall", 1500, 1400, 2500, 2200, floorY: -80, doorWest: 1800, doorEast: 1800);
        PillarRows(1700, 1600, 2300, 2000, floorY: -80);
        // The processional stair down.
        Corridor(2500, 1700, 3100, 1900, fromY: -80, toY: -160);
        // The span: a chasm chamber, its bridge, and the long stair out of the
        // pit — climbing the south air to land back ON the bridge itself.
        Room("span", 3100, 1200, 3700, 2400, floorY: -300, wallTopY: -60,
                doorWest: 1800, doorWestY: -160, doorEast: 1800, doorEastY: -160);
        _scene.AddFloor(Box(S(3100), S(-172), S(1740), S(3700), S(-160), S(1860)), _bone, "Span");
        _scene.AddStairs(new Vector3(S(3250), S(-300), S(2300)), new Vector3(S(3550), S(-160), S(1850)),
                         S(120), _floor);
        // The east landing beyond the span.
        Room("landing", 3700, 1500, 3900, 2100, floorY: -160, doorWest: 1800, doorSouth: 3800);
        // The deep stair south.
        Corridor(3700, 2100, 3900, 2700, fromY: -160, toY: -240, alongZ: true);
        // The catacombs — a pillared maze.
        Room("catacombs", 3200, 2700, 3900, 3300, floorY: -240, doorNorth: 3800, doorWest: 3000);
        PillarRows(3280, 2800, 3840, 3220, floorY: -240, spacing: 400f); // a maze wants them close
        // The low gallery westward, easing down the last twenty. Its ends are
        // stated to MATCH the floors it joins — west is the Mausoleum (-260),
        // east the catacombs (-240) — so the walk is flush at both mouths
        // rather than a drop the raider cannot climb back up.
        Corridor(2600, 2900, 3200, 3100, fromY: -260, toY: -240);
        // The Mausoleum — the boss's tall chamber.
        Room("mausoleum", 1800, 2600, 2600, 3400, floorY: -260, wallTopY: -40, doorEast: 3000);
        var dais = Named("mausoleum");
        _scene.AddFloor(Box(dais.MidX - 474f, dais.FloorY, dais.MidZ - 474f,
                            dais.MidX + 474f, dais.FloorY + DaisRise, dais.MidZ + 474f), _bone, "BossDais");

        Cast();
        Relics(); // the imported kit pass (CryptDesign.Relics.cs) — pure scenery
        Gloom();
        return _scene;
    }

    /// <summary>
    /// The garrison. Camps are laid out as FRACTIONS of the chamber they hold,
    /// so widening the plan spreads them through the new floor instead of
    /// leaving them huddled at the old coordinates. The server caps live
    /// enemies at 40 (SpawnDirector), which is what this fills.
    /// </summary>
    private void Cast()
    {
        _scene.SetPlayerSpawn(_scene.OnFloor(S(350), S(1800)));

        // A camp at a fraction across a chamber — (0,0) its north-west corner.
        void Camp(EnemyType type, string room, float u, float v)
        {
            var c = Named(room);
            _scene.AddEnemy(type, _scene.OnFloor(c.X0 + c.Width * u, c.Z0 + c.Depth * v));
        }

        // The undercroft: a thin picket, so the way in stays survivable.
        Camp(EnemyType.Minion, "undercroft", 0.55f, 0.25f);
        Camp(EnemyType.Minion, "undercroft", 0.55f, 0.75f);
        Camp(EnemyType.Minion, "undercroft", 0.80f, 0.50f);

        // The hall of the dead: the realm's first real fight, spread wide
        // between the pillar lines with casters holding the far end.
        Camp(EnemyType.Minion, "hall", 0.20f, 0.22f);
        Camp(EnemyType.Minion, "hall", 0.20f, 0.78f);
        Camp(EnemyType.Minion, "hall", 0.38f, 0.50f);
        Camp(EnemyType.Rogue, "hall", 0.55f, 0.20f);
        Camp(EnemyType.Rogue, "hall", 0.55f, 0.80f);
        Camp(EnemyType.Minion, "hall", 0.70f, 0.35f);
        Camp(EnemyType.Minion, "hall", 0.70f, 0.65f);
        Camp(EnemyType.Mage, "hall", 0.86f, 0.24f);
        Camp(EnemyType.Mage, "hall", 0.86f, 0.76f);

        // The span: sentries on the bridge, and the drowned garrison below.
        Camp(EnemyType.Rogue, "span", 0.50f, 0.48f);
        Camp(EnemyType.Rogue, "span", 0.72f, 0.52f);
        Camp(EnemyType.Minion, "span", 0.30f, 0.14f);
        Camp(EnemyType.Minion, "span", 0.70f, 0.12f);
        Camp(EnemyType.Minion, "span", 0.35f, 0.88f);
        Camp(EnemyType.Minion, "span", 0.68f, 0.86f);

        // The east landing — a squeeze on the shelf.
        Camp(EnemyType.Rogue, "landing", 0.50f, 0.22f);
        Camp(EnemyType.Rogue, "landing", 0.50f, 0.78f);
        Camp(EnemyType.Minion, "landing", 0.50f, 0.50f);

        // The catacombs: the maze is the point — many, and scattered.
        Camp(EnemyType.Minion, "catacombs", 0.18f, 0.20f);
        Camp(EnemyType.Minion, "catacombs", 0.18f, 0.80f);
        Camp(EnemyType.Rogue, "catacombs", 0.35f, 0.50f);
        Camp(EnemyType.Minion, "catacombs", 0.50f, 0.18f);
        Camp(EnemyType.Minion, "catacombs", 0.50f, 0.82f);
        Camp(EnemyType.Rogue, "catacombs", 0.66f, 0.34f);
        Camp(EnemyType.Rogue, "catacombs", 0.66f, 0.66f);
        Camp(EnemyType.Mage, "catacombs", 0.84f, 0.22f);
        Camp(EnemyType.Mage, "catacombs", 0.84f, 0.78f);
        Camp(EnemyType.Minion, "catacombs", 0.82f, 0.50f);

        // The Mausoleum: the boss's court, its honour guard drawn around the
        // dais rather than on it.
        Camp(EnemyType.Minion, "mausoleum", 0.22f, 0.24f);
        Camp(EnemyType.Minion, "mausoleum", 0.22f, 0.76f);
        Camp(EnemyType.Rogue, "mausoleum", 0.32f, 0.50f);
        Camp(EnemyType.Mage, "mausoleum", 0.16f, 0.50f);
        Camp(EnemyType.Mage, "mausoleum", 0.50f, 0.16f);
        Camp(EnemyType.Mage, "mausoleum", 0.50f, 0.84f);
        Camp(EnemyType.Rogue, "mausoleum", 0.78f, 0.28f);
        Camp(EnemyType.Rogue, "mausoleum", 0.78f, 0.72f);

        var court = Named("mausoleum");
        _scene.SetBossSpawn(_scene.OnFloor(court.MidX, court.MidZ));
    }

    // ----------------------------------------------------------- vocabulary

    private static Aabb Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        new(new System.Numerics.Vector3(x0, y0, z0), new System.Numerics.Vector3(x1, y1, z1));

    /// <summary>
    /// A rectangular chamber, stated in PLAN units: a thick floor slab, four
    /// walls (each with an optional linteled doorway at a given coordinate),
    /// and a roof. Door Y defaults to the floor; a chasm chamber passes the
    /// height its doors open at instead. Recorded in <see cref="_chambers"/>
    /// under <paramref name="name"/> for the dressing and lighting to find.
    /// </summary>
    private void Room(string name, float x0, float z0, float x1, float z1, float floorY,
                      float? wallTopY = null,
                      float? doorNorth = null, float? doorSouth = null,
                      float? doorWest = null, float? doorEast = null,
                      float? doorWestY = null, float? doorEastY = null)
    {
        float wx0 = S(x0), wz0 = S(z0), wx1 = S(x1), wz1 = S(z1), wFloor = S(floorY);
        var top = wallTopY is { } t ? S(t) : wFloor + RoomHeight;

        _scene.AddFloor(Box(wx0, wFloor - 90f, wz0, wx1, wFloor, wz1), _floor);
        // North/south walls run along X (z fixed); west/east along Z.
        WallX(wx0, wx1, wz0, wFloor, top, Door(doorNorth), wFloor);
        WallX(wx0, wx1, wz1 - WallThickness, wFloor, top, Door(doorSouth), wFloor);
        WallZ(wz0, wz1, wx0, wFloor, top, Door(doorWest), doorWestY is { } w ? S(w) : wFloor);
        WallZ(wz0, wz1, wx1 - WallThickness, wFloor, top, Door(doorEast), doorEastY is { } e ? S(e) : wFloor);
        _scene.AddStructure(Box(wx0, top, wz0, wx1, top + WallThickness, wz1), _wall); // the roof

        _chambers.Add(new Chamber(name, wx0, wz0, wx1, wz1, wFloor, top));
    }

    private static float? Door(float? planAt) => planAt is { } d ? S(d) : null;

    /// <summary>A wall along X at a fixed Z, split around a doorway when one is given.</summary>
    private void WallX(float x0, float x1, float z, float floorY, float topY, float? doorAt, float doorY)
    {
        if (doorAt is { } d)
        {
            _scene.AddStructure(Box(x0, floorY, z, d - DoorWidth / 2, topY, z + WallThickness), _wall);
            _scene.AddStructure(Box(d + DoorWidth / 2, floorY, z, x1, topY, z + WallThickness), _wall);
            _scene.AddStructure(Box(d - DoorWidth / 2, doorY + DoorHeight, z, d + DoorWidth / 2, topY,
                                    z + WallThickness), _wall);
        }
        else
        {
            _scene.AddStructure(Box(x0, floorY, z, x1, topY, z + WallThickness), _wall);
        }
    }

    /// <summary>A wall along Z at a fixed X, split around a doorway when one is given.</summary>
    private void WallZ(float z0, float z1, float x, float floorY, float topY, float? doorAt, float doorY)
    {
        if (doorAt is { } d)
        {
            _scene.AddStructure(Box(x, floorY, z0, x + WallThickness, topY, d - DoorWidth / 2), _wall);
            _scene.AddStructure(Box(x, floorY, d + DoorWidth / 2, x + WallThickness, topY, z1), _wall);
            _scene.AddStructure(Box(x, doorY + DoorHeight, d - DoorWidth / 2, x + WallThickness, topY,
                                    d + DoorWidth / 2), _wall);
        }
        else
        {
            _scene.AddStructure(Box(x, floorY, z0, x + WallThickness, topY, z1), _wall);
        }
    }

    /// <summary>A walled, roofed stair corridor between two chambers, in PLAN units.</summary>
    private void Corridor(float x0, float z0, float x1, float z1, float fromY, float toY, bool alongZ = false)
    {
        float wx0 = S(x0), wz0 = S(z0), wx1 = S(x1), wz1 = S(z1), wFrom = S(fromY), wTo = S(toY);
        var top = Mathf.Max(wFrom, wTo) + CorridorHeight;
        var bottom = Mathf.Min(wFrom, wTo);
        if (alongZ)
        {
            _scene.AddStairs(new Vector3((wx0 + wx1) / 2, wFrom, wz0), new Vector3((wx0 + wx1) / 2, wTo, wz1),
                             wx1 - wx0, _floor);
            _scene.AddStructure(Box(wx0 - WallThickness, bottom, wz0, wx0, top, wz1), _wall);
            _scene.AddStructure(Box(wx1, bottom, wz0, wx1 + WallThickness, top, wz1), _wall);
        }
        else
        {
            _scene.AddStairs(new Vector3(wx0, wFrom, (wz0 + wz1) / 2), new Vector3(wx1, wTo, (wz0 + wz1) / 2),
                             wz1 - wz0, _floor);
            _scene.AddStructure(Box(wx0, bottom, wz0 - WallThickness, wx1, top, wz0), _wall);
            _scene.AddStructure(Box(wx0, bottom, wz1, wx1, top, wz1 + WallThickness), _wall);
        }
        _scene.AddStructure(Box(wx0, top, wz0, wx1, top + WallThickness, wz1), _wall); // roofed all the way
        _passages.Add(new Passage(wx0, wz0, wx1, wz1, bottom, top, alongZ));
    }

    /// <summary>
    /// Rows of square pillars holding a chamber's roof up, over a PLAN-unit
    /// rectangle. Spacing is a WORLD constant, so a wider hall gets MORE
    /// pillars rather than the same few stretched further apart.
    /// </summary>
    private void PillarRows(float x0, float z0, float x1, float z1, float floorY,
                            float spacing = PillarSpacing)
    {
        float wx0 = S(x0), wz0 = S(z0), wx1 = S(x1), wz1 = S(z1), wFloor = S(floorY);
        var midX = (wx0 + wx1) * 0.5f;
        var midZ = (wz0 + wz1) * 0.5f;

        // Rows are laid in PAIRS either side of the chamber's middle, never on
        // it, so the hall keeps a clear cross aisle down both centre lines.
        // That is architecture — a pillared hall has aisles — and it is also
        // load-bearing for the build: the intended route runs the centre line,
        // and a pillar standing in it stalls the route walker outright.
        var reachX = (wx1 - wx0) * 0.5f - PillarHalf;
        var reachZ = (wz1 - wz0) * 0.5f - PillarHalf;
        for (var i = 0; (i + 0.5f) * spacing <= reachX; i++)
            for (var j = 0; (j + 0.5f) * spacing <= reachZ; j++)
                foreach (var sx in new[] { -1f, 1f })
                    foreach (var sz in new[] { -1f, 1f })
                    {
                        var x = midX + sx * (i + 0.5f) * spacing;
                        var z = midZ + sz * (j + 0.5f) * spacing;
                        _scene.AddStructure(Box(x - PillarHalf, wFloor, z - PillarHalf,
                                                x + PillarHalf, wFloor + RoomHeight, z + PillarHalf), _wall);
                    }
    }
}

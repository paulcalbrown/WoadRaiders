using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb;

namespace WoadRaiders.Client;

/// <summary>
/// The Sunken Crypt's DESIGN — a TRUE interior at last: rooms with flat stone
/// floors, standing walls, linteled doorways, and real roofs overhead, sinking
/// chamber by chamber into the earth. No landscape anywhere; the necropolis is
/// masonry all the way down.
///
///   undercroft (0) → descent stair → hall of the dead (−80) → processional
///   stair (→ −160) → the span over the chasm (bridge −160, chasm floor −300,
///   a long stair back out of it) → east landing → deep stair (→ −240) →
///   the catacombs → the low gallery (→ −260) → the Mausoleum (−260)
///
/// Dropping off the span into the chasm is a legal fall — the chasm stair
/// climbs back to the landing, so the fall is a detour, never a grave.
/// </summary>
public sealed partial class CryptDesign : IRealmDesign
{
    public string Name => "Crypt";

    private const float WallHeight = 150f;
    private const float DoorWidth = 170f;
    private const float DoorHeight = 100f;

    private static readonly Color OldStone = new(0.30f, 0.30f, 0.34f);
    private static readonly Color DarkStone = new(0.22f, 0.22f, 0.27f);
    private static readonly Color BoneWhite = new(0.55f, 0.53f, 0.47f);

    private RealmScene _scene = null!;
    private Material _floor = null!;
    private Material _wall = null!;
    private Material _bone = null!;

    public RealmScene Build()
    {
        _scene = new RealmScene();
        _floor = new StandardMaterial3D { AlbedoColor = OldStone, Roughness = 0.9f };
        _wall = new StandardMaterial3D { AlbedoColor = DarkStone, Roughness = 0.95f };
        _bone = new StandardMaterial3D { AlbedoColor = BoneWhite, Roughness = 0.8f };

        // ------------------------------------------------------- the chambers
        // The undercroft — the way in.
        Room(x0: 200, z0: 1500, x1: 900, z1: 2100, floorY: 0, doorEast: 1800);
        // The descent stair, walled like the corridor it is.
        Corridor(x0: 900, z0: 1700, x1: 1500, z1: 1900, fromY: 0, toY: -80);
        // The hall of the dead.
        Room(x0: 1500, z0: 1400, x1: 2500, z1: 2200, floorY: -80, doorWest: 1800, doorEast: 1800);
        PillarRows(x0: 1700, z0: 1600, x1: 2300, z1: 2000, floorY: -80, spacing: 300);
        // The processional stair down.
        Corridor(x0: 2500, z0: 1700, x1: 3100, z1: 1900, fromY: -80, toY: -160);
        // The span: a chasm chamber, its bridge, and the long stair out of the
        // pit — climbing the south air to land back ON the bridge itself.
        Room(x0: 3100, z0: 1200, x1: 3700, z1: 2400, floorY: -300, wallTopY: -60,
             doorWest: 1800, doorWestY: -160, doorEast: 1800, doorEastY: -160);
        _scene.AddFloor(Box(3100, -172, 1740, 3700, -160, 1860), _bone, "Span");
        _scene.AddStairs(new Vector3(3250, -300, 2300), new Vector3(3550, -160, 1850), 120, _floor);
        // The east landing beyond the span.
        Room(x0: 3700, z0: 1500, x1: 3900, z1: 2100, floorY: -160, doorWest: 1800, doorSouth: 3800);
        // The deep stair south.
        Corridor(x0: 3700, z0: 2100, x1: 3900, z1: 2700, fromY: -160, toY: -240, alongZ: true);
        // The catacombs — a pillared maze.
        Room(x0: 3200, z0: 2700, x1: 3900, z1: 3300, floorY: -240, doorNorth: 3800, doorWest: 3000);
        PillarRows(x0: 3350, z0: 2850, x1: 3750, z1: 3150, floorY: -240, spacing: 200);
        // The low gallery westward, easing down the last twenty.
        Corridor(x0: 2600, z0: 2900, x1: 3200, z1: 3100, fromY: -240, toY: -260);
        // The Mausoleum — the boss's tall chamber.
        Room(x0: 1800, z0: 2600, x1: 2600, z1: 3400, floorY: -260, wallTopY: -40, doorEast: 3000);
        _scene.AddFloor(Box(2050, -260, 2850, 2350, -244, 3150), _bone, "BossDais");

        // ------------------------------------------------------- the cast
        _scene.SetPlayerSpawn(_scene.OnFloor(350, 1800));
        Post(EnemyType.Minion, 700, 1650);
        Post(EnemyType.Minion, 700, 1950);
        Post(EnemyType.Minion, 1200, 1800);
        Post(EnemyType.Minion, 1780, 1520);
        Post(EnemyType.Minion, 1700, 2000);
        Post(EnemyType.Rogue, 2000, 1500);
        Post(EnemyType.Rogue, 2000, 2100);
        Post(EnemyType.Mage, 2220, 1520);
        Post(EnemyType.Mage, 2300, 2000);
        Post(EnemyType.Minion, 2800, 1800);
        Post(EnemyType.Rogue, 3400, 1780);     // on the span
        Post(EnemyType.Minion, 3400, 2340);    // down in the chasm
        Post(EnemyType.Rogue, 3800, 1650);
        Post(EnemyType.Rogue, 3800, 1950);
        Post(EnemyType.Minion, 3800, 2400);
        Post(EnemyType.Minion, 3350, 2800);
        Post(EnemyType.Minion, 3350, 3200);
        Post(EnemyType.Rogue, 3550, 3000);
        Post(EnemyType.Rogue, 3750, 2800);
        Post(EnemyType.Mage, 3850, 3200);
        Post(EnemyType.Minion, 2900, 3000);
        Post(EnemyType.Minion, 2450, 2750);
        Post(EnemyType.Minion, 2450, 3250);
        Post(EnemyType.Mage, 1950, 2750);
        Post(EnemyType.Mage, 1950, 3250);
        _scene.SetBossSpawn(_scene.OnFloor(2200, 3000));

        // ------------------------------------------------------- the dressing
        Relics(); // the imported kit pass (CryptDesign.Relics.cs) — pure scenery

        // ------------------------------------------------------- the look
        Gloom();
        return _scene;
    }

    // ----------------------------------------------------------- vocabulary

    private static Aabb Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        new(new System.Numerics.Vector3(x0, y0, z0), new System.Numerics.Vector3(x1, y1, z1));

    /// <summary>
    /// A rectangular chamber: a thick floor slab, four walls (each with an
    /// optional linteled doorway at a given coordinate), and a roof. Door Y
    /// defaults to the floor; a chasm chamber passes the height its doors
    /// open at instead.
    /// </summary>
    private void Room(float x0, float z0, float x1, float z1, float floorY, float? wallTopY = null,
                      float? doorNorth = null, float? doorSouth = null,
                      float? doorWest = null, float? doorEast = null,
                      float? doorWestY = null, float? doorEastY = null)
    {
        var top = wallTopY ?? floorY + WallHeight;
        _scene.AddFloor(Box(x0, floorY - 30, z0, x1, floorY, z1), _floor);
        // North/south walls run along X (z fixed); west/east along Z.
        WallX(x0, x1, z0, floorY, top, doorNorth, floorY);
        WallX(x0, x1, z1 - 24, floorY, top, doorSouth, floorY);
        WallZ(z0, z1, x0, floorY, top, doorWest, doorWestY ?? floorY);
        WallZ(z0, z1, x1 - 24, floorY, top, doorEast, doorEastY ?? floorY);
        _scene.AddStructure(Box(x0, top, z0, x1, top + 24, z1), _wall); // the roof
    }

    /// <summary>A wall along X at a fixed Z, split around a doorway when one is given.</summary>
    private void WallX(float x0, float x1, float z, float floorY, float topY, float? doorAt, float doorY)
    {
        if (doorAt is { } d)
        {
            _scene.AddStructure(Box(x0, floorY, z, d - DoorWidth / 2, topY, z + 24), _wall);
            _scene.AddStructure(Box(d + DoorWidth / 2, floorY, z, x1, topY, z + 24), _wall);
            _scene.AddStructure(Box(d - DoorWidth / 2, doorY + DoorHeight, z, d + DoorWidth / 2, topY, z + 24), _wall);
        }
        else
        {
            _scene.AddStructure(Box(x0, floorY, z, x1, topY, z + 24), _wall);
        }
    }

    /// <summary>A wall along Z at a fixed X, split around a doorway when one is given.</summary>
    private void WallZ(float z0, float z1, float x, float floorY, float topY, float? doorAt, float doorY)
    {
        if (doorAt is { } d)
        {
            _scene.AddStructure(Box(x, floorY, z0, x + 24, topY, d - DoorWidth / 2), _wall);
            _scene.AddStructure(Box(x, floorY, d + DoorWidth / 2, x + 24, topY, z1), _wall);
            _scene.AddStructure(Box(x, doorY + DoorHeight, d - DoorWidth / 2, x + 24, topY, d + DoorWidth / 2), _wall);
        }
        else
        {
            _scene.AddStructure(Box(x, floorY, z0, x + 24, topY, z1), _wall);
        }
    }

    /// <summary>A walled, roofed stair corridor between two chambers.</summary>
    private void Corridor(float x0, float z0, float x1, float z1, float fromY, float toY, bool alongZ = false)
    {
        var top = MathF.Max(fromY, toY) + WallHeight;
        var bottom = MathF.Min(fromY, toY);
        if (alongZ)
        {
            _scene.AddStairs(new Vector3((x0 + x1) / 2, fromY, z0), new Vector3((x0 + x1) / 2, toY, z1),
                             x1 - x0, _floor);
            _scene.AddStructure(Box(x0 - 24, bottom, z0, x0, top, z1), _wall);
            _scene.AddStructure(Box(x1, bottom, z0, x1 + 24, top, z1), _wall);
        }
        else
        {
            _scene.AddStairs(new Vector3(x0, fromY, (z0 + z1) / 2), new Vector3(x1, toY, (z0 + z1) / 2),
                             z1 - z0, _floor);
            _scene.AddStructure(Box(x0, bottom, z0 - 24, x1, top, z0), _wall);
            _scene.AddStructure(Box(x0, bottom, z1, x1, top, z1 + 24), _wall);
        }
        _scene.AddStructure(Box(x0, top, z0, x1, top + 24, z1), _wall); // roofed all the way
    }

    /// <summary>Rows of square pillars holding a chamber's roof up.</summary>
    private void PillarRows(float x0, float z0, float x1, float z1, float floorY, float spacing)
    {
        for (var x = x0; x <= x1; x += spacing)
            for (var z = z0; z <= z1; z += spacing)
                _scene.AddStructure(Box(x - 20, floorY, z - 20, x + 20, floorY + WallHeight, z + 20), _wall);
    }

    private void Post(EnemyType type, float x, float z) => _scene.AddEnemy(type, _scene.OnFloor(x, z));

    private void Gloom()
    {
        _scene.Add(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.01f, 0.01f, 0.02f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.35f, 0.38f, 0.5f),
                AmbientLightEnergy = 0.5f,
                FogEnabled = true,
                FogLightColor = new Color(0.05f, 0.07f, 0.12f),
                FogDensity = 0.0012f,
            },
        }, "Gloom");

        // Torchlight along the route — cold blue witchfire.
        var torches = _scene.Folder("Torches");
        foreach (var (x, z) in new[]
                 {
                     (550f, 1800f), (1200f, 1800f), (2000f, 1800f), (2800f, 1800f),
                     (3400f, 1800f), (3800f, 1800f), (3800f, 2400f), (3550f, 3000f),
                     (2900f, 3000f), (2200f, 3000f),
                 })
        {
            torches.AddChild(new OmniLight3D
            {
                Position = new Vector3(x, _scene.FloorAt(x, z) + 90f, z),
                LightColor = new Color(0.5f, 0.7f, 1.0f),
                LightEnergy = 2.2f,
                OmniRange = 420f,
            });
        }
    }
}

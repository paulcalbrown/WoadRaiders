using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb;

namespace WoadRaiders.Client;

/// <summary>
/// The Crag's DESIGN — a BUILT realm, not a carved one: a megalithic ascent of
/// broad stone terraces rising from the gate court to the boss's high court,
/// joined by wide stairs and a causeway over open air. Giants cut these
/// stones; nothing here pretends to be landscape.
///
///   gate court (0) → stairs → the processional (120) → stairs →
///   the high ward (240) → the causeway → the boss court (240, dais 256)
///
/// Drops off a terrace's open edges are legal shortcuts back down (the stairs
/// climb back); the void beyond the stones seals the realm — there is nothing
/// to walk on out there. Parapets guard only the edges above NOTHING, so no
/// leap strands a raider.
/// </summary>
public sealed partial class CragDesign : IRealmDesign
{
    public string Name => "Crag";

    // Terrace tops.
    private const float Court = 0f;
    private const float Processional = 120f;
    private const float HighWard = 240f;
    private const float Dais = 256f;

    private static readonly Color Granite = new(0.42f, 0.44f, 0.47f);
    private static readonly Color WornGranite = new(0.36f, 0.37f, 0.40f);
    private static readonly Color OldBronze = new(0.45f, 0.38f, 0.25f);

    public RealmScene Build()
    {
        var scene = new RealmScene();
        var floor = Slab(Granite);
        var worn = Slab(WornGranite);
        var bronze = Slab(OldBronze);

        // ------------------------------------------------------- the terraces
        // The gate court — where the raiders arrive.
        scene.AddFloor(Box(200, -60, 1400, 1200, Court, 2600), floor, "GateCourt");
        // The processional — the long middle terrace, cut down to bedrock.
        scene.AddFloor(Box(1500, -60, 1600, 2700, Processional, 2400), floor, "Processional");
        // The high ward — the upper field before the causeway.
        scene.AddFloor(Box(3000, -60, 1200, 3800, HighWard, 2800), floor, "HighWard");
        // The causeway — a THIN bridge of stone over open air, on purpose.
        scene.AddFloor(Box(3250, 216, 2800, 3550, HighWard, 3200), worn, "Causeway");
        // The boss court, and the dais he stands on.
        scene.AddFloor(Box(2900, -60, 3200, 3900, HighWard, 4000), floor, "BossCourt");
        scene.AddFloor(Box(3250, HighWard, 3500, 3550, Dais, 3800), worn, "Dais");

        // ------------------------------------------------------- the stairs
        scene.AddStairs(new Vector3(1200, Court, 2000), new Vector3(1500, Processional, 2000), 320, worn);
        scene.AddStairs(new Vector3(2700, Processional, 2000), new Vector3(3000, HighWard, 2000), 320, worn);

        // ------------------------------------------------------- the walls
        // Parapets along every edge with NOTHING below — a fall there would be
        // into the void, so the stones forbid it. Terrace edges above lower
        // terraces stay open: leaping down is a raider's right.
        scene.AddStructure(Box(3250, HighWard, 2800, 3262, HighWard + 44, 3200), worn, "CausewayRailW");
        scene.AddStructure(Box(3538, HighWard, 2800, 3550, HighWard + 44, 3200), worn, "CausewayRailE");
        Perimeter(scene, worn);

        // ------------------------------------------------------- the stones
        // Standing stones — rings and sentinels; pure structure, set on the
        // flanks so the processional's spine stays an open walk.
        StoneRing(scene, bronze, 700, 1700, 180, 6);
        StoneRing(scene, bronze, 2300, 2220, 100, 5);
        Sentinel(scene, bronze, 3060, 1300);
        Sentinel(scene, bronze, 3740, 1300);
        Sentinel(scene, bronze, 3060, 2700);
        Sentinel(scene, bronze, 3740, 2700);
        Sentinel(scene, bronze, 2960, 3260);
        Sentinel(scene, bronze, 3840, 3260);

        // ------------------------------------------------------- the cast
        scene.SetPlayerSpawn(scene.OnFloor(400, 2000));
        Camp(scene, EnemyType.Minion, 900, 1800, 900, 2200);
        Camp(scene, EnemyType.Minion, 1700, 1750, 1700, 2250);
        Camp(scene, EnemyType.Rogue, 2100, 1700, 2100, 2300);
        Camp(scene, EnemyType.Minion, 2500, 1800, 2500, 2200);
        scene.AddEnemy(EnemyType.Mage, scene.OnFloor(2300, 2000));
        Camp(scene, EnemyType.Minion, 3200, 1500, 3200, 2500);
        Camp(scene, EnemyType.Rogue, 3500, 1400, 3500, 2600);
        scene.AddEnemy(EnemyType.Mage, scene.OnFloor(3650, 1800));
        scene.AddEnemy(EnemyType.Mage, scene.OnFloor(3650, 2200));
        Camp(scene, EnemyType.Rogue, 3300, 3350, 3500, 3350);
        Camp(scene, EnemyType.Minion, 3050, 3700, 3750, 3700);
        scene.AddEnemy(EnemyType.Mage, scene.OnFloor(3400, 3900));
        scene.SetBossSpawn(scene.OnFloor(3400, 3650));

        // ------------------------------------------------------- the dressing
        Scenery(scene); // the imported kit pass (CragDesign.Scenery.cs) — pure scenery

        // ------------------------------------------------------- the look
        Skies(scene);
        return scene;
    }

    // ----------------------------------------------------------- vocabulary

    private static Aabb Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        new(new System.Numerics.Vector3(x0, y0, z0), new System.Numerics.Vector3(x1, y1, z1));

    private static StandardMaterial3D Slab(Color colour) => new()
    {
        AlbedoColor = colour,
        Roughness = 0.95f,
    };

    /// <summary>A terrace-edge parapet wherever the drop would land on nothing.</summary>
    private static void Perimeter(RealmScene scene, Material material)
    {
        // The gate court's outer rim.
        scene.AddStructure(Box(200, Court, 1400, 1200, Court + 44, 1412), material, "RimGateN");
        scene.AddStructure(Box(200, Court, 2588, 1200, Court + 44, 2600), material, "RimGateS");
        scene.AddStructure(Box(200, Court, 1400, 212, Court + 44, 2600), material, "RimGateW");
        // The processional's flanks.
        scene.AddStructure(Box(1500, Processional, 1600, 2700, Processional + 44, 1612), material, "RimProcN");
        scene.AddStructure(Box(1500, Processional, 2388, 2700, Processional + 44, 2400), material, "RimProcS");
        // The high ward's outer flanks.
        scene.AddStructure(Box(3000, HighWard, 1200, 3800, HighWard + 44, 1212), material, "RimWardN");
        scene.AddStructure(Box(3000, HighWard, 2788, 3238, HighWard + 44, 2800), material, "RimWardS1");
        scene.AddStructure(Box(3562, HighWard, 2788, 3800, HighWard + 44, 2800), material, "RimWardS2");
        scene.AddStructure(Box(3788, HighWard, 1200, 3800, HighWard + 44, 2800), material, "RimWardE");
        // The boss court's rim, broken only where the causeway enters.
        scene.AddStructure(Box(2900, HighWard, 3200, 3238, HighWard + 44, 3212), material, "RimCourtN1");
        scene.AddStructure(Box(3562, HighWard, 3200, 3900, HighWard + 44, 3212), material, "RimCourtN2");
        scene.AddStructure(Box(2900, HighWard, 3988, 3900, HighWard + 44, 4000), material, "RimCourtS");
        scene.AddStructure(Box(2900, HighWard, 3200, 2912, HighWard + 44, 4000), material, "RimCourtW");
        scene.AddStructure(Box(3888, HighWard, 3200, 3900, HighWard + 44, 4000), material, "RimCourtE");
    }

    /// <summary>A ring of standing stones around a point on the floor.</summary>
    private static void StoneRing(RealmScene scene, Material material, float cx, float cz, float radius, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = Mathf.Tau * i / count;
            var x = cx + Mathf.Cos(angle) * radius;
            var z = cz + Mathf.Sin(angle) * radius;
            var y = scene.FloorAt(x, z);
            scene.AddStructure(Box(x - 14, y, z - 10, x + 14, y + 110 + 14 * (i % 3), z + 10), material);
        }
    }

    /// <summary>One tall sentinel stone.</summary>
    private static void Sentinel(RealmScene scene, Material material, float x, float z)
    {
        var y = scene.FloorAt(x, z);
        scene.AddStructure(Box(x - 18, y, z - 14, x + 18, y + 170, z + 14), material);
    }

    /// <summary>A small camp: two of a kind flanking a stretch of floor.</summary>
    private static void Camp(RealmScene scene, EnemyType type, float x0, float z0, float x1, float z1)
    {
        scene.AddEnemy(type, scene.OnFloor(x0, z0));
        scene.AddEnemy(type, scene.OnFloor(x1, z1));
    }

    private static void Skies(RealmScene scene)
    {
        scene.Add(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky
                {
                    SkyMaterial = new ProceduralSkyMaterial
                    {
                        SkyTopColor = new Color(0.25f, 0.32f, 0.45f),
                        SkyHorizonColor = new Color(0.55f, 0.52f, 0.50f),
                        GroundBottomColor = new Color(0.12f, 0.13f, 0.16f),
                        GroundHorizonColor = new Color(0.45f, 0.43f, 0.42f),
                    },
                },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 0.8f,
                FogEnabled = true,
                FogLightColor = new Color(0.55f, 0.58f, 0.65f),
                FogDensity = 0.0002f,
            },
        }, "Sky");
        var sun = new DirectionalLight3D
        {
            LightColor = new Color(1.0f, 0.95f, 0.85f),
            LightEnergy = 1.1f,
            ShadowEnabled = true,
        };
        sun.RotationDegrees = new Vector3(-40, -60, 0);
        scene.Add(sun, "Sun");
    }
}

using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's imported dressing — the pass that places the CC0 asset-kit
/// pieces (KayKit Dungeon Remastered and Halloween Bits, Kenney Graveyard Kit,
/// and a few Poly Pizza singles; see assets/crypt/*/LICENSE and
/// ATTRIBUTION.md). Each piece is instanced from its .glb/.gltf, so the saved
/// scene carries instance= references, never inlined geometry —
/// RealmSceneBuilder owns only the instance roots when packing.
///
/// The kits are authored at different scales (KayKit props run stylized-large,
/// Kenney's run miniature), so every placement states its own scale; this
/// game's characters stand ~44 units (~25 units to the metre).
/// </summary>
public sealed partial class CryptDesign
{
    /// <summary>One kit piece, loaded once and instanced everywhere. Paths are
    /// project-relative: the dungeon pieces come from the committed KayKit
    /// Dungeon Remastered addon; the crypt-specific packs live under
    /// assets/crypt/.</summary>
    private readonly record struct Kit(PackedScene Scene)
    {
        public static Kit Load(string file) =>
            new(GD.Load<PackedScene>($"res://{file}"));

        public Node3D At(Vector3 position, float scale, float yaw = 0f)
        {
            var node = Scene.Instantiate<Node3D>();
            node.Position = position;
            node.Rotation = new Vector3(0f, yaw, 0f);
            node.Scale = new Vector3(scale, scale, scale);
            return node;
        }
    }

    /// <summary>Dress the Crypt with the kits: the interred in their niches,
    /// the ossuary's stacked dead, coffins and urns and grave markers, the
    /// candle chapel's furniture, banners under the vault ribs, and iron
    /// rails at the chasm. Pure scenery — the bake walks straight past it.</summary>
    private static void DressWithRelics(RealmScene scene)
    {
        var relics = scene.Folder("Relics");
        void Place(Kit kit, float x, float z, float scale, float yaw = 0f, float lift = 0f)
        {
            var node = kit.At(scene.OnGround(x, z) + new Vector3(0f, lift, 0f), scale, yaw);
            node.Name = $"Relic{relics.GetChildCount()}";
            relics.AddChild(node);
        }

        // ---- the kits ----
        var skull = Kit.Load("assets/crypt/kaykit_halloween/skull.gltf");
        var skullCandle = Kit.Load("assets/crypt/kaykit_halloween/skull_candle.gltf");
        var smallSkull = Kit.Load("assets/crypt/polypizza/skull_quaternius.glb");
        var boneA = Kit.Load("assets/crypt/kaykit_halloween/bone_A.gltf");
        var boneB = Kit.Load("assets/crypt/kaykit_halloween/bone_B.gltf");
        var boneC = Kit.Load("assets/crypt/kaykit_halloween/bone_C.gltf");
        var ribcage = Kit.Load("assets/crypt/kaykit_halloween/ribcage.gltf");
        var greatBone = Kit.Load("assets/crypt/polypizza/bone_large_quaternius.glb");
        var standingBones = Kit.Load("assets/crypt/polypizza/skeleton_quaternius.glb");
        var coffin = Kit.Load("assets/crypt/kaykit_halloween/coffin.gltf");
        var coffinCarved = Kit.Load("assets/crypt/kaykit_halloween/coffin_decorated.gltf");
        var coffinOld = Kit.Load("assets/crypt/kenney_graveyard/coffin-old.glb");
        var gravestone = Kit.Load("assets/crypt/kaykit_halloween/gravestone.gltf");
        var graveBroken = Kit.Load("assets/crypt/kenney_graveyard/gravestone-broken.glb");
        var markerA = Kit.Load("assets/crypt/kaykit_halloween/gravemarker_A.gltf");
        var markerB = Kit.Load("assets/crypt/kaykit_halloween/gravemarker_B.gltf");
        var postSkull = Kit.Load("assets/crypt/kaykit_halloween/post_skull.gltf");
        var shrine = Kit.Load("assets/crypt/kaykit_halloween/shrine.gltf");
        var shrineCandles = Kit.Load("assets/crypt/kaykit_halloween/shrine_candles.gltf");
        var plaqueCandles = Kit.Load("assets/crypt/kaykit_halloween/plaque_candles.gltf");
        var lanternStanding = Kit.Load("assets/crypt/kaykit_halloween/lantern_standing.gltf");
        var tombFront = Kit.Load("assets/crypt/kaykit_halloween/crypt.gltf");
        var altar = Kit.Load("assets/crypt/kenney_graveyard/altar-stone.glb");
        var urnRound = Kit.Load("assets/crypt/kenney_graveyard/urn-round.glb");
        var urnSquare = Kit.Load("assets/crypt/kenney_graveyard/urn-square.glb");
        var ironFence = Kit.Load("assets/crypt/kenney_graveyard/iron-fence.glb");
        var ironFenceBent = Kit.Load("assets/crypt/kenney_graveyard/iron-fence-damaged.glb");
        var debris = Kit.Load("assets/crypt/kenney_graveyard/debris.glb");
        var torchMounted = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/torch_mounted.gltf.glb");
        var pillarCarved = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/pillar_decorated.gltf.glb");
        var bannerWide = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/banner_patternA_green.gltf.glb");
        var bannerThin = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/banner_thin_green.gltf.glb");
        var bannerTriple = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/banner_triple_green.gltf.glb");
        var chestGold = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/chest_gold.glb");
        var coinsLarge = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/coin_stack_large.gltf.glb");
        var coinsMedium = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/coin_stack_medium.gltf.glb");
        var candleShelf = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/shelf_small_candles.gltf.glb");
        var rubbleLarge = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/rubble_large.gltf.glb");
        var rubbleHalf = Kit.Load("addons/kaykit_dungeon_remastered/Assets/gltf/rubble_half.gltf.glb");

        // ---- the entrance undercroft: the founder's sealed tomb, the first
        // graves, and the rubble of the collapse under the light shaft.
        Place(tombFront, 540, 1618, 12f, Mathf.Pi);   // door south, against the north wall
        Place(rubbleLarge, 480, 1745, 16f, 0.7f);
        Place(rubbleHalf, 585, 1860, 14f, 2.3f);
        Place(gravestone, 330, 1700, 20f, 0.25f);
        Place(markerA, 372, 1662, 22f, -0.2f);
        Place(markerB, 350, 1905, 22f, 0.4f);
        Place(markerA, 420, 1950, 22f, 2.9f);
        Place(debris, 545, 1755, 45f, 1.1f);
        Place(debris, 462, 1845, 45f, 2.6f);

        // ---- doorway torches: an unlit iron sconce on every portal jamb —
        // the frames read as tended thresholds (the light itself comes from
        // the standing torches of the Lights pass).
        foreach (var (x, z, alongX, width) in Doorways)
        {
            var half = width / 2f;
            foreach (var side in new[] { -1f, 1f })
            {
                var (jx, jz) = alongX ? (x, z + side * half) : (x + side * half, z);
                var toCentre = Mathf.Atan2(x - jx, z - jz);
                var node = torchMounted.At(
                    scene.OnGround(jx, jz) + new Vector3(0f, 62f, 0f), 22f, toCentre);
                node.Name = $"Relic{relics.GetChildCount()}";
                relics.AddChild(node);
            }
        }

        // ---- the burial niches hold their dead: a skull, a long bone, or a
        // ribcage in each recess, chosen and turned by hash. Mirrors the
        // NicheRow table in the Masonry pass.
        void FillNiches(float x0, float z0, float x1, float z1, float spacing, int salt)
        {
            var a = new Vector2(x0, z0);
            var b = new Vector2(x1, z1);
            var dir = (b - a).Normalized();
            var run = a.DistanceTo(b);
            var k = 0;
            for (var d = 0f; d <= run; d += spacing, k++)
            {
                var p = a + dir * d;
                var yaw = (Hash(k, salt, 883) - 0.5f) * 1.2f;
                switch ((int)(Hash(k, salt, 887) * 3f))
                {
                    case 0: Place(skull, p.X, p.Y, 8f, yaw, lift: 8f); break;
                    case 1: Place(ribcage, p.X, p.Y, 11f, yaw + Mathf.Pi / 2f, lift: 12f); break;
                    default: Place(boneC, p.X, p.Y, 13f, yaw, lift: 9f); break;
                }
            }
        }
        FillNiches(1372, 1520, 1372, 1020, 96f, 61);
        FillNiches(1528, 1520, 1528, 1020, 96f, 62);
        FillNiches(1372, 2080, 1372, 2580, 96f, 63);
        FillNiches(1528, 2080, 1528, 2580, 96f, 64);
        FillNiches(2960, 1520, 3640, 1520, 128f, 65);
        FillNiches(2960, 2080, 3640, 2080, 128f, 66);
        FillNiches(2830, 3005, 3170, 3005, 110f, 67);

        // ---- the Ossuary: the stacked centuries. A bone wall along the far
        // arc, a ring of remains around the altar, skull-post wardens at the
        // door, and one assembled skeleton standing its eternal vigil.
        for (var k = 0; k < 8; k++)
        {
            var ang = Mathf.Pi + (k - 3.5f) * 0.24f; // the far (north) arc
            var bx = 1450 + Mathf.Cos(ang) * 195f;
            var bz = 680 + Mathf.Sin(ang) * 195f;
            Place(ribcage, bx, bz, 15f, ang + Mathf.Pi / 2f, lift: 12f);
            Place(skull, bx + 10f, bz + 14f, 9f, ang * 3f, lift: 2f);
        }
        foreach (var (ox, oz, oy) in new[] { (1385f, 745f, 0.9f), (1516f, 748f, 2.2f), (1395f, 612f, 4.1f), (1510f, 615f, 5.3f) })
        {
            Place(boneA, ox, oz, 16f, oy);
            Place(skullCandle, ox + 12f, oz - 10f, 9f, oy + 1f);
        }
        Place(postSkull, 1382, 900, 20f, 0.2f);
        Place(postSkull, 1518, 900, 20f, -0.2f);
        Place(shrineCandles, 1450, 585, 22f);
        Place(standingBones, 1450, 620, 10f, Mathf.Pi); // facing the door, waiting

        // ---- the charnel pit: the nameless dead, and something far older.
        Place(greatBone, 2370, 1790, 7f, 0.9f, lift: -4f);
        Place(standingBones, 2400, 2020, 9.5f, -1.1f);
        foreach (var (bx, bz, by) in new[] { (2330f, 1520f, 0.5f), (2420f, 1700f, 2.8f), (2350f, 1930f, 4.4f), (2395f, 2140f, 1.6f) })
        {
            Place(boneB, bx, bz, 18f, by);
            Place(smallSkull, bx + 16f, bz + 8f, 18f, by * 2f);
        }
        Place(graveBroken, 2320, 1450, 40f, 0.5f);
        Place(graveBroken, 2430, 2160, 40f, 2.2f);

        // ---- iron rails at the chasm rims, bent where the collapse took them.
        foreach (var (fz, bent) in new[] { (1480f, false), (1560f, false), (2040f, true), (2120f, false) })
            Place(bent ? ironFenceBent : ironFence, 2242, fz, 40f, Mathf.Pi / 2f);
        foreach (var (fz, bent) in new[] { (1500f, true), (2080f, false) })
            Place(bent ? ironFenceBent : ironFence, 2520, fz, 40f, Mathf.Pi / 2f);

        // ---- the Hall of the Dead: urns of the ambulatory, a candle shelf
        // by the east door, an open coffin someone disturbed.
        foreach (var (ux, uz, round) in new[] { (1290f, 1585f, true), (1610f, 1585f, false), (1258f, 1990f, false), (1640f, 2005f, true) })
            Place(round ? urnRound : urnSquare, ux, uz, 38f, ux + uz);
        Place(candleShelf, 1637, 1745, 22f, -Mathf.Pi / 2f);
        Place(coffin, 1340, 1858, 18f, 0.35f);

        // ---- the galleries: a disturbed coffin and bone-crumbs on the walks.
        Place(coffin, 1408, 1265, 18f, 1.75f);
        Place(coffinOld, 1495, 2350, 45f, -0.3f);
        foreach (var (sx, sz, sy) in new[]
                 { (1465f, 1120f, 1.1f), (1435f, 2210f, 2.6f), (2060f, 1815f, 0.4f), (2740f, 1755f, 3.4f),
                   (3120f, 1785f, 1.9f), (3395f, 2145f, 5.1f), (3672f, 1935f, 2.2f), (1300f, 2895f, 0.8f) })
            Place(smallSkull, sx, sz, 18f, sy);

        // ---- the flooded cloister: lanterns at the water, an old coffin
        // dragged from its shelf.
        Place(lanternStanding, 1318, 2790, 24f, 0.4f);
        Place(lanternStanding, 1585, 3052, 24f, 2.1f);
        Place(coffinOld, 1225, 3030, 45f, 1.2f);

        // ---- the noble crypt: the family's carved coffins and their hoard.
        Place(coffinCarved, 862, 3062, 19f, Mathf.Pi / 2f);
        Place(coffinCarved, 862, 3178, 19f, Mathf.Pi / 2f);
        Place(chestGold, 800, 3120, 22f, 0.5f);
        Place(coinsLarge, 838, 3098, 22f, 1.2f);
        Place(coinsMedium, 815, 3155, 22f, 2.8f);
        Place(urnSquare, 950, 3055, 38f, 0.9f);
        Place(lanternStanding, 1088, 3020, 24f, -0.5f);

        // ---- the Processional: banners under the vault ribs.
        foreach (var rx in new[] { 1860f, 2040f, 2220f, 2560f })
        {
            var ground = scene.GroundAt(rx, 1800);
            var banner = bannerWide.At(new Vector3(rx, ground + 36f, 1690f), 22f, 0f);
            banner.Name = $"Relic{relics.GetChildCount()}";
            relics.AddChild(banner);
        }

        // ---- the candle chapel: the keepers' furniture.
        Place(altar, 3360, 940, 42f);
        Place(shrine, 3295, 955, 22f, 0.3f);
        Place(plaqueCandles, 3430, 975, 16f, -0.4f);
        Place(pillarCarved, 3255, 1050, 25f, 0f);
        Place(pillarCarved, 3465, 1050, 25f, Mathf.Pi);
        Place(urnRound, 3288, 1128, 38f, 1.7f);

        // ---- the maze: a dead adventurer's spill in a far corner.
        Place(chestGold, 3705, 2225, 20f, 2.4f);
        Place(coinsMedium, 3680, 2200, 20f, 0.8f);
        Place(boneA, 3690, 2245, 14f, 1.5f);

        // ---- the deep stair and antechamber: coffins of the kings' retinue.
        Place(coffinCarved, 2955, 2975, 19f, 0f);
        Place(coffinCarved, 3048, 3148, 19f, Mathf.Pi);
        Place(urnSquare, 2845, 3105, 38f, 0.6f);
        Place(urnRound, 3152, 3005, 38f, 2.9f);

        // ---- the Mausoleum: triple banners at the gate, thin banners on the
        // ring piers, and the honored dead's markers at the walls.
        var gateGround = scene.GroundAt(2600, 3095);
        var gateBanner = bannerTriple.At(new Vector3(2600f, gateGround + 30f, 3118f), 20f, Mathf.Pi);
        gateBanner.Name = $"Relic{relics.GetChildCount()}";
        relics.AddChild(gateBanner);
        for (var k = 0; k < 6; k++)
        {
            var ang = k * Mathf.Tau / 6f + 0.26f;
            var bx = 2320 + Mathf.Cos(ang) * 252f;
            var bz = 3260 + Mathf.Sin(ang) * 252f;
            var ground = scene.GroundAt(bx, bz);
            var banner = bannerThin.At(new Vector3(bx, ground + 34f, bz), 20f, ang + Mathf.Pi / 2f);
            banner.Name = $"Relic{relics.GetChildCount()}";
            relics.AddChild(banner);
        }
        Place(gravestone, 2135, 3435, 20f, 0.7f);
        Place(gravestone, 2510, 3430, 20f, -0.5f);
        Place(ribcage, 2245, 3120, 13f, 1.9f, lift: 10f);
        Place(skull, 2415, 3395, 8f, 3.6f, lift: 2f);
    }
}

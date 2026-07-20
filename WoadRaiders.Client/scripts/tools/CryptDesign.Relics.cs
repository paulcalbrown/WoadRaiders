using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's imported dressing — the pass that places the CC0 asset-kit
/// pieces (KayKit Halloween Bits, Kenney Graveyard Kit, and a few Poly Pizza
/// singles; see assets/crypt/*/LICENSE and ATTRIBUTION.md) through the slab
/// chambers. Each piece is instanced from its .glb/.gltf, so the saved scene
/// carries instance= references, never inlined geometry — RealmSceneBuilder
/// owns only the instance roots when packing.
///
/// The kits are authored at different scales (KayKit props run stylized-large,
/// Kenney's run miniature), so every placement states its own scale; this
/// game's characters stand ~44 units (~25 units to the metre). Pure scenery —
/// only "ground" and "structure" slabs reach the bake, so nothing placed here
/// can change what the server hosts.
/// </summary>
public sealed partial class CryptDesign
{
    /// <summary>One kit piece, loaded once and instanced everywhere.</summary>
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

    /// <summary>Deterministic noise for the scatters — same numbers, same
    /// dead, every run (no framework RNG).</summary>
    private static float Hash(int x, int z, int salt)
    {
        var h = unchecked((uint)(x * 374761393 + z * 668265263 + salt * 974711 + 144665));
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000; // 0..1
    }

    /// <summary>Dress the chambers: the founder's tomb in the undercroft,
    /// coffins and bone piles in the hall's wall niches, iron rails and the
    /// fallen dead at the span, grave markers filing the catacomb lanes,
    /// candles and shrine furniture in the Mausoleum, and lanterns hung from
    /// every corridor roof. The bake walks straight past all of it.</summary>
    private void Relics()
    {
        var relics = _scene.Folder("Relics");

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
        var graveCross = Kit.Load("assets/crypt/kenney_graveyard/gravestone-cross.glb");
        var graveRoof = Kit.Load("assets/crypt/kenney_graveyard/gravestone-roof.glb");
        var graveWide = Kit.Load("assets/crypt/kenney_graveyard/gravestone-wide.glb");
        var graveRuin = Kit.Load("assets/crypt/kaykit_halloween/grave_A_destroyed.gltf");
        var markerA = Kit.Load("assets/crypt/kaykit_halloween/gravemarker_A.gltf");
        var markerB = Kit.Load("assets/crypt/kaykit_halloween/gravemarker_B.gltf");
        var postSkull = Kit.Load("assets/crypt/kaykit_halloween/post_skull.gltf");
        var shrine = Kit.Load("assets/crypt/kaykit_halloween/shrine.gltf");
        var shrineCandles = Kit.Load("assets/crypt/kaykit_halloween/shrine_candles.gltf");
        var plaqueCandles = Kit.Load("assets/crypt/kaykit_halloween/plaque_candles.gltf");
        var lanternStanding = Kit.Load("assets/crypt/kaykit_halloween/lantern_standing.gltf");
        var lanternHanging = Kit.Load("assets/crypt/kaykit_halloween/lantern_hanging.gltf");
        var tombFront = Kit.Load("assets/crypt/kaykit_halloween/crypt.gltf");
        var altar = Kit.Load("assets/crypt/kenney_graveyard/altar-stone.glb");
        var obelisk = Kit.Load("assets/crypt/kenney_graveyard/pillar-obelisk.glb");
        var candleCluster = Kit.Load("assets/crypt/kenney_graveyard/candle-multiple.glb");
        var fireBasket = Kit.Load("assets/crypt/kenney_graveyard/fire-basket.glb");
        var urnRound = Kit.Load("assets/crypt/kenney_graveyard/urn-round.glb");
        var urnSquare = Kit.Load("assets/crypt/kenney_graveyard/urn-square.glb");
        var ironFence = Kit.Load("assets/crypt/kenney_graveyard/iron-fence.glb");
        var ironFenceBent = Kit.Load("assets/crypt/kenney_graveyard/iron-fence-damaged.glb");
        var debris = Kit.Load("assets/crypt/kenney_graveyard/debris.glb");

        // ---- the verbs ----
        void Add(Node3D node)
        {
            node.Name = $"Relic{relics.GetChildCount()}";
            relics.AddChild(node);
        }

        // Seat a piece on whatever floor lies under (x, z).
        void Place(Kit kit, float x, float z, float scale, float yaw = 0f, float lift = 0f) =>
            Add(kit.At(_scene.OnFloor(x, z) + new Vector3(0f, lift, 0f), scale, yaw));

        // Hang a piece from a roof: the hanging lantern's origin is its hook
        // (its mesh hangs ~1.3 native units BELOW the origin), so it mounts
        // at the ceiling and swings under it.
        void Hang(Kit kit, float x, float z, float ceiling, float scale = 24f) =>
            Add(kit.At(new Vector3(x, ceiling - 2f, z), scale));

        // A little heap of the dead — two or three remains, chosen and turned
        // by hash so every pile reads differently.
        void BonePile(float cx, float cz, int salt)
        {
            for (var k = 0; k < 3; k++)
            {
                var x = cx + (Hash(k, salt, 883) - 0.5f) * 44f;
                var z = cz + (Hash(k, salt, 887) - 0.5f) * 44f;
                var yaw = Hash(k, salt, 907) * Mathf.Tau;
                switch ((int)(Hash(k, salt, 911) * 4f))
                {
                    case 0: Place(skull, x, z, 8f, yaw, lift: 1f); break;
                    case 1: Place(ribcage, x, z, 12f, yaw, lift: 10f); break;
                    case 2: Place(boneB, x, z, 16f, yaw, lift: 2f); break;
                    default: Place(boneC, x, z, 13f, yaw, lift: 2f); break;
                }
            }
        }

        // Chamber ceilings, per the Masonry pass: a room's roof sits at
        // floor + WallHeight; a corridor's at its HIGHER end + WallHeight.
        const float UndercroftCeil = 0f + WallHeight;       // and the descent stair's
        const float HallCeil = -80f + WallHeight;           // and the processional's
        const float DeepStairCeil = -160f + WallHeight;
        const float GalleryCeil = -240f + WallHeight;

        // ---- the undercroft: the founder's sealed tomb against the north
        // wall, the first graves along the west, and a coffin someone already
        // dragged out into the light.
        Place(tombFront, 550, 1585, 12f, Mathf.Pi); // door facing south, into the room
        Place(markerA, 255, 1620, 22f, Mathf.Pi / 2 - 0.15f);
        Place(markerB, 252, 1745, 22f, Mathf.Pi / 2 + 0.2f);
        Place(gravestone, 250, 1955, 20f, Mathf.Pi / 2 - 0.1f);
        Place(markerA, 300, 2015, 22f, Mathf.Pi / 2 + 0.35f);
        Place(coffinOld, 770, 1590, 45f, 0.35f);
        Place(debris, 690, 2010, 45f, 1.2f);
        BonePile(645, 1565, 11);
        Place(skull, 795, 2040, 8f, 2.1f, lift: 1f);
        Place(lanternStanding, 835, 1690, 24f);
        Place(lanternStanding, 835, 1910, 24f);
        Hang(lanternHanging, 600, 1800, UndercroftCeil);

        // ---- the descent stair: lanterns from the corridor roof.
        Hang(lanternHanging, 1080, 1800, UndercroftCeil);
        Hang(lanternHanging, 1320, 1800, UndercroftCeil);

        // ---- the hall of the dead: the wall bays between the pillar lines
        // are the burial niches — a coffin and its spilled occupants in each,
        // urns at the west door, skull-post wardens at the east.
        Place(coffin, 1600, 1490, 18f, Mathf.Pi / 2 + 0.06f);
        BonePile(1662, 1470, 21);
        Place(coffinCarved, 1850, 1490, 19f, Mathf.Pi / 2 - 0.04f);
        Place(coffinOld, 2150, 1492, 45f, Mathf.Pi / 2 + 0.1f);
        BonePile(2210, 1468, 22);
        Place(coffin, 2400, 1490, 18f, Mathf.Pi / 2 - 0.1f);
        Place(coffinCarved, 1600, 2110, 19f, Mathf.Pi / 2 + 0.08f);
        BonePile(1665, 2132, 23);
        Place(coffin, 1850, 2110, 18f, Mathf.Pi / 2 - 0.05f);
        Place(coffin, 2150, 2112, 18f, Mathf.Pi / 2 + 0.12f);
        BonePile(2215, 2136, 24);
        Place(coffinCarved, 2400, 2110, 19f, Mathf.Pi / 2);
        Place(urnRound, 1545, 1692, 38f, 0.4f);
        Place(urnSquare, 1545, 1908, 38f, 1.7f);
        Place(postSkull, 2428, 1700, 20f, -0.2f);
        Place(postSkull, 2428, 1900, 20f, 0.2f);
        Place(skullCandle, 1730, 1630, 9f, 0.8f);   // at the pillar bases
        Place(boneA, 2032, 1938, 16f, 1.2f, lift: 2f);
        Place(skullCandle, 2268, 1868, 9f, 2.4f);
        Hang(lanternHanging, 1800, 1800, HallCeil);
        Hang(lanternHanging, 2200, 1800, HallCeil);

        // ---- the processional stair.
        Hang(lanternHanging, 2650, 1800, HallCeil);
        Hang(lanternHanging, 2950, 1800, HallCeil);

        // ---- the span: iron rails along the bridge (one length torn away —
        // the gap tells the story), witchfire baskets at its ends, lanterns
        // from the chasm roof, and below, everyone the bridge has ever
        // dropped. Chasm placements stay OUT of the bridge's z-band
        // (1740..1860): OnFloor seats on the HIGHEST floor, which there is
        // the bridge deck, not the pit.
        foreach (var fx in new[] { 3220f, 3310f, 3400f, 3490f, 3580f })
            Place(fx == 3400f ? ironFenceBent : ironFence, fx, 1748, 40f);
        foreach (var fx in new[] { 3220f, 3310f, 3490f, 3580f })
            Place(fx == 3310f ? ironFenceBent : ironFence, fx, 1852, 40f);
        Place(fireBasket, 3140, 1775, 42f);
        Place(fireBasket, 3140, 1825, 42f);
        Place(fireBasket, 3660, 1775, 42f);
        Place(fireBasket, 3660, 1825, 42f);
        Hang(lanternHanging, 3300, 1800, -60f);
        Hang(lanternHanging, 3500, 1800, -60f);
        // The north pit: the nameless dead, and something far older.
        Place(greatBone, 3300, 1480, 7f, 0.9f);
        Place(standingBones, 3480, 1350, 10f, 2.6f); // still on its feet, down there
        Place(graveBroken, 3190, 1560, 40f, 0.5f);
        Place(debris, 3560, 1620, 45f, 2.6f);
        Place(debris, 3260, 1300, 45f, 1.1f);
        for (var k = 0; k < 10; k++)
        {
            var x = 3180f + Hash(k, 31, 821) * 440f;
            var z = 1260f + Hash(k, 31, 823) * 400f;
            var yaw = Hash(k, 31, 827) * Mathf.Tau;
            switch ((int)(Hash(k, 31, 829) * 4f))
            {
                case 0: Place(ribcage, x, z, 13f, yaw, lift: 10f); break;
                case 1: Place(smallSkull, x, z, 18f, yaw); break;
                case 2: Place(boneA, x, z, 16f, yaw, lift: 2f); break;
                default: Place(skull, x, z, 8f, yaw, lift: 1f); break;
            }
        }
        Place(skullCandle, 3160, 1700, 9f, 1.9f); // someone's candle, still lit
        // The south pit, west of the long stair out.
        Place(coffinOld, 3165, 2060, 45f, 1.15f);
        BonePile(3180, 2200, 33);
        Place(smallSkull, 3200, 1950, 18f, 0.7f);
        Place(boneB, 3170, 1930, 18f, 2.2f, lift: 2f);
        Place(urnSquare, 3640, 2320, 38f, 0.9f);
        Place(skull, 3620, 2240, 8f, 4.1f, lift: 1f);
        Place(ribcage, 3650, 2150, 13f, 1.0f, lift: 10f);

        // ---- the east landing: a lit shelf to catch a breath on.
        Place(plaqueCandles, 3760, 1565, 16f, 0.3f);
        Place(urnRound, 3845, 1570, 38f, 1.1f);
        Place(lanternStanding, 3745, 1985, 24f);
        Place(skull, 3855, 1990, 8f, 3.3f, lift: 1f);

        // ---- the deep stair.
        Hang(lanternHanging, 3800, 2280, DeepStairCeil);
        Hang(lanternHanging, 3800, 2520, DeepStairCeil);

        // ---- the catacombs: grave markers file the lanes — rows along the
        // north and south walls, and stones filling the gaps BETWEEN the
        // pillars of each row, so the pillar lines read as burial shelving
        // while the walked lanes stay open.
        Place(markerA, 3415, 2848, 22f, Mathf.Pi + 0.1f);
        Place(graveWide, 3480, 2852, 40f, Mathf.Pi - 0.06f);
        Place(graveCross, 3618, 2850, 40f, Mathf.Pi + 0.04f);
        Place(markerB, 3682, 2848, 22f, Mathf.Pi - 0.12f);
        Place(graveRoof, 3418, 3052, 40f, 0.08f);
        Place(markerB, 3480, 3048, 22f, -0.1f);
        Place(markerA, 3615, 3050, 22f, 0.05f);
        Place(graveBroken, 3680, 3052, 40f, 0.15f);
        Place(markerA, 3300, 2762, 22f, 0.06f);
        Place(gravestone, 3420, 2760, 20f, -0.08f);
        Place(markerB, 3540, 2764, 22f, 0.1f);
        Place(graveCross, 3650, 2762, 40f, -0.05f);
        Place(graveWide, 3290, 3238, 40f, Mathf.Pi + 0.07f);
        Place(markerB, 3410, 3242, 22f, Mathf.Pi - 0.1f);
        Place(gravestone, 3530, 3240, 20f, Mathf.Pi + 0.12f);
        Place(markerA, 3660, 3238, 22f, Mathf.Pi);
        Place(graveBroken, 3800, 3242, 40f, Mathf.Pi - 0.08f);
        Place(graveRuin, 3270, 2760, 20f, 0.5f); // a grave already broken open
        BonePile(3325, 2885, 41);
        BonePile(3577, 3082, 42);
        BonePile(3775, 2875, 43);
        Place(urnRound, 3252, 2888, 38f, 0.9f);
        Place(urnSquare, 3252, 3112, 38f, 2.1f);
        Place(lanternStanding, 3700, 2742, 24f);

        // ---- the low gallery.
        Hang(lanternHanging, 2750, 3000, GalleryCeil);
        Hang(lanternHanging, 3050, 3000, GalleryCeil);

        // ---- the Mausoleum: the shrine against the west wall behind the
        // dais, its altar before it, obelisks marking the court's corners,
        // candle clusters on the dais rim and a ring of skull-candles around
        // it, the kings' carved coffins and the honored dead along the walls,
        // and fire baskets flanking the walk in from the east door.
        Place(shrine, 1872, 3000, 22f, Mathf.Pi / 2);
        Place(shrineCandles, 1870, 2905, 22f, Mathf.Pi / 2 - 0.1f);
        Place(shrineCandles, 1870, 3095, 22f, Mathf.Pi / 2 + 0.1f);
        Place(altar, 1965, 3000, 42f);
        Place(obelisk, 2000, 2800, 45f);
        Place(obelisk, 2400, 2800, 45f);
        Place(obelisk, 2000, 3200, 45f);
        Place(obelisk, 2400, 3200, 45f);
        Place(candleCluster, 2070, 2872, 38f);
        Place(candleCluster, 2330, 2872, 38f);
        Place(candleCluster, 2070, 3128, 38f);
        Place(candleCluster, 2330, 3128, 38f);
        foreach (var (cx, cz, cy) in new[]
                 {
                     (2008f, 2952f, 0.6f), (2008f, 3048f, 2.1f), (2392f, 2952f, 4.2f), (2392f, 3048f, 5.5f),
                     (2152f, 2808f, 1.3f), (2248f, 2808f, 3.7f), (2152f, 3192f, 5.0f), (2248f, 3192f, 0.2f),
                 })
            Place(skullCandle, cx, cz, 9f, cy);
        Place(plaqueCandles, 2545, 2890, 16f, -Mathf.Pi / 2);
        Place(plaqueCandles, 2545, 3110, 16f, -Mathf.Pi / 2);
        Place(postSkull, 2505, 2925, 20f, 2.9f);
        Place(postSkull, 2505, 3075, 20f, 3.4f);
        Place(fireBasket, 2420, 2940, 42f);
        Place(fireBasket, 2420, 3060, 42f);
        Place(coffinCarved, 1885, 2705, 19f, 0.05f);
        Place(coffinCarved, 1885, 3295, 19f, -0.05f);
        Place(gravestone, 2080, 2676, 20f, 0.1f);
        Place(gravestone, 2320, 2678, 20f, -0.08f);
        Place(gravestone, 2080, 3324, 20f, Mathf.Pi - 0.1f);
        Place(gravestone, 2320, 3322, 20f, Mathf.Pi + 0.06f);
        Place(ribcage, 2450, 2680, 13f, 1.9f, lift: 10f);
        Place(skull, 1980, 3320, 8f, 3.6f, lift: 1f);
        Place(urnRound, 2540, 2660, 38f, 0.7f);
        Place(urnSquare, 2540, 3340, 38f, 2.4f);
    }
}

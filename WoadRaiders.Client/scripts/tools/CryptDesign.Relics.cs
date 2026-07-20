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
/// EVERY PLACEMENT IS RELATIVE TO A CHAMBER. Nothing here names a world
/// coordinate: pieces are set an INSET from a named wall, or walked along one,
/// or scattered across a fraction of a floor. That is what lets the plan scale
/// — a coffin asked to stand against the north wall still stands against it
/// when the room trebles, where a hard-coded coordinate would have left it
/// stranded in the middle of a much larger floor. It also means a bigger room
/// is dressed with MORE pieces rather than the same few, adrift.
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

    /// <summary>Which wall of a chamber a piece is set against.</summary>
    private enum Side { North, South, West, East }

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

        // A point an INSET in from one of a chamber's walls, at a fraction
        // ALONG that wall — the primitive the whole dressing is built from.
        Vector3 OnWall(Chamber c, Side side, float along, float inset) => side switch
        {
            Side.North => new Vector3(Mathf.Lerp(c.X0, c.X1, along), 0f, c.Z0 + inset),
            Side.South => new Vector3(Mathf.Lerp(c.X0, c.X1, along), 0f, c.Z1 - inset),
            Side.West => new Vector3(c.X0 + inset, 0f, Mathf.Lerp(c.Z0, c.Z1, along)),
            _ => new Vector3(c.X1 - inset, 0f, Mathf.Lerp(c.Z0, c.Z1, along)),
        };

        // Which way a piece faces to have its back to that wall.
        float Facing(Side side) => side switch
        {
            Side.North => Mathf.Pi,
            Side.South => 0f,
            Side.West => Mathf.Pi / 2f,
            _ => -Mathf.Pi / 2f,
        };

        // Set a piece against a wall, back to the stone.
        void Against(Kit kit, string room, Side side, float along, float scale,
                     float inset = 110f, float skew = 0f, float lift = 0f)
        {
            var p = OnWall(Named(room), side, along, inset);
            Place(kit, p.X, p.Z, scale, Facing(side) + skew, lift);
        }

        // Walk a wall, dropping a piece every stride — how a long wall gets
        // dressed at any length. `pick` chooses by index so rows read varied.
        void AlongWall(string room, Side side, float from, float to, int count,
                       System.Func<int, (Kit kit, float scale)> pick,
                       float inset = 110f, int salt = 0)
        {
            var c = Named(room);
            for (var i = 0; i < count; i++)
            {
                var along = count == 1 ? (from + to) * 0.5f : Mathf.Lerp(from, to, i / (float)(count - 1));
                var (kit, scale) = pick(i);
                var p = OnWall(c, side, along, inset);
                Place(kit, p.X, p.Z, scale, Facing(side) + (Hash(i, salt, 71) - 0.5f) * 0.3f);
            }
        }

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

        // Scatter remains across a fraction-rect of a chamber's floor.
        void Ossuary(string room, float u0, float v0, float u1, float v1, int count, int salt)
        {
            var c = Named(room);
            for (var k = 0; k < count; k++)
            {
                var x = Mathf.Lerp(c.X0 + c.Width * u0, c.X0 + c.Width * u1, Hash(k, salt, 821));
                var z = Mathf.Lerp(c.Z0 + c.Depth * v0, c.Z0 + c.Depth * v1, Hash(k, salt, 823));
                var yaw = Hash(k, salt, 827) * Mathf.Tau;
                switch ((int)(Hash(k, salt, 829) * 5f))
                {
                    case 0: Place(ribcage, x, z, 13f, yaw, lift: 10f); break;
                    case 1: Place(smallSkull, x, z, 18f, yaw); break;
                    case 2: Place(boneA, x, z, 16f, yaw, lift: 2f); break;
                    case 3: Place(debris, x, z, 45f, yaw); break;
                    default: Place(skull, x, z, 8f, yaw, lift: 1f); break;
                }
            }
        }

        // Hang a piece from a roof: the hanging lantern's origin is its hook
        // (its mesh hangs ~1.3 native units BELOW the origin), so it mounts
        // at the ceiling and swings under it.
        void Hang(Kit kit, float x, float z, float ceiling, float scale = 24f) =>
            Add(kit.At(new Vector3(x, ceiling - 2f, z), scale));

        // Hang lanterns on a grid under a chamber's own roof.
        void HangThrough(string room, int nx, int nz)
        {
            var c = Named(room);
            for (var i = 1; i <= nx; i++)
                for (var j = 1; j <= nz; j++)
                    Hang(lanternHanging, Mathf.Lerp(c.X0, c.X1, i / (nx + 1f)),
                         Mathf.Lerp(c.Z0, c.Z1, j / (nz + 1f)), c.TopY, 40f);
        }

        // ---- the undercroft: the founder's sealed tomb against the north
        // wall, the first graves filing the west, and a coffin someone already
        // dragged out into the light.
        Against(tombFront, "undercroft", Side.North, 0.5f, 26f, inset: 150f);
        AlongWall("undercroft", Side.West, 0.15f, 0.85f, 5,
                  i => i % 2 == 0 ? (markerA, 22f) : (markerB, 22f), salt: 3);
        Against(gravestone, "undercroft", Side.West, 0.5f, 20f, inset: 240f);
        Against(coffinOld, "undercroft", Side.North, 0.82f, 45f, inset: 220f, skew: 0.35f);
        Against(lanternStanding, "undercroft", Side.East, 0.28f, 24f, inset: 180f);
        Against(lanternStanding, "undercroft", Side.East, 0.72f, 24f, inset: 180f);
        Ossuary("undercroft", 0.35f, 0.20f, 0.90f, 0.85f, 9, 11);
        HangThrough("undercroft", 2, 2);

        // ---- the hall of the dead: the wall bays are the burial niches — a
        // coffin every stride down both long walls with the spilled dead
        // between them, urns at the west door, skull-post wardens at the east.
        AlongWall("hall", Side.North, 0.10f, 0.90f, 7,
                  i => i % 3 == 0 ? (coffinOld, 45f) : i % 3 == 1 ? (coffin, 18f) : (coffinCarved, 19f),
                  inset: 130f, salt: 5);
        AlongWall("hall", Side.South, 0.10f, 0.90f, 7,
                  i => i % 3 == 0 ? (coffinCarved, 19f) : i % 3 == 1 ? (coffin, 18f) : (coffinOld, 45f),
                  inset: 130f, salt: 6);
        Against(urnRound, "hall", Side.West, 0.36f, 38f, inset: 150f);
        Against(urnSquare, "hall", Side.West, 0.64f, 38f, inset: 150f);
        Against(postSkull, "hall", Side.East, 0.38f, 20f, inset: 150f);
        Against(postSkull, "hall", Side.East, 0.62f, 20f, inset: 150f);
        Ossuary("hall", 0.10f, 0.08f, 0.92f, 0.30f, 10, 21);
        Ossuary("hall", 0.10f, 0.70f, 0.92f, 0.92f, 10, 22);
        Place(skullCandle, Named("hall").X0 + 700f, Named("hall").Z0 + 640f, 9f, 0.8f);
        Place(skullCandle, Named("hall").X1 - 700f, Named("hall").Z1 - 640f, 9f, 2.4f);
        HangThrough("hall", 3, 2);

        // ---- the passages: lanterns from every corridor roof, walked along
        // each one so a longer stair simply gets more of them.
        foreach (var p in _passages)
        {
            var steps = Mathf.Max(2, Mathf.RoundToInt((p.AlongZ ? p.Z1 - p.Z0 : p.X1 - p.X0) / 700f));
            for (var i = 1; i < steps; i++)
            {
                var f = i / (float)steps;
                Hang(lanternHanging,
                     p.AlongZ ? (p.X0 + p.X1) * 0.5f : Mathf.Lerp(p.X0, p.X1, f),
                     p.AlongZ ? Mathf.Lerp(p.Z0, p.Z1, f) : (p.Z0 + p.Z1) * 0.5f,
                     p.TopY, 36f);
            }
        }

        // ---- the span: iron rails along the bridge (one length torn away —
        // the gap tells the story), witchfire baskets at its ends, and below,
        // everyone the bridge has ever dropped. Chasm placements stay OUT of
        // the deck's z-band: OnFloor seats on the HIGHEST floor, which there
        // is the bridge, not the pit.
        var chasm = Named("span");
        var deckZ0 = S(1740f);
        var deckZ1 = S(1860f);
        for (var i = 0; i < 9; i++)
        {
            var x = Mathf.Lerp(chasm.X0 + 200f, chasm.X1 - 200f, i / 8f);
            Place(i == 4 ? ironFenceBent : ironFence, x, deckZ0 + 26f, 40f);
            Place(i == 3 ? ironFenceBent : ironFence, x, deckZ1 - 26f, 40f);
        }
        Place(fireBasket, chasm.X0 + 150f, chasm.MidZ - 90f, 42f);
        Place(fireBasket, chasm.X0 + 150f, chasm.MidZ + 90f, 42f);
        Place(fireBasket, chasm.X1 - 150f, chasm.MidZ - 90f, 42f);
        Place(fireBasket, chasm.X1 - 150f, chasm.MidZ + 90f, 42f);
        Place(skullCandle, chasm.X0 + 190f, deckZ0 - 220f, 9f, 1.9f); // someone's candle, still lit
        // The north pit: the nameless dead, and something far older.
        Place(greatBone, chasm.MidX - 300f, chasm.Z0 + 800f, 7f, 0.9f);
        Place(standingBones, chasm.MidX + 250f, chasm.Z0 + 450f, 10f, 2.6f); // still on its feet
        Place(graveBroken, chasm.X0 + 280f, chasm.Z0 + 1100f, 40f, 0.5f);
        Ossuary("span", 0.10f, 0.04f, 0.92f, 0.38f, 16, 31);
        // The south pit, west of the long stair out.
        Place(coffinOld, chasm.X0 + 250f, chasm.Z1 - 1000f, 45f, 1.15f);
        Ossuary("span", 0.08f, 0.66f, 0.55f, 0.96f, 12, 33);
        Place(urnSquare, chasm.X1 - 260f, chasm.Z1 - 300f, 38f, 0.9f);

        // ---- the east landing: a lit shelf to catch a breath on.
        Against(plaqueCandles, "landing", Side.North, 0.5f, 16f, inset: 140f);
        Against(urnRound, "landing", Side.East, 0.22f, 38f, inset: 150f);
        Against(lanternStanding, "landing", Side.West, 0.78f, 24f, inset: 150f);
        Ossuary("landing", 0.2f, 0.2f, 0.8f, 0.8f, 5, 37);

        // ---- the catacombs: grave markers file the lanes — rows along both
        // long walls, and stones filling the gaps between the pillar rows, so
        // the pillar lines read as burial shelving while the lanes stay open.
        AlongWall("catacombs", Side.North, 0.08f, 0.92f, 8,
                  i => (i % 4) switch
                  {
                      0 => (graveWide, 40f), 1 => (markerA, 22f),
                      2 => (graveCross, 40f), _ => (markerB, 22f),
                  }, inset: 130f, salt: 41);
        AlongWall("catacombs", Side.South, 0.08f, 0.92f, 8,
                  i => (i % 4) switch
                  {
                      0 => (graveRoof, 40f), 1 => (markerB, 22f),
                      2 => (gravestone, 20f), _ => (graveBroken, 40f),
                  }, inset: 130f, salt: 42);
        AlongWall("catacombs", Side.West, 0.2f, 0.8f, 4,
                  i => i % 2 == 0 ? (markerA, 22f) : (graveCross, 40f), inset: 140f, salt: 43);
        Against(graveRuin, "catacombs", Side.West, 0.1f, 20f, inset: 200f); // already broken open
        Against(urnRound, "catacombs", Side.West, 0.34f, 38f, inset: 150f);
        Against(urnSquare, "catacombs", Side.West, 0.66f, 38f, inset: 150f);
        Against(lanternStanding, "catacombs", Side.East, 0.2f, 24f, inset: 160f);
        Ossuary("catacombs", 0.15f, 0.15f, 0.85f, 0.85f, 14, 45);
        HangThrough("catacombs", 3, 2);

        // ---- the Mausoleum: the shrine against the west wall behind the
        // dais, its altar before it, obelisks marking the court's corners,
        // candle clusters on the dais rim, the kings' carved coffins along the
        // walls, and fire baskets flanking the walk in from the east door.
        var court = Named("mausoleum");
        Against(shrine, "mausoleum", Side.West, 0.5f, 26f, inset: 160f);
        Against(shrineCandles, "mausoleum", Side.West, 0.38f, 22f, inset: 170f);
        Against(shrineCandles, "mausoleum", Side.West, 0.62f, 22f, inset: 170f);
        Place(altar, court.X0 + 620f, court.MidZ, 48f, Mathf.Pi / 2f);
        foreach (var (u, v) in new[] { (0.16f, 0.16f), (0.84f, 0.16f), (0.16f, 0.84f), (0.84f, 0.84f) })
            Place(obelisk, court.X0 + court.Width * u, court.Z0 + court.Depth * v, 46f);
        // The dais rim: candles round the boss's floor, and skull-lights beyond.
        for (var i = 0; i < 8; i++)
        {
            var a = i / 8f * Mathf.Tau;
            Place(candleCluster, court.MidX + Mathf.Cos(a) * 560f, court.MidZ + Mathf.Sin(a) * 560f, 40f, a);
            Place(skullCandle, court.MidX + Mathf.Cos(a + 0.4f) * 900f,
                  court.MidZ + Mathf.Sin(a + 0.4f) * 900f, 9f, a);
        }
        AlongWall("mausoleum", Side.North, 0.25f, 0.75f, 4,
                  i => i % 2 == 0 ? (coffinCarved, 19f) : (coffin, 18f), inset: 130f, salt: 51);
        AlongWall("mausoleum", Side.South, 0.25f, 0.75f, 4,
                  i => i % 2 == 0 ? (coffin, 18f) : (coffinCarved, 19f), inset: 130f, salt: 52);
        Place(fireBasket, court.X1 - 380f, court.MidZ - 260f, 42f);
        Place(fireBasket, court.X1 - 380f, court.MidZ + 260f, 42f);
        Ossuary("mausoleum", 0.2f, 0.06f, 0.8f, 0.18f, 7, 55);
        Ossuary("mausoleum", 0.2f, 0.82f, 0.8f, 0.94f, 7, 56);
        HangThrough("mausoleum", 2, 2);
    }
}

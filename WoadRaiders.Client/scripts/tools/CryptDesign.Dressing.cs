using System.Collections.Generic;
using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's DRESSING — the layer that turns built space into inhabited space.
///
/// Three rules run this file, and all three are the spec's (LOOK-018…020):
///
///   QUOTA. Each space gets exactly ONE hero prop, three to five supporting, and
///   filler without limit. The hero takes the strongest light and the clearest
///   floor around it, because a room where everything is interesting is a room
///   where nothing is. Corridors are deliberately STARVED — the budget is spent
///   at the far end of the sight line, which is where a player is looking anyway.
///
///   VIGNETTE. Story is staged, never scattered: a fixed relative arrangement of
///   props, turned as a whole to face the door it will be read from. Randomising
///   the ARRANGEMENT destroys the story; randomising which VARIANT fills each
///   slot preserves it. So the last camp is always a fire, a lantern and three
///   sleepers around it — but which gravestone, which urn, which skull varies.
///
///   VARIETY. Players notice repeated clutter long before they notice repeated
///   architecture, so the architecture kit stays small and this pass varies hard:
///   full yaw, a little pitch and roll, non-uniform scale, and positions loose
///   off the grid.
///
/// COLLISION. Being a kit piece buys nothing (REALM-C constitution): every mesh
/// is collision until an author says otherwise. So the split here is deliberate
/// and physical rather than technical — MONUMENTS are things you could not walk
/// through, and they block; DRESSING is ankle-height clutter that Recast's own
/// agent-radius erosion would discard anyway, and a raider snagging on a candle
/// is a bug rather than fiction.
///
/// SCALE. Every kit is authored in metres and this realm is not: a raider is ~44
/// units and a 4 m door is 96, so ONE METRE IS ABOUT 24 UNITS. Each kit below
/// therefore carries the scale that makes it its own real size, measured from the
/// authored AABBs (tools/probe_assets.gd) rather than guessed — the kits disagree
/// with each other by a factor of eight, and eyeballing it is how a realm ends up
/// with skulls the size of an altar.
/// </summary>
public sealed partial class CryptDesign
{
    /// <summary>One kit piece, loaded once and instanced everywhere. <paramref
    /// name="Scale"/> is what takes it from its own authoring units to this
    /// realm's, so a call site never states one.</summary>
    private readonly record struct Kit(PackedScene Scene, float Scale)
    {
        public static Kit Load(string file, float scale) =>
            new(GD.Load<PackedScene>($"res://assets/crypt/{file}"), scale);
    }

    // Era III and the surface: church furniture, markers, the mausoleum front.
    private Kit _crypt, _shrine, _shrineCandles, _plaque, _altar, _column, _obelisk;
    private Kit _gravestone, _graveBroken, _graveCross, _graveRoof, _graveWide, _graveRuined;
    private Kit _marker, _markerB, _postSkull;
    // The dead themselves, and what people left behind them.
    private Kit _coffin, _coffinOld, _coffinPlain, _coffinFine;
    private Kit _skeleton, _bonePile, _ribcage, _skullA, _skullB;
    private Kit _boneA, _boneB, _boneC;
    private Kit _urnRound, _urnSquare, _candles, _lantern, _lanternHung, _lanternIron;
    private Kit _fireBasket, _debris, _fence, _fenceBroken, _skullCandle;

    private Node3D _monuments = null!;
    private Node3D _dressing = null!;
    private int _propCount;
    private int _heroCount;

    /// <summary>Lights a vignette asked for, handed to <c>Gloom()</c> so they land
    /// in the same folder as every other flame and are covered by the same
    /// flicker. A vignette without its own light is a pile of props in the dark.</summary>
    private readonly List<(Vector3 At, Color Colour, float Energy, float Range)> _vignetteLights = new();

    private void Dressing()
    {
        LoadKits();
        _monuments = _scene.Folder("Monuments");
        _dressing = _scene.DeclarePassable(_scene.Folder("Dressing"));

        Porch();
        NaveDressing();
        OssuaryDressing();
        FaultDressing();
        GalleryDressing();
        CubiculumDressing();
        ForecourtDressing();
        WheelDressing();
        Corridors();
    }

    private void LoadKits()
    {
        // KayKit's Halloween pack — authored around a 1 m grid.
        _crypt = Kit.Load("kaykit_halloween/crypt.gltf", 16f);
        _shrine = Kit.Load("kaykit_halloween/shrine.gltf", 24f);
        _shrineCandles = Kit.Load("kaykit_halloween/shrine_candles.gltf", 24f);
        _plaque = Kit.Load("kaykit_halloween/plaque_candles.gltf", 12f);
        _postSkull = Kit.Load("kaykit_halloween/post_skull.gltf", 18f);
        _gravestone = Kit.Load("kaykit_halloween/gravestone.gltf", 18f);
        _graveRuined = Kit.Load("kaykit_halloween/grave_A_destroyed.gltf", 18f);
        _marker = Kit.Load("kaykit_halloween/gravemarker_A.gltf", 22f);
        _markerB = Kit.Load("kaykit_halloween/gravemarker_B.gltf", 22f);
        _coffinPlain = Kit.Load("kaykit_halloween/coffin.gltf", 16f);
        _coffinFine = Kit.Load("kaykit_halloween/coffin_decorated.gltf", 16f);
        _ribcage = Kit.Load("kaykit_halloween/ribcage.gltf", 10f);
        _skullA = Kit.Load("kaykit_halloween/skull.gltf", 5f);
        _skullCandle = Kit.Load("kaykit_halloween/skull_candle.gltf", 5f);
        _boneA = Kit.Load("kaykit_halloween/bone_A.gltf", 10f);
        _boneB = Kit.Load("kaykit_halloween/bone_B.gltf", 10f);
        _boneC = Kit.Load("kaykit_halloween/bone_C.gltf", 10f);
        _lantern = Kit.Load("kaykit_halloween/lantern_standing.gltf", 15f);
        _lanternHung = Kit.Load("kaykit_halloween/lantern_hanging.gltf", 15f);

        // Kenney's graveyard kit runs miniature — near 1 unit for a person's
        // height — so it scales more than twice as hard as KayKit's.
        _altar = Kit.Load("kenney_graveyard/altar-stone.glb", 40f);
        _column = Kit.Load("kenney_graveyard/column-large.glb", 40f);
        _obelisk = Kit.Load("kenney_graveyard/pillar-obelisk.glb", 42f);
        _graveBroken = Kit.Load("kenney_graveyard/gravestone-broken.glb", 40f);
        _graveCross = Kit.Load("kenney_graveyard/gravestone-cross.glb", 40f);
        _graveRoof = Kit.Load("kenney_graveyard/gravestone-roof.glb", 40f);
        _graveWide = Kit.Load("kenney_graveyard/gravestone-wide.glb", 40f);
        _coffin = Kit.Load("kenney_graveyard/coffin.glb", 42f);
        _coffinOld = Kit.Load("kenney_graveyard/coffin-old.glb", 42f);
        _urnRound = Kit.Load("kenney_graveyard/urn-round.glb", 40f);
        _urnSquare = Kit.Load("kenney_graveyard/urn-square.glb", 40f);
        _candles = Kit.Load("kenney_graveyard/candle-multiple.glb", 40f);
        _lanternIron = Kit.Load("kenney_graveyard/lantern-candle.glb", 40f);
        // NOT Kenney's fire-basket. Its atlas is painted for a daylit
        // graveyard and the basket samples a GRASS-GREEN patch of it, so every
        // brazier in the realm had a bright green blob sitting beside its
        // flame. Same failure as that kit's columns, which came out banded in
        // orange and blue. KayKit's dungeon torch is lit for a dungeon.
        _fireBasket = Kit.Load("../../addons/kaykit_dungeon_remastered/Assets/gltf/torch.gltf.glb", 20f);
        _debris = Kit.Load("kenney_graveyard/debris.glb", 45f);
        _fence = Kit.Load("kenney_graveyard/iron-fence.glb", 40f);
        _fenceBroken = Kit.Load("kenney_graveyard/iron-fence-damaged.glb", 40f);

        // Poly Pizza — the big set pieces the other two kits have no answer for.
        _skeleton = Kit.Load("polypizza/skeleton_quaternius.glb", 9f);
        _bonePile = Kit.Load("polypizza/bone_large_quaternius.glb", 6f);
        _skullB = Kit.Load("polypizza/skull_quaternius.glb", 8f);
    }

    // ----------------------------------------------------------- the placement

    /// <summary>
    /// One prop, varied. Full yaw, a little pitch and roll, non-uniform scale in
    /// 0.85–1.15, and a position loose off whatever grid the caller was thinking
    /// in — all of it deterministic, because the realm regenerates byte for byte.
    ///
    /// Mirroring by NEGATIVE scale is deliberately not offered: it produces the
    /// mixed-sign basis Node3D warns about, and normals and culling then disagree
    /// between MeshInstance3D and MultiMeshInstance3D (godotengine/godot#108739).
    /// A 180° yaw costs nothing and is always correct.
    /// </summary>
    private Node3D Prop(Kit kit, Vector3 at, int salt, float yaw = float.NaN,
                        float scale = 1f, float loose = 0f, bool monument = false)
    {
        var node = kit.Scene.Instantiate<Node3D>();
        var jitterX = Jitter(salt, 1, 301, loose);
        var jitterZ = Jitter(salt, 2, 303, loose);
        node.Position = at + new Vector3(jitterX, 0f, jitterZ);
        // Pitch and roll stay SMALL. A prop lying at a real angle needs the floor
        // modelled under it; a prop leaning two degrees just looks like it has been
        // there a while, which is the whole effect wanted.
        var pitch = Jitter(salt, 3, 305, 0.05f);
        var turn = float.IsNaN(yaw) ? Hash(salt, 4, 307) * Mathf.Tau : yaw + Jitter(salt, 5, 309, 0.2f);
        var roll = Jitter(salt, 6, 311, 0.05f);
        var s = kit.Scale * scale;
        var size = new Vector3(s * (0.85f + Hash(salt, 7, 313) * 0.3f),
                               s * (0.85f + Hash(salt, 8, 315) * 0.3f),
                               s * (0.85f + Hash(salt, 9, 317) * 0.3f));
        // Set the rotation×scale basis directly from deterministic trig (see Det):
        // Godot's own Euler→Basis behind node.Rotation runs on libm at save time
        // and a prop's random yaw makes its serialised bytes host-dependent.
        node.Basis = Det.EulerScale(pitch, turn, roll, size);
        var parent = monument ? _monuments : _dressing;
        node.Name = $"{(monument ? "Monument" : "Prop")}{_propCount++}";
        parent.AddChild(node);
        return node;
    }

    /// <summary>
    /// The space's ONE hero: placed exactly, never jittered into place, and
    /// remembered so <c>Gloom()</c> can give it the room's strongest flame. A hero
    /// blocks — it is the thing in the room you walk around.
    /// </summary>
    private void Hero(Kit kit, Vector3 at, float yaw, Color light, float energy = 7f, float range = 300f)
    {
        var node = kit.Scene.Instantiate<Node3D>();
        node.Position = at;
        // Deterministic basis (see Det) — a hero's yaw is an arbitrary constant, so
        // Godot's libm Euler→Basis would make its transform host-dependent.
        node.Basis = Det.EulerScale(0f, yaw, 0f, new Vector3(kit.Scale, kit.Scale, kit.Scale));
        node.Name = $"Hero{_heroCount++}";
        _monuments.AddChild(node);
        // 45 rather than 70: a lamp with nothing modelled under it reads as a
        // floating orb the moment it is high enough to be seen as a source
        // rather than as light coming off the thing it lights.
        _vignetteLights.Add((at + new Vector3(0f, 45f, 0f), light, energy, range));
    }

    /// <summary>
    /// Gestalt clustering: one anchor, then three to five shrunken, rotated,
    /// offset repeats of it. Uniform scatter reads as wallpaper; a clump with a
    /// clear anchor reads as something that happened in one place.
    /// </summary>
    private void Cluster(Kit kit, Vector3 at, int salt, int count = 4, float spread = 44f)
    {
        Prop(kit, at, salt, loose: 6f);
        for (var i = 1; i <= count; i++)
        {
            var a = Hash(salt, i, 319) * Mathf.Tau;
            var r = 12f + Hash(salt, i, 321) * spread;
            Prop(kit, _scene.OnFloor(at + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r)),
                 salt * 31 + i, scale: 0.55f + Hash(salt, i, 323) * 0.35f, loose: 5f);
        }
    }

    /// <summary>
    /// A row of props strung along a wall, each one a variant drawn from the set —
    /// which is how a wall of gravestones stops reading as one gravestone copied.
    /// </summary>
    private void Along(Kit[] variants, Vector3 from, Vector3 to, int count, int salt,
                       float yaw = float.NaN, float loose = 14f)
    {
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0.5f : i / (count - 1f);
            var kit = variants[(int)(Hash(salt, i, 325) * variants.Length) % variants.Length];
            Prop(kit, _scene.OnFloor(from.Lerp(to, t)), salt * 17 + i, yaw, loose: loose);
        }
    }

    // ------------------------------------------------------------ the vignettes

    /// <summary>
    /// THE LAST CAMP — somebody got this far and stopped. A dead fire, a lantern
    /// set down beside it, bedding, and three of them still there.
    ///
    /// The arrangement is FIXED and the whole thing turns as one to face
    /// <paramref name="facing"/>, which is the door it will be read from. That is
    /// the entire difference between environmental storytelling and clutter: a
    /// player has to be able to see it as a scene, from where they enter.
    /// </summary>
    private void LastCamp(Vector3 at, float facing, int salt)
    {
        var turn = new Basis(Vector3.Up, facing);
        Vector3 Slot(float x, float z) => _scene.OnFloor(at + turn * new Vector3(x, 0f, z));

        Prop(_fireBasket, Slot(0f, 0f), salt, loose: 4f);
        Prop(_lantern, Slot(-46f, 22f), salt + 1, loose: 6f);
        // Three sleepers round the fire, at the angles people actually lie at.
        foreach (var (x, z, i) in new[] { (-52f, -40f, 0), (54f, -34f, 1), (10f, 62f, 2) })
        {
            Prop(_skeleton, Slot(x, z), salt + 10 + i, scale: 0.9f, loose: 8f);
            Prop(i == 1 ? _debris : _urnRound, Slot(x * 0.6f, z * 0.6f), salt + 20 + i, loose: 10f);
        }
        // The fire is out, but not long enough that the last of it has gone.
        _vignetteLights.Add((at + new Vector3(0f, 34f, 0f), Ember, 2.4f, 190f));
    }

    /// <summary>
    /// THE ROBBING — a coffin dragged clear of its niche, the lid off, the
    /// contents gone through, and the lantern they were working by dropped and
    /// left. What is missing tells this one; there is nothing valuable in it.
    /// </summary>
    private void Robbing(Vector3 at, float facing, int salt)
    {
        var turn = new Basis(Vector3.Up, facing);
        Vector3 Slot(float x, float z) => _scene.OnFloor(at + turn * new Vector3(x, 0f, z));

        Prop(_coffinOld, Slot(0f, 0f), salt, yaw: facing + 0.35f, loose: 5f);
        Prop(_debris, Slot(40f, 30f), salt + 1, loose: 8f);        // the lid, off
        Prop(_lanternIron, Slot(-38f, 26f), salt + 2, loose: 5f);  // dropped, still lit
        Prop(_ribcage, Slot(30f, -34f), salt + 3, loose: 9f);
        Prop(_skullA, Slot(-24f, -42f), salt + 4, loose: 9f);
        Prop(_boneC, Slot(56f, -12f), salt + 5, loose: 11f);
        _vignetteLights.Add((at + turn * new Vector3(-38f, 26f, 0f) + new Vector3(0f, 26f, 0f),
                             Tallow, 2.0f, 150f));
    }

    /// <summary>
    /// THE OFFERING — candles, urns and a plaque set out before a grave that
    /// mattered to somebody. The only vignette in the realm that is tidy, which
    /// is what makes it read as care rather than as aftermath.
    /// </summary>
    private void Offering(Vector3 at, float facing, int salt)
    {
        var turn = new Basis(Vector3.Up, facing);
        Vector3 Slot(float x, float z) => _scene.OnFloor(at + turn * new Vector3(x, 0f, z));

        Prop(_plaque, Slot(0f, 0f), salt, yaw: facing, loose: 0f);
        Prop(_candles, Slot(-30f, 26f), salt + 1, yaw: facing, loose: 3f);
        Prop(_candles, Slot(30f, 26f), salt + 2, yaw: facing, loose: 3f);
        Prop(_urnSquare, Slot(-48f, -4f), salt + 3, loose: 4f);
        Prop(_urnRound, Slot(48f, -4f), salt + 4, loose: 4f);
        _vignetteLights.Add((at + new Vector3(0f, 30f, 0f), Tallow, 3.2f, 170f));
    }

    // --------------------------------------------------------------- the rooms

    /// <summary>
    /// The Broken Porch: the last of the surface. A ruined mausoleum front stands
    /// in the daylight of the shaft, and the markers still stand in rows because
    /// nobody has been down here to knock them over.
    /// </summary>
    private void Porch()
    {
        var s = Named("B1");
        Hero(_crypt, new Vector3(s.X0 + 150f, s.FloorY, s.MidZ - 210f), Mathf.Pi / 2f, Daylight, 4f, 420f);

        Along(new[] { _gravestone, _graveRuined, _marker, _markerB },
              new Vector3(s.X0 + 130f, s.FloorY, s.MidZ + 130f),
              new Vector3(s.X0 + 480f, s.FloorY, s.MidZ + 250f), 5, salt: 331);
        Prop(_graveCross, _scene.OnFloor(new Vector3(s.X0 + 420f, s.FloorY, s.MidZ - 250f)), 337);
        Prop(_fence, _scene.OnFloor(new Vector3(s.X0 + 300f, s.FloorY, s.Z0 + 90f)), 339, yaw: 0f);
        Prop(_fenceBroken, _scene.OnFloor(new Vector3(s.X0 + 380f, s.FloorY, s.Z0 + 90f)), 341, yaw: 0f);
        Cluster(_debris, _scene.OnFloor(new Vector3(s.X1 - 190f, s.FloorY, s.MidZ + 60f)), 343);
    }

    /// <summary>
    /// The Minster nave. The hero is the shrine on the axis at the east end — the
    /// thing you walk the whole hall toward — and the aisles carry the church
    /// furniture that says this was a building people came to.
    /// </summary>
    private void NaveDressing()
    {
        var s = Named("B2");
        Hero(_shrine, new Vector3(s.X1 - 190f, s.FloorY, s.MidZ), -Mathf.Pi / 2f, Tallow, 9f, 380f);

        // Supporting: an altar under the shrine, and memorials against the aisle
        // walls. NOT Kenney's columns, which were here and came out banded in
        // orange and blue — that kit's atlas is painted for a daylit graveyard,
        // and a piece of it under firelight reads as a traffic cone standing in a
        // church. The arcade piers already mark the crossing; they are sculpted,
        // they are the right stone, and a kit column beside one is redundant.
        Prop(_altar, _scene.OnFloor(new Vector3(s.X1 - 330f, s.FloorY, s.MidZ)), 347, yaw: 0f);
        foreach (var (x, z, yaw, salt) in new[]
                 {
                     (1400f, 1420f, 0f, 349), (2500f, 1420f, 0f, 353),
                     (1400f, 2900f, Mathf.Pi, 359), (2500f, 2900f, Mathf.Pi, 367),
                 })
            Prop(salt % 2 == 1 ? _gravestone : _graveRoof,
                 _scene.OnFloor(new Vector3(x, s.FloorY, z)), salt, yaw: yaw);

        Offering(_scene.OnFloor(new Vector3(s.X1 - 420f, s.FloorY, s.Z0 + 210f)), -Mathf.Pi / 2f, 373);

        // Filler in the aisles only. The vessel stays clear: it is where the
        // fight is, and where the camera needs the floor readable.
        Cluster(_debris, _scene.OnFloor(new Vector3(1180f, s.FloorY, s.Z1 - 200f)), 379, 3);
        Cluster(_debris, _scene.OnFloor(new Vector3(2200f, s.FloorY, s.Z0 + 220f)), 383, 3);
        Along(new[] { _urnRound, _urnSquare, _candles },
              new Vector3(1000f, s.FloorY, s.Z1 - 150f),
              new Vector3(2900f, s.FloorY, s.Z1 - 150f), 6, salt: 389, yaw: Mathf.Pi);
    }

    /// <summary>
    /// The Ossuary. The hero is a fine coffin standing at the arcosolium — the
    /// one grave in the charnel that got an arch — and the two vignettes tell the
    /// story the loculus states already spell along the walls: somebody was here
    /// before, worked east, and did not come back out.
    /// </summary>
    private void OssuaryDressing()
    {
        var s = Named("B3");
        Hero(_coffinFine, new Vector3(s.MidX, s.FloorY, s.Z1 - 140f), 0f, Tallow, 8f, 320f);

        Prop(_coffin, _scene.OnFloor(new Vector3(s.X0 + 320f, s.FloorY, s.Z0 + 190f)), 397, yaw: 0.3f);
        Prop(_coffinOld, _scene.OnFloor(new Vector3(s.X0 + 300f, s.FloorY, s.Z1 - 210f)), 401, yaw: 2.9f);
        Prop(_obelisk, _scene.OnFloor(new Vector3(s.X0 + 180f, s.FloorY, s.MidZ)), 403, yaw: 0f);

        // Read from the WEST door, which is the way in.
        Robbing(_scene.OnFloor(new Vector3(s.X0 + 520f, s.FloorY, s.MidZ + 180f)), -Mathf.Pi / 2f, 409);
        // And the camp at the far end, where the robbing stopped.
        LastCamp(_scene.OnFloor(new Vector3(s.X1 - 430f, s.FloorY, s.MidZ - 150f)), -Mathf.Pi / 2f, 419);

        // Bone spill at the foot of the robbed banks — this is the ONLY loose
        // bone in the room, because the walls are where the bone lives here.
        for (var i = 0; i < 7; i++)
        {
            var x = Mathf.Lerp(s.MidX, s.X1 - 120f, i / 6f);
            Cluster(i % 2 == 0 ? _boneC : _boneA,
                    _scene.OnFloor(new Vector3(x, s.FloorY, s.Z0 + 120f)), 421 + i, 3, 30f);
        }
    }

    /// <summary>
    /// The Fault. Nothing dressed on the span itself — it is 160 wide and every
    /// prop on it is a thing to fall over — so the budget goes to the pit floor,
    /// which is what a raider looks down at from the shelf.
    /// </summary>
    private void FaultDressing()
    {
        var pit = Named("B4");
        Hero(_bonePile, new Vector3(pit.MidX, pit.FloorY, pit.MidZ + 200f), 0.7f, Rushlight, 5f, 340f);

        // Everyone the bridge has ever dropped, lying where they landed — under
        // the DECK, so the fall reads from the shelf above.
        var deck = Named("B4c");
        for (var i = 0; i < 6; i++)
        {
            var x = Mathf.Lerp(deck.X0 + 120f, deck.X1 - 120f, i / 5f);
            Prop(_skeleton, _scene.OnFloor(new Vector3(x, pit.FloorY, deck.MidZ + Jitter(i, 0, 327, 90f))),
                 431 + i, scale: 0.95f, loose: 24f);
        }
        Cluster(_skullB, _scene.OnFloor(new Vector3(pit.MidX - 300f, pit.FloorY, pit.MidZ - 400f)), 439, 5, 60f);
        Cluster(_debris, _scene.OnFloor(new Vector3(pit.MidX + 280f, pit.FloorY, pit.MidZ + 420f)), 443, 4);

        // One fire on the deck, at the FAR end — the sight line down the span
        // wants a destination, and the near end already has the shelf's light.
        Prop(_fireBasket, _scene.OnFloor(new Vector3(deck.X1 - 90f, deck.FloorY, deck.MidZ)), 449, yaw: 0f);
        _vignetteLights.Add((new Vector3(deck.X1 - 90f, deck.FloorY + 40f, deck.MidZ), Ember, 6f, 300f));
    }

    /// <summary>
    /// The Deep Gallery: the transition. The dressing thins westward as the stone
    /// changes, so the last third has nothing in it but orthostats — the props
    /// stopping is how a player feels they have left one civilisation's reach.
    /// </summary>
    private void GalleryDressing()
    {
        var s = Named("B5");
        Hero(_shrineCandles, new Vector3(s.X1 - 200f, s.FloorY, s.Z1 - 160f), Mathf.Pi, Rushlight, 7f, 300f);

        Prop(_coffinOld, _scene.OnFloor(new Vector3(s.X1 - 480f, s.FloorY, s.Z0 + 170f)), 457, yaw: 1.6f);
        Prop(_urnSquare, _scene.OnFloor(new Vector3(s.X1 - 620f, s.FloorY, s.Z1 - 150f)), 461);
        Prop(_graveWide, _scene.OnFloor(new Vector3(s.X1 - 900f, s.FloorY, s.Z1 - 170f)), 463, yaw: Mathf.Pi);

        LastCamp(_scene.OnFloor(new Vector3(s.X1 - 760f, s.FloorY, s.MidZ)), Mathf.Pi, 467);
        Cluster(_boneB, _scene.OnFloor(new Vector3(s.X1 - 1100f, s.FloorY, s.Z0 + 200f)), 479, 3, 34f);
    }

    /// <summary>
    /// The Cubiculum: one family's room, and the only tidy place left in the
    /// realm. Sealed since it was closed, so nothing here is broken — which is
    /// exactly why it is worth the fight to get in.
    /// </summary>
    private void CubiculumDressing()
    {
        var s = Named("B7");
        Hero(_coffinFine, new Vector3(s.MidX, s.FloorY, s.MidZ - 40f), Mathf.Pi / 2f, Tallow, 13f, 300f);
        Offering(_scene.OnFloor(new Vector3(s.X0 + 140f, s.FloorY, s.MidZ)), -Mathf.Pi / 2f, 487);
        // A lantern with no light in it is a prop of a lantern. This one hangs,
        // so its flame sits a little under the hook it swings from.
        var hook = new Vector3(s.MidX, s.FloorY + 300f, s.Z0 + 120f);
        Prop(_lanternHung, hook, 491, yaw: 0f);
        _vignetteLights.Add((hook - new Vector3(0f, 14f, 0f), Tallow, 5f, 260f));
        Prop(_urnRound, _scene.OnFloor(new Vector3(s.X1 - 120f, s.FloorY, s.Z0 + 120f)), 499);
        Prop(_skullCandle, _scene.OnFloor(new Vector3(s.X1 - 130f, s.FloorY, s.Z1 - 130f)), 503);
    }

    /// <summary>
    /// The Forecourt, where the boss is first seen through the trilithon. Almost
    /// nothing stands here on purpose: the hero is the VIEW, and a court full of
    /// props would compete with the one thing the whole descent has been for.
    /// </summary>
    private void ForecourtDressing()
    {
        var s = Named("B8");
        Hero(_postSkull, new Vector3(s.X1 - 130f, s.FloorY, s.Z0 + 150f), -0.8f, Witchlight, 16f, 520f);
        Prop(_postSkull, _scene.OnFloor(new Vector3(s.X1 - 130f, s.FloorY, s.Z1 - 150f)), 509, yaw: 0.8f);
        Cluster(_debris, _scene.OnFloor(new Vector3(s.X0 + 200f, s.FloorY, s.Z1 - 200f)), 521, 3);
    }

    /// <summary>
    /// The Chamber of the Wheel. The cist IS the hero and it is architecture, so
    /// this pass adds only what makes the ring read as a place people were
    /// brought to — and keeps every last thing off the plate itself.
    /// </summary>
    private void WheelDressing()
    {
        var s = Named("B9");
        // Offerings at the foot of the ring, between the orthostats, never inside
        // the cist's 400-unit plate where the fight happens.
        for (var i = 0; i < 6; i++)
        {
            var a = Mathf.Tau * (i + 0.25f) / 6f;
            var r = s.Width / 2f - Module * 2.4f;
            var at = _scene.OnFloor(new Vector3(s.MidX + Mathf.Cos(a) * r, s.FloorY,
                                                s.MidZ + Mathf.Sin(a) * r * (s.Depth / s.Width)));
            Prop(i % 2 == 0 ? _urnSquare : _urnRound, at, 523 + i, loose: 12f);
            Cluster(_skullB, at + new Vector3(0f, 0f, 40f), 541 + i, 3, 26f);
        }
        Prop(_ribcage, _scene.OnFloor(new Vector3(s.MidX - 300f, s.FloorY, s.MidZ + 260f)), 547);
        Prop(_skeleton, _scene.OnFloor(new Vector3(s.MidX + 330f, s.FloorY, s.MidZ - 240f)), 557, scale: 1.1f);
    }

    /// <summary>
    /// The corridors, STARVED. One lantern apiece, at the FAR end — the spec's
    /// rule, and the reason it works is that a corridor is a sight line: whatever
    /// you put at its end is what the player walks toward, and whatever you put
    /// along its length is what they walk past without seeing.
    /// </summary>
    private void Corridors()
    {
        // The salts are STATED rather than derived from the id. string.GetHashCode
        // is randomised per process in .NET, so a salt taken from one would place
        // these props somewhere new on every run — and a realm that regenerates
        // differently each time cannot be diffed, cached, or identity-matched.
        foreach (var (id, salt) in new[]
                 {
                     ("C1", 563), ("C2", 569), ("C3", 571), ("C5", 577),
                     ("C6", 587), ("C7", 593), ("B6", 599),
                 })
        {
            var s = Named(id);
            Prop(_lanternIron, _scene.OnFloor(new Vector3(s.X1 - 40f, s.EndY, s.MidZ)), salt, loose: 6f);
        }
    }
}

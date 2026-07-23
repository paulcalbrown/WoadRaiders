using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb;

namespace WoadRaiders.Client;

/// <summary>
/// The Sunken Crypt — a souterrain-and-charnel complex driven down into a
/// passage tomb that was old before anyone thought to bury Christians in it.
/// Each era cut into the last, and the deepest cut is not human work.
///
/// Built FROM docs/realms/crypt.md, which is normative: the chamber table, the
/// metrics, the camp mix and the budgets all live there, and this file is what
/// realises them. Read the spec before changing a number here.
///
///   the Broken Porch → the Minster nave → the Stair of the Dead → the Ossuary
///   → the Cut → THE FAULT, revealed → down the west stair, across the span →
///   the east landing → the deep stair → the Deep Gallery → the Descent →
///   the Forecourt (and the boss, seen) → the Passage → the Chamber of the Wheel
///
/// with two loops off it: a fall into the Fault, which two wall-hugging stairs
/// climb back out of, and a creepway to a sealed cubiculum that breaks through
/// into the Forecourt one way.
///
/// THREE ERAS, AND THEY DO NOT MIX (spec SPACE-003). Era III is mortared ashlar
/// and voussoired arches; Era II is drystone, lintels and corbels; Era I is
/// orthostats, trilithons and a corbelled dome. Every junction between them is a
/// visible BREACH — later work cut through earlier — never a blend. That rule is
/// the whole legibility of the realm: you always know how deep in time you are,
/// and never need a word of text to know it.
///
/// The other spine is the CAMERA. Ceiling height decides the shot
/// (CameraRig fits itself to the space), so compression and release here are not
/// a metaphor: a 140-high crawl pins the camera behind the raider's shoulder and
/// an unroofed hall lets it swing right out. Every beat is authored against that.
/// </summary>
public sealed partial class CryptDesign : IRealmDesign
{
    public string Name => "Crypt";

    // --- The metrics chart (spec §3). Nothing here invents a dimension. -----

    /// <summary>The realm's grid: one KayKit Dungeon tile at scale 20, so kit
    /// props drop in 1:1. Everything snaps to half of it.</summary>
    private const float Module = 80f;      // 3.33 m
    private const float Snap = Module / 2f;

    private const float StoreyIII = 80f;   // Era III wall height per storey
    private const float DoorWidthIII = 120f;
    private const float DoorHeadIII = 96f;
    private const float TrilithonWidth = 160f;
    private const float TrilithonHead = 128f;
    private const float WallThick = 40f;
    private const float DrystoneThick = 60f;
    private const float OrthostatThick = 80f;
    private const float PierHalf = 40f;
    private const float PierSpacing = 320f;
    private const float VaultBay = 320f;
    private const float TreadRise = 16f;   // < SimConstants.StepHeight (18)
    private const float LaneMin = 160f;
    private const float CreepwayWidth = 120f;

    /// <summary>
    /// The clearance above a raider at which the chase camera stops ducking,
    /// derived from CameraRig rather than chosen: at fit 0 the boom stands
    /// <c>OpenBoomLength(430) * sin(OpenPitchDegrees(40)) = 276</c> above the aim
    /// point, and the rig holds <c>CeilingClearance(25)</c> under the roof.
    /// Below this the camera tightens in toward the raider's shoulder.
    /// </summary>
    private const float CameraFreeAt = 302f;

    // CEILINGS. These were 140 / 240 / 480, named for the camera states they
    // produced - CRAWL pinned it, PRESS gave a tight fit, OPEN let it out -
    // because compression and release were meant to be the realm's whole spine.
    //
    // Played, that spine is unbearable. A camera pinned behind the shoulder in a
    // fight is not tension, it is a fight you cannot read, and 10% of this
    // realm's walkable floor did exactly that. Every roofed space now clears
    // CameraFreeAt wherever a raider can stand, and the names state height
    // rather than mood.
    //
    // A NOMINAL ceiling is not clearance. A groin vault's soffit hangs to about
    // 0.58 of nominal - a 280 bay measured 162 - and a corbelled roof steps in
    // from its walls. So these sit well above 302 and the result is MEASURED
    // (tools/MeasureHeadroom.cs) rather than trusted.
    private const float CeilLow = 460f;    // passages and creepways
    private const float CeilMid = 560f;    // chambers
    private const float CeilHigh = 820f;   // the halls that were always meant to soar
    private const float Unroofed = 0f;     // no roof at all; the camera is free by definition

    /// <summary>How high the boss's cist stands off the chamber floor. MUST stay
    /// under SimConstants.StepHeight (18) — a dais taller than a step is a plinth
    /// that entombs whoever stands on it, and every cell in the realm then reports
    /// as stranded.</summary>
    private const float DaisRise = 16f;

    /// <summary>Which age built a piece of stone. The number is the era's own
    /// name — Era I is the oldest — so the enum reads the way the fiction does.</summary>
    private enum Era
    {
        Cairn = 1,       // pre-Christian, megalithic
        Souterrain = 2,  // early medieval, drystone
        Minster = 3,     // latest, mortared ashlar
    }

    /// <summary>
    /// One space from the spec's chamber table, in WORLD units. Connectors carry
    /// a second floor height and ramp between the two.
    /// </summary>
    private readonly record struct Space(
        string Id, Era Era, float X0, float X1, float Z0, float Z1,
        float FloorY, float Ceiling, float FloorEndY = float.NaN)
    {
        public float MidX => (X0 + X1) * 0.5f;
        public float MidZ => (Z0 + Z1) * 0.5f;
        public float Width => X1 - X0;
        public float Depth => Z1 - Z0;
        public bool Descends => !float.IsNaN(FloorEndY) && !Mathf.IsEqualApprox(FloorEndY, FloorY);
        public float EndY => float.IsNaN(FloorEndY) ? FloorY : FloorEndY;
    }

    private RealmScene _scene = null!;
    private readonly List<Space> _spaces = new();
    private readonly Dictionary<Era, Material> _stone = new();
    private readonly Dictionary<Era, Material> _ground = new();
    private Material _soffit = null!;
    private Material _bone = null!;
    private Material _quartz = null!;

    /// <summary>The space recorded under this id — how the lighting and the cast
    /// walk the realm instead of restating its coordinates.</summary>
    private Space Named(string id) => _spaces.Find(s => s.Id == id);

    private Material Stone(Era era) => _stone[era];

    /// <summary>What an era walks on. A floor is worn where a wall is not.</summary>
    private Material Ground(Era era) => _ground[era];

    /// <summary>Half a wall's thickness, per era — how far the fitted walls inset
    /// so their corners butt rather than overlap.</summary>
    private static float WallHalf(Era era) => era switch
    {
        Era.Minster => WallThick / 2f,
        Era.Souterrain => DrystoneThick / 2f,
        _ => OrthostatThick / 2f,
    };

    public RealmScene Build()
    {
        _scene = new RealmScene();
        Surfaces();

        // ---------------------------------------------------------- Era III
        // The Minster: a burial church driven into the hillside. Human
        // proportions throughout — 4 m doors, 3.3 m storeys — because humans
        // built it and the realm's scale has to start somewhere honest.
        Room("B1", Era.Minster, 0, 720, 1840, 2560, 0, CeilMid, doorEast: 2200);
        Corridor("C1", Era.Minster, 720, 800, 2120, 2280, 0, CeilLow, alongZ: false);
        Nave();
        Corridor("C2", Era.Minster, 3040, 3600, 2080, 2320, 0, CeilLow, alongZ: false, floorAtEnd: -160);

        // ----------------------------------------------------------- Era II
        // The souterrain the Minster's builders broke into, widened into a
        // charnel. Deliberately LOW for its size: 47 × 40 m under a 10 m
        // corbelled roof is oppressive, and the camera says so.
        Ossuary();
        Corridor("C3", Era.Souterrain, 5280, 5520, 2080, 2240, -160, CeilLow, alongZ: false);

        Fault();

        Corridor("C5", Era.Souterrain, 7040, 7200, 1440, 2320, -400, CeilMid, alongZ: true);
        Corridor("C5a", Era.Souterrain, 7040, 7200, 2320, 2880, -400, CeilLow, alongZ: true, floorAtEnd: -560);
        DeepGallery();
        // −640 at the CUBICULUM end (x0), −560 at the gallery end (x1).
        // The gallery enters the creepway at x=5880, NOT at the 5760 the chamber
        // table said. A corridor that descends meets a side door at whatever
        // height its ramp happens to be at that point, and at 5760 the creepway
        // sits 17 below the gallery floor against a StepHeight of 18. It worked,
        // by one unit, until the ceilings moved and the tread quantisation with
        // them. At 5880 the ramp is within 3 of the gallery, which is a threshold
        // rather than a coin toss.
        Corridor("B6", Era.Souterrain, 4880, 5920, 3520, 3680, -640, CeilLow, alongZ: false, floorAtEnd: -560,
                 sideDoorAt: 5880);
        Room("B7", Era.Souterrain, 4400, 4880, 3440, 3840, -640, CeilMid, doorEast: 3600, doorNorth: 4560);
        // Travel is EAST-WEST here even though the space is deeper than it is wide.
        Corridor("C6", Era.Souterrain, 4720, 4960, 3040, 3360, -720, CeilLow, alongZ: false, floorAtEnd: -560);

        // ------------------------------------------------------------ Era I
        // The cairn the souterrain's diggers broke into. Megalithic: nobody
        // with hands and rope built this at this size, which is the payoff the
        // whole descent has been earning.
        Room("B8", Era.Cairn, 4160, 4720, 2960, 3600, -720, CeilHigh,
             doorEast: 3200, doorWest: 3280, doorSouth: 4560);
        Corridor("C7", Era.Cairn, 3520, 4160, 3200, 3360, -880, CeilLow, alongZ: false, floorAtEnd: -720);
        Wheel();

        // The dead, then the living, then the light. Burials come last of the
        // structure because the state a wall wears depends on where it stands on
        // the route, which no room knows while it is still being laid.
        Burials();
        Dressing();
        Surface();
        Cast();
        Gloom();
        return _scene;
    }

    // ------------------------------------------------------------ the spaces

    /// <summary>
    /// The Minster nave: an arcade of piers down a tall central vessel with
    /// groin-vaulted aisles either side. The aisles are PRESS and the nave is
    /// OPEN, so the camera lifts as a raider steps out of the flanks into the
    /// middle — the first release the realm offers, and the reason the fight
    /// here reads as a hall rather than a corridor.
    /// </summary>
    private void Nave()
    {
        var space = Room("B2", Era.Minster, 800, 3040, 1360, 2960, 0, Unroofed,
                         doorWest: 2200, doorEast: 2200);

        // Aisles: vaulted low against the outer walls.
        foreach (var (z0, z1) in new[] { (1360f, 1760f), (2560f, 2960f) })
            Roof(Era.Minster, space with { Z0 = z0, Z1 = z1 }, CeilHigh);

        // The arcade: pairs of piers either side of the centre line, never on
        // it. A pillared hall has aisles — and the route walker runs the middle,
        // so a pier standing in it stalls the build outright.
        for (var x = space.X0 + PierSpacing; x <= space.X1 - PierSpacing; x += PierSpacing)
            foreach (var z in new[] { 1760f, 2560f })
            {
                Place(Pier(StoreyIII * 2.2f), new Vector3(x, 0, z), 0f, Stone(Era.Minster));
                Standing(new Vector3(x, 0, z), PierHalf + SimConstants.CharacterRadius + 20f);
            }
    }

    /// <summary>
    /// The Ossuary: a drystone charnel whose walls ARE the burial. Loculus banks
    /// tier the long walls, piers on a wide grid carry a corbelled roof only ten
    /// metres up over a room forty across — the realm's biggest fight in its
    /// most oppressive space.
    /// </summary>
    private void Ossuary()
    {
        Room("B3", Era.Souterrain, 3600, 5280, 1360, 2880, -160, CeilMid,
             doorWest: 2200, doorEast: 2160);

        // The burial itself — banks, revetment and the one arch — is laid by
        // Burials(), after every space exists, because the states it grades
        // depend on where a room sits on the route rather than on its walls.

        var space = Named("B3");
        for (var x = space.X0 + PierSpacing; x <= space.X1 - PierSpacing; x += PierSpacing)
            for (var z = space.Z0 + PierSpacing; z <= space.Z1 - PierSpacing; z += PierSpacing)
            {
                Place(Pier(CeilMid), new Vector3(x, -160, z), 0f, Stone(Era.Souterrain));
                Standing(new Vector3(x, -160, z), PierHalf + SimConstants.CharacterRadius + 20f);
            }
    }

    /// <summary>
    /// The Fault — a natural cut the builders bridged, and the realm's one
    /// STACKED space, where "the floor here" has two honest answers.
    ///
    /// The order matters more than the geometry. The Cut arrives at the OSSUARY's
    /// own level and opens onto a shelf jutting over a drop of 720 with nothing
    /// overhead: crawl to release in one step, the whole chasm and the span
    /// below it in frame at once. Only THEN does the stair take you down.
    /// Descending after the reveal is what makes the reveal mean anything.
    /// </summary>
    private void Fault()
    {
        // The pit floor and the rock it is cut into. West and east strips are
        // SOLID (the shelf and landings sit on them), so only three small
        // footprints ever stack over the void.
        // The pit stops at z 2880, where the Deep Gallery begins. The spec's
        // table overlapped them by 160, which put this chamber's south wall
        // straight through the gallery's floor — an unroofed pit and a roofed
        // gallery cannot share plan area without one of them cutting the other.
        var pit = Room("B4", Era.Souterrain, 5680, 7040, 1120, 2880, -880, Unroofed);
        // The cut rock, but only up to DECK level. Above −400 the fault is open
        // air: carrying the wall higher would run it straight through the
        // landings' doorways and seal the climbs out, which is precisely what it
        // did — the stairs reached the top and met stone.
        for (var y = -880f; y < -400f; y += 240f)
        {
            WallRun(Era.Souterrain, pit.X0, pit.Z0, pit.X0, pit.Z1, y, 240f);
            WallRun(Era.Souterrain, pit.X1, pit.Z0, pit.X1, pit.Z1, y, 240f);
            WallRun(Era.Souterrain, pit.X0, pit.Z1, pit.X1, pit.Z1, y, 240f);
        }

        // The shelf: a ledge at the Ossuary's level, jutting 80 over the drop.
        Ledge("B4a", Era.Souterrain, 5520, 5760, 1920, 2240, -160);
        // Down the west wall, along it rather than across the pit.
        // High end at the SHELF (z 1920), low at the landing (z 1360).
        Stair("C4", Era.Souterrain, 5520, 5680, 1920, 1360, -160, -400);
        Ledge("B4b", Era.Souterrain, 5520, 5920, 1120, 1440, -400);

        // The span: the Minster's own masonry, thrown across a hole older than
        // it. The one place Era III appears below its own storey.
        var deck = Ledge("B4c", Era.Minster, 5920, 7040, 1200, 1360, -400);
        foreach (var z in new[] { deck.Z0 + 8f, deck.Z1 - 8f })
            for (var x = deck.X0; x < deck.X1; x += Module)
                Place(Kerbstone((int)(x / Module) % 5), new Vector3(x + Snap, -400, z), 0f,
                      Stone(Era.Minster));

        Room("B4d", Era.Souterrain, 7040, 7200, 1120, 1440, -400, CeilMid,
             doorWest: 1280, doorSouth: 7120);

        // A way up at EACH end of the span, both hugging a wall. A flight rising
        // 480 is a wall for most of its length: one struck diagonally across the
        // floor would not merely climb, it would partition the pit and pin a
        // margin against the stone that can be walked forever and left never.
        // That is exactly the defect the first Crypt shipped.
        Stair("B4w", Era.Souterrain, 5700, 5820, 2800, 1440, -880, -400);
        // Tops out ON the deck rather than beside it: a stair that merely
        // touches its landing is a stair the navmesh may decline to join.
        Stair("B4e", Era.Souterrain, 6920, 7040, 2600, 1300, -880, -400);
    }

    /// <summary>
    /// The Deep Gallery: the transition made visible. Drystone at its east end,
    /// and at the west the first orthostats of something far older, with a monk's
    /// wall built up against a stone that was already there.
    /// </summary>
    private void DeepGallery()
    {
        var space = Room("B5", Era.Souterrain, 4960, 7200, 2880, 3520, -560, CeilMid,
                         doorNorth: 7120, doorWest: 3200, doorSouth: 5880);

        // The stone changes as you walk west: drystone bays give way to
        // orthostats over the last third, and nothing announces it but the wall.
        var breach = Mathf.Lerp(space.X0, space.X1, 0.34f);
        for (var x = space.X0 + Snap; x < breach; x += Module)
            foreach (var z in new[] { space.Z0 + 30f, space.Z1 - 30f })
                Place(Orthostat(240f, (int)(x / Module) % 3), new Vector3(x, -560, z), 0f, Stone(Era.Cairn));
    }

    /// <summary>
    /// The Chamber of the Wheel: a cruciform passage-tomb chamber under a
    /// corbelled beehive dome, with three shallow recesses and the cist at its
    /// centre. Deliberately over-scaled — 53 × 37 m, above what co-op arena
    /// guidance gives for eight players — because the fiction's whole payoff is
    /// that this is not human work and must not look as though it could be.
    ///
    /// The recesses are kept SHALLOW (160) and one step up, so a raider standing
    /// in one still sees the middle: a deep alcove under a chase camera is a
    /// place a player cannot read the fight from.
    /// </summary>
    private void Wheel()
    {
        // Grown to out-scale the Fault (the realm's other great void): 2320 x
        // 1760 against the pit's 1360 x 1760, so the climax reads as the largest
        // thing in the descent, not a room a third its size. It spreads WEST and
        // SOUTH into clear rock at -880 — the nearest deep structures (the
        // Cubiculum, the creepway) all lie east of x 4400. The entrance stays at
        // the east wall, so a raider now crosses the whole chamber to the cist:
        // the boss is no longer framed in the doorway but stands across a hall,
        // which is the trade the size is worth.
        var space = Room("B9", Era.Cairn, 1200, 3520, 2880, 4640, -880, Unroofed, doorEast: 3280);

        // An orthostat ring inside the walls — the chamber proper, standing
        // within the cairn's mass. TWELVE, not more: a ring is a colonnade only
        // if a raider can walk between its stones, and at eighteen the gaps came
        // to 119 before the navmesh eroded 14 off each side. Stones are also
        // held clear of the entry axis, so the way in is a way in.
        const int Stones = 12;
        for (var i = 0; i < Stones; i++)
        {
            var a = Mathf.Tau * (i + 0.5f) / Stones; // +0.5 keeps one off the door's line
            var x = space.MidX + Mathf.Cos(a) * (space.Width / 2f - Module * 1.5f);
            var z = space.MidZ + Mathf.Sin(a) * (space.Depth / 2f - Module * 1.5f);
            Place(Orthostat(300f, i % 3), new Vector3(x, -880, z), -a, Stone(Era.Cairn));
        }

        // The corbelled dome over it, at the spec's crown of 1100 — taller than
        // the old 900 to suit the larger span, and still clearing the surface
        // grass at 820 (its crown tops out near 320).
        Roof(Era.Cairn, space, 1100f);

        // The cist: a low plate the raider walks onto, never a plinth.
        BoxKit.Floor(_scene, Box(space.MidX - 200f, -880, space.MidZ - 200f,
                                 space.MidX + 200f, -880 + DaisRise, space.MidZ + 200f), _quartz, "TheCist");

        // Kerbstones mark the THRESHOLD of the monument, so they flank the way
        // into the passage rather than ringing anything. Twice now a ring has
        // sealed a room: at 14 stones the gaps came to 124, and the navmesh eats
        // 14 off each side of a lane before a raider of radius 14 tries it. A
        // ring inside a room a fight happens in is a wall with hopeful holes.
        var court = Named("B8");
        foreach (var side in new[] { -1f, 1f })
            for (var i = 0; i < 2; i++)
                Place(Kerbstone(i % 5),
                      new Vector3(court.X0 + Snap + i * Module, court.FloorY,
                                  court.MidZ + side * (TrilithonWidth + 60f + i * 40f)),
                      0f, Stone(Era.Cairn));
    }

    // -------------------------------------------------------- the vocabulary

    private static Aabb Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
        new(new System.Numerics.Vector3(x0, y0, z0), new System.Numerics.Vector3(x1, y1, z1));

    private void Surfaces()
    {
        // One palette per era, and the difference is VALUE and ROUGHNESS rather
        // than hue: at ~1900 K almost no colour survives, so dressed-versus-rough
        // and dry-versus-damp are what the eye actually has to read.
        // WALLS — photographed stone (LOOK-001), one vendored CC0 set per era.
        // The tint survives the swap: it is what keeps the eras apart in VALUE
        // when almost no colour survives 1900 K firelight, and three different
        // photographs of grey rock do not separate on their own.
        _stone[Era.Minster] = StoneSurfaces.Photographic("ashlar_wall", seed: 1301,
                                                         grain: 260f, relief: 1.0f,
                                                         tint: new Color(0.78f, 0.75f, 0.70f));
        _stone[Era.Souterrain] = StoneSurfaces.Photographic("drystone", seed: 1607,
                                                            grain: 300f, relief: 2.0f,
                                                            tint: new Color(0.62f, 0.61f, 0.58f));
        _stone[Era.Cairn] = StoneSurfaces.Photographic("orthostat", seed: 1913,
                                                       grain: 420f, relief: 2.4f,
                                                       tint: new Color(0.58f, 0.55f, 0.53f));

        // FLOORS and SOFFITS are their own surfaces, because they are in reality:
        // a floor is worn where a wall is not, and a vault's underside is the one
        // face in a church nobody ever touched. Same triplanar projection, so a
        // wall meeting a floor keeps its texel density across the joint.
        _ground[Era.Minster] = StoneSurfaces.Photographic("ashlar_floor", seed: 1319,
                                                          grain: 300f, relief: 0.8f,
                                                          tint: new Color(0.74f, 0.72f, 0.68f));
        _ground[Era.Souterrain] = StoneSurfaces.Photographic("flagstone", seed: 1613,
                                                             grain: 340f, relief: 1.4f,
                                                             tint: new Color(0.60f, 0.59f, 0.57f));
        _ground[Era.Cairn] = StoneSurfaces.Photographic("cairn_rubble", seed: 1931,
                                                        grain: 200f, relief: 1.8f,
                                                        tint: new Color(0.56f, 0.54f, 0.51f));
        _soffit = StoneSurfaces.Photographic("vault_soffit", seed: 1327,
                                             grain: 240f, relief: 0.7f,
                                             tint: new Color(0.80f, 0.78f, 0.74f));
        _bone = StoneSurfaces.Cut(new Color(0.60f, 0.57f, 0.49f), seed: 2311,
                                  roughness: 0.80f, relief: 1.2f, grain: 90f);
        _quartz = StoneSurfaces.Cut(new Color(0.80f, 0.82f, 0.86f), seed: 2617,
                                    roughness: 0.34f, relief: 0.6f, grain: 120f);
    }

    /// <summary>
    /// A rectangular chamber: a floor slab, four walls in the era's grammar with
    /// optional openings, and a roof (or none, where the dark closes it).
    /// Recorded under its id for the lighting and the cast to find.
    /// </summary>
    private Space Room(string id, Era era, float x0, float x1, float z0, float z1, float floorY, float ceiling,
                       float? doorNorth = null, float? doorSouth = null,
                       float? doorWest = null, float? doorEast = null)
    {
        var space = new Space(id, era, x0, x1, z0, z1, floorY, ceiling);
        _spaces.Add(space);

        BoxKit.Floor(_scene, Box(x0, floorY - 40f, z0, x1, floorY, z1), Ground(era), $"{id}_Floor");
        var height = ceiling > 0f ? ceiling : StoreyIII * 3f;
        var width = era == Era.Cairn ? TrilithonWidth : DoorWidthIII;

        // Corners INTERLOCK instead of overlapping. Four full-length walls put
        // two stones in every corner volume — and their coincidentally coplanar
        // faces are the realm's worst z-fighting, the stone visibly trading
        // places as the camera moves. So the north and south walls run the full
        // width and OWN the corners, and the east and west walls fit BETWEEN
        // them, inset by the wall's half-thickness so they butt against the inner
        // face rather than pushing stone through it.
        var h = WallHalf(era);
        WallRun(era, x0, z0, x1, z0, floorY, height, doorNorth, width);
        WallRun(era, x0, z1, x1, z1, floorY, height, doorSouth, width);
        WallRun(era, x0, z0 + h, x0, z1 - h, floorY, height, doorWest, width);
        WallRun(era, x1, z0 + h, x1, z1 - h, floorY, height, doorEast, width);
        Roof(era, space, ceiling);
        return space;
    }

    /// <summary>
    /// A walled passage, open at both ends, level or descending.
    ///
    /// <paramref name="alongZ"/> is STATED, never inferred from which dimension
    /// is longer. A short wide threshold is wider than it is deep, so guessing
    /// would run its walls across the way through — and the symptom is not a
    /// broken wall, it is a validator reporting a whole wing of the realm as
    /// unreachable, a long way from the cause.
    /// </summary>
    /// <remarks>
    /// <paramref name="floorAtStart"/> is the height at the x0/z0 CORNER and
    /// <paramref name="floorAtEnd"/> at x1/z1 — NOT in the order a raider walks
    /// them. Four of these were written route-first and so descended the wrong
    /// way, each meeting its chamber 160 out; the realm still built, still
    /// looked right, and reported a whole wing unreachable.
    /// </remarks>
    /// <param name="sideDoorAt">
    /// Where another space's doorway meets this corridor's SIDE, if one does.
    ///
    /// A corridor builds side walls and leaves its ENDS open, which is right when
    /// it is entered end-on and catastrophic when it is not: the creepway runs
    /// along z=3520 and the Deep Gallery's south door is at z=3520, so the
    /// corridor's own north wall sealed the doorway it exists to serve. The
    /// Cubiculum became an island — every camp in it unreachable — and the only
    /// reason the realm had ever validated is that a raider could squeeze across
    /// the three stair treads that happened to top out within a step of the
    /// gallery floor. That was luck, and raising the ceilings spent it.
    /// </param>
    private Space Corridor(string id, Era era, float x0, float x1, float z0, float z1, float floorAtStart,
                           float ceiling, bool alongZ, float? floorAtEnd = null,
                           float? sideDoorAt = null, float sideDoorWidth = 200f)
    {
        var space = new Space(id, era, x0, x1, z0, z1, floorAtStart, ceiling, floorAtEnd ?? float.NaN);
        _spaces.Add(space);

        if (space.Descends)
        {
            var from = alongZ ? new Vector3(space.MidX, floorAtStart, z0) : new Vector3(x0, floorAtStart, space.MidZ);
            var to = alongZ ? new Vector3(space.MidX, space.EndY, z1) : new Vector3(x1, space.EndY, space.MidZ);
            BoxKit.Stairs(_scene, from, to, alongZ ? x1 - x0 : z1 - z0, Stone(era));
        }
        else
        {
            BoxKit.Floor(_scene, Box(x0, floorAtStart - 40f, z0, x1, floorAtStart, z1), Ground(era), $"{id}_Floor");
        }

        // Side walls; the ends are the doorways it joins end-on. A corridor that
        // is ALSO entered from the side says so, and that wall is split around it
        // exactly as a room's is.
        var low = Mathf.Min(floorAtStart, space.EndY);
        var wall = ceiling + (floorAtStart - low);

        // Where the ramp actually is under the side door, so the opening's head
        // is measured from the floor a raider stands on rather than from the
        // corridor's lowest tread.
        float? doorFloor = null;
        if (sideDoorAt is { } door)
        {
            var t = alongZ
                ? Mathf.InverseLerp(z0, z1, door)
                : Mathf.InverseLerp(x0, x1, door);
            doorFloor = Mathf.Lerp(floorAtStart, space.EndY, Mathf.Clamp(t, 0f, 1f));
        }

        var doorW = sideDoorAt is null ? 0f : sideDoorWidth;
        if (alongZ)
        {
            WallRun(era, x0, z0, x0, z1, low, wall, sideDoorAt, doorW, doorFloor);
            WallRun(era, x1, z0, x1, z1, low, wall);
        }
        else
        {
            WallRun(era, x0, z0, x1, z0, low, wall, sideDoorAt, doorW, doorFloor);
            WallRun(era, x0, z1, x1, z1, low, wall);
        }
        return space;
    }

    /// <summary>A shelf or deck standing over open air — floor only, no walls,
    /// because what surrounds it is the drop.</summary>
    private Space Ledge(string id, Era era, float x0, float x1, float z0, float z1, float floorY)
    {
        var space = new Space(id, era, x0, x1, z0, z1, floorY, Unroofed);
        _spaces.Add(space);
        BoxKit.Floor(_scene, Box(x0, floorY - 40f, z0, x1, floorY, z1), Ground(era), $"{id}_Floor");
        return space;
    }

    /// <summary>A flight of treads, each rising less than a step so feet flow up
    /// it. Runs from one point to another — lay it ALONG a wall, never across a
    /// floor.</summary>
    private Space Stair(string id, Era era, float x0, float x1, float z0, float z1, float fromY, float toY)
    {
        var space = new Space(id, era, Mathf.Min(x0, x1), Mathf.Max(x0, x1),
                              Mathf.Min(z0, z1), Mathf.Max(z0, z1), fromY, Unroofed, toY);
        _spaces.Add(space);
        BoxKit.Stairs(_scene, new Vector3((x0 + x1) / 2f, fromY, z0),
                      new Vector3((x0 + x1) / 2f, toY, z1), Mathf.Abs(x1 - x0), Stone(era));
        return space;
    }
}

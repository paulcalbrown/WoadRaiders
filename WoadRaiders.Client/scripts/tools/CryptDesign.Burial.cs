using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// How the Crypt buries people — four forms, and the grammar is meant to be
/// learnable in one room (spec LOOK-015):
///
///   LOCULUS     a wall niche in tiers of four. The commons: most of the dead
///               are here, and the wall reads as a society rather than as
///               decoration.
///   ARCOSOLIUM  an arched recess over a chest. The elite — one per chamber at
///               most, and each one is a landmark you navigate by.
///   FORMA       a trench in the floor. The pauper: no niche, no arch, a slot
///               and a lid, and often not even the lid any more.
///   CUBICULUM   a whole private room (B7), which is architecture rather than a
///               piece, and so is built by the plan.
///
/// The wealth is put where the loot is, which is what makes the grammar worth
/// learning instead of merely present.
///
/// LOOK-016 is the reason a bank takes a STATE WORD rather than deciding its own
/// niches: the states have to tell a story across a room, and a piece that hashes
/// its own has no way to know it stands at the robbed end of a hall. The word is
/// four characters, bottom tier first, and the caller grades it along the line a
/// previous expedition took.
/// </summary>
public sealed partial class CryptDesign
{
    // The yaw that turns a piece's OPENING into the room. Every burial form here
    // is built opening toward local −Z, so which wall it stands on decides its
    // yaw — and getting this backwards is invisible in the build log and obvious
    // in the game: the Ossuary shipped forty banks facing into the rock, showing
    // the player the flat back of every niche in the realm.
    private static readonly float OnNorthWall = Mathf.Pi;      // at Z0, looking +Z
    private static readonly float OnSouthWall = 0f;            // at Z1, looking −Z
    private static readonly float OnWestWall = -Mathf.Pi / 2f; // at X0, looking +X
    private static readonly float OnEastWall = Mathf.Pi / 2f;  // at X1, looking −X

    /// <summary>A niche's state. The characters are what a bank's word is spelt
    /// from, and they are single letters so a word reads at a glance in the
    /// call that places it: <c>"RRCS"</c> is robbed, robbed, cracked, sealed.</summary>
    private const string Sealed = "S", Cracked = "C", Robbed = "R", Open = "O";

    /// <summary>
    /// The states a bank wears, in order of how thoroughly it has been worked
    /// over. Robbery climbs from the FLOOR — the bottom tier is the one a person
    /// can reach standing, and the top tier is still sealed in every word here
    /// because nobody brought a ladder. That detail is the whole reason this is
    /// a table and not a hash: it is true, and it is legible.
    /// </summary>
    private static readonly string[] Ransack =
    {
        "SSSS", // untouched — nobody has been this way
        "CSSS", // one slab split, perhaps only by the weight above it
        "RCSS", // the first one taken
        "RRCS", // working along the wall now
        "ROCS", // and not bothering to close them
        "RRRS", // everything within reach, and the top tier out of it
    };

    /// <summary>
    /// The bank at this point along a wall, graded by how far the previous
    /// expedition had got. <paramref name="pressure"/> runs 0 at the way IN to 1
    /// at the far end — sealed where the raiders enter, so the trail of prior
    /// human action leads them the way they are already going.
    /// </summary>
    private static string Ransacked(float pressure, int salt)
    {
        // The jitter is what stops the gradient reading as a dial. A wall that
        // ramps perfectly is a ramp; a wall where one bank held out two past its
        // neighbours is a place people worked.
        //
        // The −0.7 is load-bearing. Banks start a full module in from the wall, so
        // the lowest pressure any of them sees is already ~0.05 — and a gradient
        // rounded from that never once produced UNTOUCHED. The room then opened on
        // wall that was already being robbed, which is the one thing the gradient
        // exists to say it is not. Shifting the ramp below zero lets the near end
        // clamp to sealed; the far end clamps anyway.
        var step = pressure * (Ransack.Length - 0.2f) - 0.7f + Jitter(salt, 0, 401, 0.9f);
        return Ransack[Mathf.Clamp(Mathf.RoundToInt(step), 0, Ransack.Length - 1)];
    }

    /// <summary>
    /// Lay the realm's dead. Run after every space exists, because how ransacked
    /// a wall is depends on how far along the route it stands, and a room cannot
    /// know that while it is still being built.
    /// </summary>
    private void Burials()
    {
        Ossuary_Burials();
        Nave_Burials();
        Cubiculum_Burials();
        Gallery_Burials();
    }

    /// <summary>
    /// The Ossuary is where the grammar is taught, so all four forms appear in
    /// it: banks down the long walls, revetment on the short ones, one arch, and
    /// the states graded west→east along the way the raiders are already walking.
    /// </summary>
    private void Ossuary_Burials()
    {
        var space = Named("B3");
        // Banks stop well short of the raised ceiling: a wall of niches four
        // tiers high is a society, and one stretched to nine metres is a
        // filing cabinet. The room got taller; the burials did not.
        var height = 210f;

        // The one important grave in a room full of anonymous ones. It stands on
        // the south wall at the room's centre, which is where the eye lands from
        // the west door — and the banks step around it.
        var arch = space.MidX;
        Place(Arcosolium(180f, 210f), new Vector3(arch, space.FloorY, space.Z1 - 36f),
              OnSouthWall, Stone(Era.Minster));

        for (var x = space.X0 + Module; x <= space.X1 - Module; x += Module)
        {
            // Sealed at the west door, systematically robbed eastward: the line a
            // previous expedition took, told without a word of script.
            var word = Ransacked((x - space.X0) / space.Width, (int)(x / Module));
            Place(LoculusBank(height, word), new Vector3(x, space.FloorY, space.Z0 + 34f),
                  OnNorthWall, _bone);
            if (Mathf.Abs(x - arch) > 120f)
                Place(LoculusBank(height, word), new Vector3(x, space.FloorY, space.Z1 - 34f),
                      OnSouthWall, _bone);
        }

        // The short walls are revetment rather than niches — the charnel proper,
        // where the count stopped being kept. Both carry a door, and a facing
        // built across one would wall the room shut.
        foreach (var (x, yaw, door) in new[]
                 {
                     (space.X0 + 16f, OnWestWall, 2200f),
                     (space.X1 - 16f, OnEastWall, 2160f),
                 })
            for (var z = space.Z0 + Module; z <= space.Z1 - Module; z += Module)
                if (Mathf.Abs(z - door) > 170f)
                    Place(BoneRevetment(200f), new Vector3(x, space.FloorY, z), yaw, _bone);
    }

    /// <summary>
    /// Under the Minster's own floor: formae, the pauper's trench. Historically
    /// exact — the poor went under the flags of the church the rich were walled
    /// into — and it costs the nave nothing, because they lie flat.
    ///
    /// They are kept to the AISLES. A trench on the centre line is a trench the
    /// route walker has to cross, and the realm's build stops when it cannot.
    /// </summary>
    private void Nave_Burials()
    {
        var space = Named("B2");
        var i = 0;
        foreach (var z in new[] { space.Z0 + 200f, space.Z1 - 200f })
            for (var x = space.X0 + 340f; x < space.X1 - 300f; x += 420f, i++)
                Place(Forma(i % 3), new Vector3(x, space.FloorY, z), 0f,
                      Ground(Era.Minster), floor: true);
    }

    /// <summary>
    /// The Cubiculum: one family's own room, sealed, optional, and paid for by
    /// the fight in it. Two arcosolia and a short bank — which is what a cubiculum
    /// IS, and why the loot is here rather than in the charnel next door.
    /// </summary>
    private void Cubiculum_Burials()
    {
        var space = Named("B7");
        Place(Arcosolium(170f, 200f), new Vector3(space.X0 + 30f, space.FloorY, space.MidZ),
              OnWestWall, Stone(Era.Minster));
        Place(Arcosolium(170f, 200f), new Vector3(space.MidX, space.FloorY, space.Z1 - 30f),
              OnSouthWall, Stone(Era.Minster));
        // Whoever was left over went in the wall by the door, unsealed.
        foreach (var x in new[] { space.X0 + 100f, space.X0 + 180f })
            Place(LoculusBank(200f, "OROS"), new Vector3(x, space.FloorY, space.Z0 + 34f),
                  OnNorthWall, _bone);
    }

    /// <summary>
    /// The Deep Gallery, where the souterrain runs out of people who cared. A few
    /// trenches, no niches, and the revetment stops well before the orthostats —
    /// nobody stacked bones against a stone they did not put there.
    /// </summary>
    private void Gallery_Burials()
    {
        var space = Named("B5");
        var i = 0;
        for (var x = space.X1 - 300f; x > space.X0 + 900f; x -= 380f, i++)
            Place(Forma(i % 3 == 0 ? 2 : i % 3), new Vector3(x, space.FloorY, space.Z0 + 190f),
                  0f, Ground(Era.Souterrain), floor: true);

        // The east wall is the gallery's one blind end — no door on it, so the
        // facing may run the whole way.
        for (var z = space.Z0 + Module; z <= space.Z1 - Module; z += Module)
            Place(BoneRevetment(180f), new Vector3(space.X1 - 16f, space.FloorY, z),
                  OnEastWall, _bone);
    }

    /// <summary>
    /// A bank of loculi — the burial niches of a charnel gallery, cut in tiers
    /// into the wall face. Roman catacomb proportions: each niche one body long
    /// and shallow, stacked four high.
    ///
    /// The niche is the VOID, so the stone is built around it; and each tier then
    /// wears the state its word gives it.
    /// </summary>
    private ArrayMesh LoculusBank(float height, string word) =>
        Piece($"loculi_{height:0}_{word}", tool =>
        {
            const float half = Module / 2f;
            var t = DrystoneThick;
            var tiers = word.Length;
            var pitch = (height - 10f) / tiers;
            var face = -t / 2f; // the side that looks into the room

            for (var tier = 0; tier < tiers; tier++)
            {
                var y = 6f + tier * pitch;
                var head = y + pitch - 9f;
                Stone(tool, -half, head, face, half, y + pitch, t / 2f, 0.71f);  // the shelf over it
                Stone(tool, -half, y, face, -half + 7f, head, t / 2f, 0.69f);    // jamb
                Stone(tool, half - 7f, y, face, half, head, t / 2f, 0.69f);      // jamb
                Stone(tool, -half, y, 2f, half, head, t / 2f, 0.60f);            // the back of the niche

                switch (word[tier].ToString())
                {
                    case Sealed:
                        // An intact slab, flush, with an incised border standing
                        // a little proud — the one surface down here anybody
                        // bothered to dress, and it catches the flame edge-on.
                        Stone(tool, -half + 7f, y, face, half - 7f, head, face + 5f,
                              0.86f + Jitter(tier, 1, 55, 0.04f));
                        Stone(tool, -half + 11f, y + 4f, face - 2f, half - 11f, head - 4f, face,
                              0.90f);
                        break;

                    case Cracked:
                        // Split, and the halves no longer meet: a dark gap the
                        // width of a finger, and the lower half has slipped and
                        // rotated out of plane. A crack painted on would not
                        // shadow; this one does.
                        var split = Jitter(tier, 2, 57, 8f);
                        Stone(tool, -half + 7f, y, face, split - 2f, head, face + 5f, 0.83f);
                        Stone(tool, split + 3f, y - 2f, face - 3f, half - 7f, head - 5f, face + 4f, 0.81f);
                        break;

                    case Robbed:
                        // Nothing left in the niche and the slab in pieces at the
                        // foot of the bank — which is the only part of this that
                        // a player reads at speed, so it is the part that is
                        // built. Two fragments, canted, lying clear of the wall.
                        for (var f = 0; f < 2; f++)
                        {
                            var x = -half + 14f + f * 26f + Jitter(tier, f, 59, 9f);
                            var out0 = face - 16f - Hash(tier, f, 61) * 12f;
                            Stone(tool, x, 6f, out0, x + 20f, 12f + Jitter(tier, f, 63, 3f), out0 + 13f,
                                  0.79f + Jitter(tier, f, 65, 0.07f));
                        }
                        break;

                    case Open:
                        // Never sealed, or opened so long ago it makes no odds.
                        // What is left lies where it was laid: three pale forms
                        // in the dark of the niche, seen only when a flame is
                        // near enough, which is the entire effect.
                        for (var b = 0; b < 3; b++)
                        {
                            var x = -half + 12f + b * 19f;
                            var by = y + 3f + Jitter(tier, b, 67, 1.5f);
                            Stone(tool, x, by, face + 9f, x + 15f, by + 5f, face + 9f + 22f,
                                  0.94f + Jitter(tier, b, 69, 0.04f));
                        }
                        break;
                }
            }

            Stone(tool, -half, 0, face, half, 6f, t / 2f, 0.66f); // the floor course under the bank
            return "";
        });

    /// <summary>
    /// An arcosolium: an arched recess over a raised chest. This is what an
    /// important person got, and it is deliberately the most expensive piece in
    /// the library — a chamber gets ONE, it stands where the eye lands from the
    /// door, and it is how a player says "the room with the arch" out loud.
    ///
    /// The arch is Era III's own idiom, so an arcosolium in the deep is a MINSTER
    /// intrusion — a grave cut by people who still knew how to turn a voussoir,
    /// in a wall built by people who did not. That anachronism is intentional and
    /// is why they are rare below the Ossuary.
    /// </summary>
    private ArrayMesh Arcosolium(float span, float height) =>
        Piece($"arcosolium_{span:0}_{height:0}", tool =>
        {
            var half = span / 2f;
            var t = WallThick;
            var spring = height - half; // the arch springs where the jambs end

            // The recess: jambs and a back, leaving the void the chest sits in.
            Stone(tool, -half - 20f, 0, -t / 2f, -half, spring + half, t / 2f + 10f, 0.80f);
            Stone(tool, half, 0, -t / 2f, half + 20f, spring + half, t / 2f + 10f, 0.80f);
            Stone(tool, -half, 0, 14f, half, spring + half, t / 2f + 10f, 0.62f);

            // The arch over it — a real voussoired ring, laid stone by stone.
            const int Voussoirs = 9;
            for (var i = 0; i < Voussoirs; i++)
            {
                var a0 = Mathf.Pi * i / Voussoirs;
                var a1 = Mathf.Pi * (i + 1) / Voussoirs;
                var mid = (a0 + a1) / 2f;
                var inner = half;
                var outer = half + 22f;
                var wide = half * (a1 - a0) * 0.54f;
                var centre = new Vector3(-Mathf.Cos(mid) * (inner + outer) / 2f,
                                         spring + Mathf.Sin(mid) * (inner + outer) / 2f, 0f);
                var basis = new Basis(new Vector3(0, 0, 1), mid);
                var lo = centre + basis * new Vector3(-wide, -(outer - inner) / 2f, -t / 2f);
                var hi = centre + basis * new Vector3(wide, (outer - inner) / 2f, t / 2f + 10f);
                Stone(tool, lo.Min(hi), lo.Max(hi), 0.85f + Jitter(i, 3, 71, 0.08f));
            }

            // The chest, raised on a plinth and lidded — the lid slid a hand's
            // width off true, because every one of these was opened eventually.
            Stone(tool, -half + 8f, 0, -t / 2f + 6f, half - 8f, 20f, 26f, 0.76f);
            Stone(tool, -half + 14f, 20f, -t / 2f + 10f, half - 14f, 62f, 22f, 0.82f);
            Stone(tool, -half + 10f, 62f, -t / 2f + 2f, half - 18f, 74f, 18f, 0.88f);
            return "";
        });

    /// <summary>
    /// A forma: a trench cut in the floor, kerbed, with a slab over it. The
    /// pauper's burial and the cheapest thing in the realm, which is the point —
    /// a floor that is nothing but flagstones says nobody was buried here.
    ///
    /// Built as FLOOR, so it never rises enough to trip the walker; the state is
    /// carried by how far the lid has been slid off.
    /// </summary>
    private ArrayMesh Forma(int variant) => Piece($"forma_{variant}", tool =>
    {
        const float halfLong = 96f, halfWide = 30f;
        // The kerb: four low stones round the slot, laid as a border rather than
        // one ring, so an eroded corner reads as a missing stone.
        Stone(tool, -halfLong - 10f, 0, -halfWide - 10f, halfLong + 10f, 7f, -halfWide, 0.72f);
        Stone(tool, -halfLong - 10f, 0, halfWide, halfLong + 10f, 7f, halfWide + 10f, 0.72f);
        Stone(tool, -halfLong - 10f, 0, -halfWide, -halfLong, 7f, halfWide, 0.70f);
        Stone(tool, halfLong, 0, -halfWide, halfLong + 10f, 7f, halfWide, 0.70f);
        // The trench floor, sunk — a dark slot rather than a painted line.
        Stone(tool, -halfLong, -22f, -halfWide, halfLong, -16f, halfWide, 0.42f);

        // The lid, slid by a variant-dependent amount. Variant 0 is shut, 1 is
        // ajar, 2 is off and leaning against the kerb.
        var slide = variant switch { 0 => 0f, 1 => 54f, _ => 150f };
        if (variant < 2)
            Stone(tool, -halfLong + slide, 4f, -halfWide, halfLong + slide, 13f, halfWide,
                  0.84f + Jitter(variant, 0, 73, 0.05f));
        else
            for (var f = 0; f < 3; f++)
            {
                var x = -halfLong + 20f + f * 62f;
                Stone(tool, x, 4f, halfWide + 12f + Jitter(f, 1, 75, 5f), x + 48f, 12f,
                      halfWide + 40f, 0.80f + Jitter(f, 2, 77, 0.06f));
            }
        return "";
    });

    /// <summary>
    /// Bone revetment, built the Paris way (spec LOOK-017): a FACING of long
    /// bones laid end-on in courses, banded with skull courses at intervals, over
    /// an opaque backing. The Catacombes are stacked exactly like this and the
    /// reason is structural — the bones you see are a retaining face and the rest
    /// is tipped in loose behind it.
    ///
    /// It matters that this is not loose skulls on the floor. A floor of skulls
    /// is the generic charnel read, it is what every dungeon does, and it says
    /// nothing except "spooky". A WALL of femur ends in courses says somebody
    /// stacked these, by hand, for a long time — which is the horror.
    /// </summary>
    private ArrayMesh BoneRevetment(float height) => Piece($"revetment_{height:0}", tool =>
    {
        const float half = Module / 2f;
        const float face = -18f; // the facing stands proud of the wall behind it

        // The opaque backing. Nothing behind the facing is modelled, because
        // nothing behind it is visible and a solid tipped-in mass of bone would
        // cost more triangles than the entire Minster.
        Stone(tool, -half, 0, 0f, half, height, 16f, 0.30f);

        var course = 0;
        for (var y = 4f; y < height - 10f; course++)
        {
            // Every fourth course is skulls. The banding is what turns a texture
            // of bone ends into a built thing — Paris did it to keep the courses
            // level, and it reads as deliberate at any distance.
            var skulls = course % 4 == 3;
            var courseHeight = skulls ? 19f : 11f;
            if (y + courseHeight > height - 6f)
                break;

            if (skulls)
            {
                var wide = 15f;
                for (var i = 0; (i + 1) * (wide + 1.5f) <= Module; i++)
                {
                    var x = -half + i * (wide + 1.5f) + 0.75f;
                    var tone = 0.90f + Jitter(course, i, 81, 0.06f);
                    var lean = Jitter(course, i, 83, 1.4f);
                    // The cranium, then the jaw under it, then two sunk sockets.
                    // Four boxes is all a skull needs at the distance one is ever
                    // seen from, and there are hundreds of them.
                    Stone(tool, x + lean, y + 5f, face, x + wide + lean, y + 18f, 2f, tone);
                    Stone(tool, x + 3f + lean, y, face + 3f, x + wide - 3f + lean, y + 5f, 2f, tone - 0.06f);
                    Stone(tool, x + 2.5f + lean, y + 10f, face - 0.5f, x + 6.5f + lean, y + 14f, face + 3f, 0.16f);
                    Stone(tool, x + wide - 6.5f + lean, y + 10f, face - 0.5f, x + wide - 2.5f + lean, y + 14f, face + 3f, 0.16f);
                }
            }
            else
            {
                var wide = 8.5f;
                for (var i = 0; (i + 1) * (wide + 1f) <= Module; i++)
                {
                    var x = -half + i * (wide + 1f) + 0.5f;
                    var jut = Jitter(course, i, 85, 2.5f); // no two bones cut the same length
                    var tone = 0.87f + Jitter(course, i, 87, 0.09f);
                    // The shaft, seen end-on, and the condyle proud of it — which
                    // is the whole reason this reads as a FEMUR and not as a peg.
                    Stone(tool, x, y, face + jut, x + wide, y + 9f, 2f, tone);
                    Stone(tool, x + 1f, y + 1f, face + jut - 2.5f, x + wide - 1f, y + 8f, face + jut,
                          tone + 0.04f);
                }
            }
            y += courseHeight;
        }
        return "";
    });
}

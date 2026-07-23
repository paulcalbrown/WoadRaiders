using System.Collections.Generic;
using Godot;
using WoadRaiders.Core;
using Aabb = WoadRaiders.Core.Aabb;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's SCULPTED VOCABULARY — a small library of stone pieces, each built
/// once and placed many times.
///
/// Every piece is an ArrayMesh saved BESIDE the scene (RealmScene.SharedMesh), so
/// a placement costs a node and a reference rather than a copy of its vertices.
/// That is what lets the realm hold millions of baked triangles in a scene file
/// small enough to read a diff of: measured, a scene costs ~217 bytes a placement
/// and nothing at all for the geometry those placements share.
///
/// THE ERAS DO NOT MIX. Each generator belongs to exactly one, and the rule is
/// the realm's whole legibility (spec SPACE-003):
///
///   Era III — the Minster.    Mortared ashlar. Voussoired arches, groin vaults,
///                             piers with plinth and string course.
///   Era II  — the Souterrain. Drystone: no mortar, so every stone is a separate
///                             slab with dark voids between. Flat lintels over
///                             passages, corbelling over chambers. NO ARCHES.
///   Era I   — the Cairn.      Megalithic. Single split orthostats on edge,
///                             trilithon openings, a corbelled beehive dome.
///
/// Corbelling is the defining Gaelic/Atlantic structural signature — clocháin,
/// passage-tomb vaults and souterrain chambers all use it, and not one of them
/// uses a voussoir. Keeping the arch out of the deep is what separates this from
/// generic fantasy masonry, and it costs nothing but discipline.
///
/// Courses live HERE rather than in a texture because they have to: FastNoiseLite
/// cannot produce running-bond masonry at any setting, so a wall of individually
/// tinted stones is the only way to get one (see StoneSurfaces).
/// </summary>
public sealed partial class CryptDesign
{
    /// <summary>Pieces already built and saved, by name — a library is built once.</summary>
    private readonly Dictionary<string, ArrayMesh> _library = new();

    /// <summary>How many placements the realm has made, for the build log.</summary>
    private int _placements;

    /// <summary>
    /// Standing obstacles a camp must not be placed inside, as (centre, radius)
    /// in plan. Piers and orthostats register themselves here.
    ///
    /// Camps are laid on a stratified grid and the arcade is laid on the module
    /// grid, so the two align by construction and it is only luck how many camps
    /// land in a pillar. Halving the garrison changed the stratification and
    /// buried a minion inside a 92-wide pier in the Ossuary — a camp the
    /// validator correctly called unreachable, a long way from anything that
    /// looked like a cause.
    /// </summary>
    private readonly List<(Vector3 At, float Radius)> _standing = new();

    private void Standing(Vector3 at, float radius) => _standing.Add((at, radius));

    /// <summary>
    /// Every wall bay that was actually built, with the way its face points.
    ///
    /// Wall furniture used to be laid round a space's RECTANGLE — walk the
    /// perimeter, drop a light every so often. That was invisible while a torch
    /// was only a light, and the moment the torches got a model the realm filled
    /// with brackets bolted to thin air: over doorways, across ledges and stairs
    /// that have no walls at all, and along the open sides of spaces that only
    /// ever had two. A rectangle is where a room was DRAWN, not where its stone
    /// ended up.
    /// </summary>
    private readonly List<(Vector3 At, Vector3 Face, float FloorY, float Height, Era Era)> _wallBays = new();

    /// <summary>
    /// The nearest spot to <paramref name="at"/> that is not inside a pillar.
    /// Pushes straight out along the line from the obstacle's centre, which is
    /// the shortest way clear and keeps the scatter's shape.
    /// </summary>
    private Vector3 ClearOfStanding(Vector3 at)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            var moved = false;
            foreach (var (centre, radius) in _standing)
            {
                var away = new Vector3(at.X - centre.X, 0f, at.Z - centre.Z);
                var d = away.Length();
                if (d >= radius)
                    continue;
                // Dead centre gives no direction to push, so pick one.
                away = d < 1e-3f ? Vector3.Right : away / d;
                at += away * (radius - d + 4f);
                moved = true;
            }
            if (!moved)
                break;
        }
        return at;
    }

    // ------------------------------------------------------------- the library

    /// <summary>
    /// The piece under this name, built on first ask and shared thereafter. The
    /// name IS the file name, so it must be stable across runs: one that moved
    /// would rewrite the scene for no reason.
    /// </summary>
    private ArrayMesh Piece(string name, System.Func<SurfaceTool, string> build, bool soffit = false)
    {
        if (_library.TryGetValue(name, out var cached))
            return cached;

        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        build(tool);
        tool.GenerateNormals();
        tool.Index();
        var mesh = tool.Commit();
        WindingGuard(name, mesh, soffit);

        _library[name] = _scene.SharedMesh(Name, name, mesh);
        return _library[name];
    }

    /// <summary>
    /// Every piece is checked for right-way-out faces as it is built.
    ///
    /// Winding is the one thing geometry cannot state about itself, and getting
    /// it backwards is SILENT: Godot renders the inside of the realm, the bake
    /// reads every upward face as an overhang, and the realm simply has no floor
    /// — which surfaces much later as a validator complaining that nothing is
    /// reachable. A cross product here costs nothing and names the piece.
    ///
    /// A SOFFIT is the exception and has to be told apart: a vault's underside is
    /// seen from below and legitimately has no outward top face at all, so the
    /// same test that proves a solid right-way-out would condemn it.
    /// </summary>
    private static void WindingGuard(string name, ArrayMesh mesh, bool soffit)
    {
        var faces = mesh.GetFaces();
        var wanted = 0;
        for (var i = 0; i + 2 < faces.Length; i += 3)
        {
            // Godot winds front faces clockwise, so a face presenting itself
            // UPWARD yields a right-hand normal pointing DOWN, and vice versa.
            var normal = (faces[i + 1] - faces[i]).Cross(faces[i + 2] - faces[i]);
            if (soffit ? normal.Y > 1e-3f : normal.Y < -1e-3f)
                wanted++;
        }
        if (wanted == 0)
            throw new System.InvalidOperationException(
                $"the '{name}' piece presents no {(soffit ? "downward" : "upward")} surface — its winding is " +
                "inside out, and a realm whose floors are overhangs has no floor at all");
    }

    /// <summary>Deterministic noise — same stones every run, no framework RNG.</summary>
    private static float Hash(int a, int b, int salt)
    {
        var h = unchecked((uint)(a * 374761393 + b * 668265263 + salt * 974711 + 144665));
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / (float)0x1000000; // 0..1
    }

    /// <summary>A jitter in ±<paramref name="spread"/>, deterministic per stone.</summary>
    private static float Jitter(int a, int b, int salt, float spread) => (Hash(a, b, salt) - 0.5f) * 2f * spread;

    // ------------------------------------------------------------- the stones

    /// <summary>
    /// One stone: a box, wound outward, carrying its own tint. This is the atom
    /// every piece below is built from, because a wall of separately tinted
    /// stones is the only masonry this project can produce — noise cannot course.
    /// </summary>
    private static void Stone(SurfaceTool tool, Vector3 min, Vector3 max, float tone)
    {
        var colour = new Color(tone, tone, tone);
        // Corners, named so the faces below read as faces rather than indices.
        var a = new Vector3(min.X, min.Y, min.Z);
        var b = new Vector3(max.X, min.Y, min.Z);
        var c = new Vector3(max.X, min.Y, max.Z);
        var d = new Vector3(min.X, min.Y, max.Z);
        var e = new Vector3(min.X, max.Y, min.Z);
        var f = new Vector3(max.X, max.Y, min.Z);
        var g = new Vector3(max.X, max.Y, max.Z);
        var h = new Vector3(min.X, max.Y, max.Z);

        // Clockwise seen from OUTSIDE each face — Godot's front-face convention.
        Quad(tool, colour, e, f, g, h); // top    (+Y), clockwise from above
        Quad(tool, colour, d, c, b, a); // bottom (−Y)
        Quad(tool, colour, a, b, f, e); // north  (−Z)
        Quad(tool, colour, c, d, h, g); // south  (+Z)
        Quad(tool, colour, d, a, e, h); // west   (−X)
        Quad(tool, colour, b, c, g, f); // east   (+X)
    }

    private static void Quad(SurfaceTool tool, Color colour, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        foreach (var v in new[] { p0, p1, p2, p0, p2, p3 })
        {
            tool.SetColor(colour);
            tool.AddVertex(v);
        }
    }

    /// <summary>A stone from an axis-aligned box, for readability at call sites.</summary>
    private static void Stone(SurfaceTool tool, float x0, float y0, float z0,
                              float x1, float y1, float z1, float tone) =>
        Stone(tool, new Vector3(x0, y0, z0), new Vector3(x1, y1, z1), tone);

    // ---------------------------------------------------- Era III: the Minster

    /// <summary>
    /// A bay of mortared ashlar: a projecting plinth, courses of squared blocks
    /// with staggered joints, and a string course capping it. Dressed stone, so
    /// the joints are thin and the faces flat — the opposite of drystone below,
    /// and the contrast is the point.
    /// </summary>
    private ArrayMesh AshlarBay(float height) => Piece($"ashlar_bay_{height:0}", tool =>
    {
        const float half = Module / 2f;
        var t = WallThick;

        // THE CORE OWNS THE EDGES; the facing sits inside them. This is the fix
        // for the realm's worst z-fighting: a bay used to cap its ends with the
        // plinth, the core AND the blocks all at the exact same plane, three
        // coincident faces fighting for the same pixels — invisible mid-wall
        // (buried in the next bay) but glaring at every door jamb and corner. So
        // only the CORE reaches the bay edge (+-half); the plinth, blocks and
        // string all inset from it, capping at their own planes. Nothing coincides.
        //
        // The core is the full solid box the joints are grooves in — set 3 back
        // from the face so a joint self-shadows into a defined line.
        Stone(tool, -half, 0, -t / 2 + 3, half, height, t / 2 - 3, 0.58f);
        Stone(tool, -half + 2, 0, -t / 2 - 4, half - 2, 14, t / 2 + 4, 0.78f);   // plinth, inset from the edge

        var courses = Mathf.Max(2, Mathf.RoundToInt((height - 26f) / 26f));
        for (var c = 0; c < courses; c++)
        {
            var y0 = 14 + c * 26f;
            // 25 of a 26 pitch: a 1-unit bed joint, not the 2 that read as a gap.
            var y1 = Mathf.Min(height - 12f, y0 + 25f);
            // Three blocks a course, their joints staggered course to course —
            // a running bond, which is what makes ashlar read as laid rather
            // than poured. Clamped to half-1, so the block ends never reach the
            // core's edge and cannot fight it there.
            var offset = c % 2 == 0 ? 0f : Module / 6f;
            for (var s = -1; s <= 1; s++)
            {
                var x0 = Mathf.Max(-half + 1f, s * Module / 3f - Module / 6f + offset);
                var x1 = Mathf.Min(half - 1f, x0 + Module / 3f - 1f); // 1-unit perpend, tight
                if (x1 - x0 < 6f)
                    continue;
                Stone(tool, x0, y0, -t / 2, x1, y1, t / 2, 0.86f + Jitter(c, s, 11, 0.10f));
            }
        }
        Stone(tool, -half + 2, height - 12f, -t / 2 - 5, half - 2, height, t / 2 + 5, 0.80f); // string, inset
        return "";
    });

    /// <summary>
    /// The panel of wall above a doorway's arch, sized to the gap EXACTLY.
    ///
    /// It used to be tiled from Module-wide bays, `ceil(width/Module)` of them —
    /// so whenever the gap was not a clean multiple of the module the tiling
    /// overspilled it and drove its stones through the wall bays either side,
    /// which was the doorway z-fighting. This is one piece the width of the gap:
    /// it abuts the wall bays rather than overlapping them, and its own facing
    /// insets from the edge so the core alone caps the sides.
    /// </summary>
    private ArrayMesh Spandrel(float width, float height) => Piece($"spandrel_{width:0}x{height:0}", tool =>
    {
        var t = WallThick;
        var half = width / 2f;
        Stone(tool, -half, 0, -t / 2 + 3, half, height, t / 2 - 3, 0.58f);   // core owns the edges

        var courses = Mathf.Max(1, Mathf.RoundToInt(height / 26f));
        var per = Mathf.Max(1, Mathf.RoundToInt(width / 30f));               // ~30-wide blocks
        var bw = width / per;
        for (var c = 0; c < courses; c++)
        {
            var y0 = c * (height / courses);
            var y1 = y0 + height / courses - 1f;
            var offset = c % 2 == 0 ? 0f : bw / 2f;                          // running bond
            for (var k = -1; k <= per; k++)
            {
                var x0 = Mathf.Max(-half + 1f, -half + k * bw + offset);
                var x1 = Mathf.Min(half - 1f, x0 + bw - 1f);
                if (x1 - x0 < 6f)
                    continue;
                Stone(tool, x0, y0, -t / 2, x1, y1, t / 2, 0.86f + Jitter(c, k, 11, 0.10f));
            }
        }
        return "";
    });

    /// <summary>
    /// A semicircular arch ring of individual voussoirs with a keystone at the
    /// crown. An ODD count, so one stone lands on the centre line — that is what
    /// a keystone is, and an even ring gives a joint there instead.
    /// </summary>
    private ArrayMesh ArchRing(float span, float depth) => Piece($"arch_{span:0}_{depth:0}", tool =>
    {
        const int Voussoirs = 11;
        var radius = span / 2f;
        for (var i = 0; i < Voussoirs; i++)
        {
            var a0 = Mathf.Pi * i / Voussoirs;
            var a1 = Mathf.Pi * (i + 1) / Voussoirs;
            // Each voussoir is a wedge, approximated by a box spanning its arc.
            var inner = radius + Jitter(i, 0, 21, 1.5f);
            var outer = inner + depth;
            var mid = (a0 + a1) / 2f;
            // 0.46, not 0.52: a straight box standing in for a curved wedge
            // overlaps its neighbours near the inner radius when cut to touch, and
            // those overlaps z-fight. Narrowing it leaves a thin joint between
            // voussoirs — which is what mortar is — and they stop fighting.
            var wide = radius * (a1 - a0) * 0.46f;
            var tone = 0.84f + Jitter(i, 1, 23, 0.09f);
            // Rotate the wedge into place around Z, keeping it a stone rather than
            // a segment of a smooth tube — the Z-rotation of the box about the arc
            // midpoint, in deterministic arithmetic (see Det.CosSin) not a Basis.
            var (c, s) = Det.CosSin(mid);
            var mr = (inner + outer) / 2f;
            Vector3 P(double x, double y, double z) =>
                new((float)(c * (x - mr) - s * y), (float)(s * (mr + x) + c * y), (float)z);
            var lo = P(-wide, -(outer - inner) / 2f, -WallThick / 2f);
            var hi = P(wide, (outer - inner) / 2f, WallThick / 2f);
            Stone(tool, lo.Min(hi), lo.Max(hi), tone);
        }
        return "";
    });

    /// <summary>
    /// A groin-vault bay: two barrel profiles intersecting, so the ceiling is
    /// <c>min(A(x), B(z))</c> and the groins fall on the diagonals. Built as four
    /// webs parametrised so a groin is a mesh EDGE — a naive grid over the bay
    /// would run the crease diagonally through its quads and lose it.
    ///
    /// The short arch is stilted to the long one's crown height, which is the
    /// standard fix for a rectangular bay: otherwise the two barrels top out at
    /// different heights and never meet.
    /// </summary>
    private ArrayMesh GroinVault(float width, float depth, float rise, int segments) =>
        Piece($"groin_{width:0}x{depth:0}_{rise:0}", soffit: true, build: tool =>
        {
            var rx = width / 2f;
            var rz = depth / 2f;
            Vector3 Surface(float x, float z)
            {
                var ax = rise * Mathf.Sqrt(Mathf.Max(0f, 1f - x * x / (rx * rx)));
                var az = rise * Mathf.Sqrt(Mathf.Max(0f, 1f - z * z / (rz * rz)));
                return new Vector3(x, Mathf.Min(ax, az), z);
            }

            for (var i = 0; i < segments; i++)
            {
                for (var j = 0; j < segments; j++)
                {
                    // Sample across the whole bay; min() puts the groin exactly
                    // on the diagonal where the two profiles cross.
                    var x0 = Mathf.Lerp(-rx, rx, i / (float)segments);
                    var x1 = Mathf.Lerp(-rx, rx, (i + 1) / (float)segments);
                    var z0 = Mathf.Lerp(-rz, rz, j / (float)segments);
                    var z1 = Mathf.Lerp(-rz, rz, (j + 1) / (float)segments);
                    var tone = 0.80f + Jitter(i, j, 31, 0.07f);
                    // Underside only: nobody stands on a vault, and the upper
                    // face would only add triangles and a walkable roof.
                    var colour = new Color(tone, tone, tone);
                    Quad(tool, colour, Surface(x0, z0), Surface(x0, z1), Surface(x1, z1), Surface(x1, z0));
                }
            }
            return "";
        });

    /// <summary>A pier: a squared shaft with a chamfered base and a capital, the
    /// arcade's support. Rectilinear on purpose — Naughty Dog's shape-language
    /// rule, that rectangles read as structural and round shapes disappear.</summary>
    private ArrayMesh Pier(float height) => Piece($"pier_{height:0}", tool =>
    {
        var w = PierHalf;
        Stone(tool, -w - 6, 0, -w - 6, w + 6, 16, w + 6, 0.76f);                    // base
        Stone(tool, -w, 16, -w, w, height - 18, w, 0.88f);                          // shaft
        Stone(tool, -w - 7, height - 18, -w - 7, w + 7, height, w + 7, 0.79f);      // capital
        return "";
    });

    // ------------------------------------------------- Era II: the Souterrain

    /// <summary>
    /// A bay of DRYSTONE: irregular flat slabs laid in rough courses with dark
    /// voids between them and small pinning stones wedged in the gaps. No mortar
    /// anywhere, which is the single biggest read difference between a Celtic
    /// underground space and a generic dungeon — and it is why this cannot be a
    /// texture: the gaps have to be real geometry to self-shadow under a moving
    /// flame.
    /// </summary>
    private ArrayMesh DrystoneBay(float height) => Piece($"drystone_bay_{height:0}", tool =>
    {
        const float half = Module / 2f;
        var t = DrystoneThick;

        // A solid core, as under the ashlar — but set deeper, because drystone
        // WANTS its voids: the gaps between slabs are the whole character of a
        // mortarless wall. The core only stops them seeing clean through to the
        // dark beyond; a void still falls ~20 to reach it, so it stays a deep
        // self-shadowing hollow rather than becoming a flat painted seam.
        Stone(tool, -half, 0, -8f, half, height, 8f, 0.50f);

        var courses = Mathf.Max(3, Mathf.RoundToInt(height / 34f));
        var y = 0f;
        for (var c = 0; c < courses && y < height - 6f; c++)
        {
            var courseHeight = Mathf.Min(height - y, 26f + Jitter(c, 0, 41, 7f));
            // Slabs of unequal length, so no two courses break in the same place.
            // Start a unit IN from the edge so the first slab's end face never
            // lands on the core's edge and fights it there — the same end-cap
            // z-fighting the ashlar had, and the core owns the bay edge here too.
            var x = -half + 1f;
            var s = 0;
            while (x < half - 4f)
            {
                var run = Mathf.Min(half - 1f - x, 22f + Hash(c, s, 43) * 26f);
                var depth = t / 2f - Hash(c, s, 47) * 5f; // faces sit at slightly different depths
                Stone(tool, x, y, -depth, x + run - 2.5f, y + courseHeight - 3f, depth,
                      0.72f + Jitter(c, s, 45, 0.13f));
                // A pinning stone wedged into the gap above, as a waller would.
                if (Hash(c, s, 49) > 0.55f)
                    Stone(tool, x + 4, y + courseHeight - 3f, -depth * 0.6f,
                          x + 11, y + courseHeight, depth * 0.6f, 0.66f);
                x += run;
                s++;
            }
            y += courseHeight;
        }
        return "";
    });

    /// <summary>A lintel: one long stone bridging an opening. Era II roofs its
    /// passages with these and its chambers by corbelling — never with an arch.</summary>
    private ArrayMesh Lintel(float span) => Piece($"lintel_{span:0}", tool =>
    {
        Stone(tool, -span / 2f, 0, -DrystoneThick / 2f - 4, span / 2f, 26, DrystoneThick / 2f + 4, 0.70f);
        return "";
    });

    // The burial forms — loculus, arcosolium, forma and the bone revetment —
    // live in CryptDesign.Burial.cs. They are masonry, but they are the realm's
    // SUBJECT rather than its structure, and the states they wear are graded
    // across a room by the caller rather than decided piece by piece.

    /// <summary>One ring of a corbelled roof: flat stones stepping inward, each
    /// course oversailing the one below. No centring, no keystone — the Atlantic
    /// way of closing a space, and the reason there are no arches down here.</summary>
    private ArrayMesh CorbelRing(float radius, int stones) => Piece($"corbel_{radius:0}_{stones}", tool =>
    {
        for (var i = 0; i < stones; i++)
        {
            var a = Mathf.Tau * i / stones;
            var wide = Mathf.Pi * radius / stones * 0.92f;
            // The stone is the Up-rotation (by -a) of the offset about the ring
            // point (cos a * radius, 0, sin a * radius), done in deterministic
            // arithmetic (see Det.CosSin) rather than a libm-backed Basis.
            var (c, s) = Det.CosSin(a);
            Vector3 P(double ox, double oy, double oz) =>
                new((float)(c * (radius + ox) - s * oz), (float)oy, (float)(s * (radius + ox) + c * oz));
            var lo = P(-wide, 0, -22f);
            var hi = P(wide, 20f, 22f);
            Stone(tool, lo.Min(hi), lo.Max(hi), 0.74f + Jitter(i, 0, 61, 0.10f));
        }
        return "";
    });

    // --------------------------------------------------------- Era I: the Cairn

    /// <summary>
    /// An orthostat: one split slab set on edge, taller than a person and
    /// leaning a little as a stone dragged into place does. Megalithic — nobody
    /// squared this, and its irregularity is what separates it from Era III's
    /// dressed ashlar at a glance.
    /// </summary>
    private ArrayMesh Orthostat(float height, int variant) => Piece($"orthostat_{height:0}_{variant}", tool =>
    {
        var w = Module / 2f - 4f + Jitter(variant, 0, 71, 6f);
        var t = OrthostatThick / 2f;
        // Built as three stacked slices so the slab tapers and kinks rather than
        // reading as a box — a split rock face, not a cut one.
        var slices = 3;
        for (var i = 0; i < slices; i++)
        {
            var y0 = height * i / slices;
            var y1 = height * (i + 1) / slices;
            var lean = Jitter(variant, i, 73, 5f);
            var taper = 1f - 0.12f * i / slices;
            Stone(tool, -w * taper + lean, y0, -t * taper, w * taper + lean, y1, t * taper,
                  0.70f + Jitter(variant, i, 75, 0.12f));
        }
        return "";
    });

    /// <summary>A kerbstone: a decorated slab set on edge round a cairn's foot,
    /// marking boundary and threshold. Newgrange has 97 of them.</summary>
    private ArrayMesh Kerbstone(int variant) => Piece($"kerb_{variant}", tool =>
    {
        var w = Module / 2f - 6f;
        Stone(tool, -w, 0, -22f, w, 54f + Jitter(variant, 0, 81, 10f), 22f,
              0.73f + Jitter(variant, 1, 83, 0.10f));
        return "";
    });

    // ------------------------------------------------------- placement verbs

    /// <summary>Place a library piece, filed as structure (it blocks) or floor.</summary>
    private MeshInstance3D Place(ArrayMesh piece, Vector3 at, float yaw, Material material, bool floor = false)
    {
        var node = new MeshInstance3D
        {
            Mesh = piece,
            MaterialOverride = material,
            Position = at,
            // Deterministic yaw (see Det): a piece placed at an arbitrary angle —
            // the orthostat ring around the Wheel turns each stone by -a — is
            // collision, so a libm Euler->Basis here would drift the baked soup
            // across hosts. Every other yaw is cardinal and unaffected either way.
            Basis = Det.EulerBasis(0f, yaw, 0f),
        };
        _placements++;
        return floor ? _scene.AddFloor(node) : _scene.AddStructure(node);
    }

    /// <summary>
    /// A run of wall from one point to another, in the era's own grammar, split
    /// around a doorway when one is given. Bays are laid on the module, so a
    /// longer wall is MORE stones rather than the same few stretched.
    /// </summary>
    /// <param name="doorFloorY">
    /// The floor height AT THE DOORWAY, when that differs from the wall's own
    /// base. A descending corridor's walls are built from its LOWEST point so
    /// they stand level along its length — but its head-height is measured from
    /// the floor a raider is actually on, and a side door partway up the ramp is
    /// 80 above that base. Left to the wall's base, the creepway's lintels landed
    /// at −544 with the gallery floor at −560: a 16-unit gap, and the Cubiculum
    /// unreachable. The gap in the masonry was correct the whole time; the thing
    /// bridging it was at knee height.
    /// </param>
    private void WallRun(Era era, float x0, float z0, float x1, float z1, float floorY, float height,
                         float? doorAt = null, float doorWidth = 0f, float? doorFloorY = null)
    {
        var alongZ = Mathf.Abs(z1 - z0) > Mathf.Abs(x1 - x0);
        var length = alongZ ? z1 - z0 : x1 - x0;
        var bays = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(length) / Module));
        var yaw = alongZ ? Mathf.Pi / 2f : 0f;

        // Track which bays actually go in, so the occluders below can cover the
        // SOLID spans and stop at the doorway. An occluder larger than its
        // solid culls what is really visible — a doorway you can see through
        // but the renderer has decided you cannot.
        var solid = new List<float>();
        for (var i = 0; i < bays; i++)
        {
            var t = (i + 0.5f) / bays;
            var x = alongZ ? x0 : Mathf.Lerp(x0, x1, t);
            var z = alongZ ? Mathf.Lerp(z0, z1, t) : z0;
            var along = alongZ ? z : x;

            // The doorway is a hole in the run: skip the bays it covers and
            // lintel or arch the head, per era.
            if (doorAt is { } door && Mathf.Abs(along - door) < doorWidth / 2f + Module / 2f)
                continue;

            Place(Bay(era, height), new Vector3(x, floorY, z), yaw, Stone(era));
            solid.Add(along);
            // Which way this bay's face looks. A wall run is a line, so the
            // outward normal is whichever axis it does NOT run along; the sign
            // is resolved later, against the centre of the space it belongs to.
            _wallBays.Add((new Vector3(x, floorY, z),
                           alongZ ? Vector3.Right : Vector3.Back, floorY, height, era));
        }

        Occlude(era, solid, alongZ, alongZ ? x0 : z0, floorY, height, Mathf.Sign(length));

        if (doorAt is not { } opening)
            return;

        // The opening spans the whole gap the skipped bays left, NOT the nominal
        // door width. A bay is skipped when it so much as TOUCHES the doorway, so
        // a 120 door punches a 160-240 hole — and filling only 120 of it left
        // full-height daylight either side of every arch. At the old 240 wall
        // heights that read as a rough jamb; at 560-820 it reads as an arch
        // floating in a hole, which is exactly what it was.
        var span = doorWidth;
        if (solid.Count > 0)
        {
            var before = float.NegativeInfinity;
            var after = float.PositiveInfinity;
            foreach (var edge in solid)
            {
                if (edge < opening && edge > before) before = edge;
                if (edge > opening && edge < after) after = edge;
            }
            if (!float.IsInfinity(before) && !float.IsInfinity(after))
                span = after - before - Module;
        }
        span = Mathf.Max(doorWidth, span);

        var ox = alongZ ? x0 : opening;
        var oz = alongZ ? opening : z0;
        Opening(era, new Vector3(ox, doorFloorY ?? floorY, oz), yaw, span, height);
    }

    /// <summary>
    /// Offer the renderer the solid spans of a wall run to cull behind — one
    /// box per unbroken stretch of bays, so a doorway stays a hole in the
    /// occluder as well as in the stone.
    ///
    /// Sculpted walls need this stated because they do not go through
    /// BoxKit.Structure, which emits occluders unasked. Godot documents
    /// occlusion culling as most effective in exactly this shape of world —
    /// many small indoor rooms — and without it every flame and probe in
    /// chambers a raider cannot see is still submitted.
    /// </summary>
    private void Occlude(Era era, List<float> solid, bool alongZ, float fixedAxis, float floorY, float height,
                         float direction)
    {
        if (solid.Count == 0)
            return;
        var thickness = era switch
        {
            Era.Minster => WallThick,
            Era.Souterrain => DrystoneThick,
            _ => OrthostatThick,
        };

        var start = 0;
        for (var i = 1; i <= solid.Count; i++)
        {
            // A span ends where the next bay is not adjacent — i.e. at a door.
            var contiguous = i < solid.Count && Mathf.Abs(solid[i] - solid[i - 1]) < Module * 1.5f;
            if (contiguous)
                continue;

            var a = Mathf.Min(solid[start], solid[i - 1]) - Module / 2f;
            var b = Mathf.Max(solid[start], solid[i - 1]) + Module / 2f;
            var centreAlong = (a + b) / 2f;
            var span = b - a;
            var centre = alongZ
                ? new Vector3(fixedAxis, floorY + height / 2f, centreAlong)
                : new Vector3(centreAlong, floorY + height / 2f, fixedAxis);
            var size = alongZ
                ? new Vector3(thickness, height, span)
                : new Vector3(span, height, thickness);
            _scene.AddOccluder(centre, size);
            start = i;
        }
        _ = direction;
    }

    /// <summary>The era's wall bay.</summary>
    private ArrayMesh Bay(Era era, float height) => era switch
    {
        Era.Minster => AshlarBay(height),
        Era.Souterrain => DrystoneBay(height),
        _ => Orthostat(height, 0),
    };

    /// <summary>
    /// The era's way of spanning an opening, and the clearest single tell of
    /// which age built a wall: a voussoired arch, a drystone head stepping in on
    /// corbels, or a trilithon — two uprights and one lintel.
    /// </summary>
    /// <summary>
    /// The head of a doorway — an arch, a corbelled lintel, or a trilithon slab,
    /// per era — hung HIGH ENOUGH THAT THE CAMERA NEVER DUCKS UNDER IT.
    ///
    /// A 4 m door head is right for a person and a third of what the chase rig
    /// needs: at DoorHeadIII (96) every threshold in the realm crushed the camera
    /// onto the raider's shoulder for the two strides it took to pass through.
    /// Ceilings were raised for exactly this reason (PLAY-001) and the doorways
    /// were left behind, so the realm went from being unreadable in rooms to
    /// being unreadable in the gaps between them.
    ///
    /// The head is therefore lifted to clear <c>CameraFreeAt</c> — and where the
    /// wall is too low to carry one that high, there is NO head at all. An
    /// opening open to the wall-head is a perfectly good opening; an arch you
    /// have to stoop through is not, however correct its voussoirs are.
    /// </summary>
    private void Opening(Era era, Vector3 at, float yaw, float width, float wallHeight)
    {
        var material = Stone(era);
        var turn = new Basis(Vector3.Up, yaw);

        // What the head costs above its springing, per era: an arch rises by its
        // own half-span, a lintel course by its three steps, a trilithon by the
        // one slab across the top.
        var crown = era switch
        {
            Era.Minster => width / 2f + 26f,
            Era.Souterrain => 3f * 26f,
            _ => 30f,
        };

        // Clear the camera if the wall can carry it. 40 of margin over
        // CameraFreeAt, because the rig also holds CeilingClearance under
        // whatever it finds and a threshold is the worst place to be tight.
        var wanted = CameraFreeAt + 40f;
        var highest = wallHeight - crown - 20f;
        if (highest < wanted)
        {
            // No head fits above head height. Leave the opening open rather than
            // hang stone in the one place a player has to walk through.
            return;
        }
        var head = Mathf.Min(wanted, highest);

        switch (era)
        {
            case Era.Minster:
                Place(ArchRing(width, 22f), at + new Vector3(0, head, 0), yaw, material);
                // The wall above the arch, carried on it — ONE panel the width of
                // the gap, abutting the wall bays rather than tiling past them.
                var lift = head + width / 2f;
                Place(Spandrel(width, Mathf.Max(20f, wallHeight - lift)),
                      at + new Vector3(0, lift, 0), yaw, material);
                break;

            case Era.Souterrain:
                // Courses stepping in until the gap is short enough to lintel.
                for (var c = 0; c < 3; c++)
                    Place(Lintel(width + 24f - c * 12f), at + new Vector3(0, head + c * 26f, 0),
                          yaw, material);
                break;

            default:
                // A trilithon: uprights either side, one great stone across. The
                // uprights are as tall as the head they carry, or the lintel
                // floats above two stumps.
                foreach (var side in new[] { -1f, 1f })
                    Place(Orthostat(head, side > 0 ? 1 : 2),
                          at + turn * new Vector3(side * (width / 2f + Module / 2f), 0f, 0f),
                          yaw, material);
                Place(Lintel(width + Module), at + new Vector3(0, head, 0), yaw, material);
                break;
        }
    }

    /// <summary>A chamber's roof, in its era's grammar. An unroofed space (a
    /// ceiling of 0) gets nothing — the dark closes it, and the chase camera
    /// opens right out, which is how this realm spends its releases.</summary>
    private void Roof(Era era, Space space, float ceiling)
    {
        if (ceiling <= 0f)
            return;

        // A vault's soffit is its own surface; a corbelled roof is the same
        // drystone as the walls that carry it, because it IS those walls, still
        // stepping inward.
        var material = era == Era.Minster ? _soffit : Stone(era);
        var top = space.FloorY + ceiling;
        if (era == Era.Minster)
        {
            // Groin bays across the span, springing from the wall heads.
            var bays = Mathf.Max(1, Mathf.RoundToInt(space.Width / VaultBay));
            var deep = Mathf.Max(1, Mathf.RoundToInt(space.Depth / VaultBay));
            var w = space.Width / bays;
            var d = space.Depth / deep;
            var vault = GroinVault(w, d, Mathf.Min(ceiling * 0.42f, VaultBay * 0.5f), 6);
            for (var i = 0; i < bays; i++)
                for (var j = 0; j < deep; j++)
                    Place(vault, new Vector3(space.X0 + (i + 0.5f) * w, top - ceiling * 0.42f,
                                             space.Z0 + (j + 0.5f) * d), 0f, material);
            return;
        }

        // Era II and I close a space by CORBELLING: rings of flat stones
        // stepping inward course by course until one slab can seal the top.
        var radius = Mathf.Min(space.Width, space.Depth) / 2f;
        var courses = Mathf.Max(3, Mathf.RoundToInt(ceiling / 40f));
        for (var c = 0; c < courses; c++)
        {
            var t = c / (float)courses;
            var r = radius * (1f - t * 0.92f);
            if (r < Module / 2f)
                break;
            var stones = Mathf.Max(6, Mathf.RoundToInt(Mathf.Tau * r / Module));
            Place(CorbelRing(r, stones), new Vector3(space.MidX, space.FloorY + ceiling * 0.55f + c * 22f, space.MidZ),
                  0f, material);
        }
    }
}

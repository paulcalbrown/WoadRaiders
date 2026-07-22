using System.Collections.Generic;
using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's light, and the story is told in COLOUR TEMPERATURE running
/// backwards through time: warm tallow where the living were recently, guttering
/// rushlight where they were long ago, and in the cairn no fire at all.
///
/// Almost everything here was measured or corrected rather than chosen, and the
/// first Crypt got most of it wrong in the same direction — too bright, too far,
/// too even:
///
///   REACH. Its lamps ran ranges of 1150–1800 units, which is 48–75 metres. A
///   real torch cannot light a room; it lights the wall it hangs on. Ranges here
///   are 96–384 with physically-correct inverse-square falloff, so the dark is
///   the default state of the realm and a lit place is an event.
///
///   SHAPE. Wall flames are SpotLight3D, not omni. A shadow-casting spot is one
///   shadow pass where an omni cube is six, and the wall behind occludes nothing
///   anyway — so the cheap choice is also the correct one.
///
///   MOVEMENT. Flicker animates light_energy ONLY, never a transform. Godot
///   caches positional shadow maps and invalidates them when something moves, so
///   a static stone interior pays its shadow cost roughly once — unless a light
///   jitters, which would make it pay every frame for nothing.
///
///   FILL. Ambient sits near zero and the lift comes from box-projected interior
///   reflection probes instead. That is what keeps a black corner readable
///   without adding a light that no flame in the fiction is casting.
/// </summary>
public sealed partial class CryptDesign
{
    // sRGB as a colour picker would show them — Godot converts internally, so
    // pre-linearising here would double-convert into muddy orange.
    private static readonly Color Tallow = new(1.00f, 0.70f, 0.40f);      // Era III, lamps and cressets
    private static readonly Color Ember = new(1.00f, 0.60f, 0.28f);       // braziers, hotter and deeper
    private static readonly Color Rushlight = new(1.00f, 0.64f, 0.32f);   // Era II, guttering in niches
    private static readonly Color Witchlight = new(0.52f, 0.74f, 1.00f);  // Era I — not fire at all
    private static readonly Color Daylight = new(0.72f, 0.80f, 0.92f);    // the lucernaria

    /// <summary>Physically-correct falloff. Godot defaults to 1.0, which is a
    /// constant-ish brightness through most of the range — the opposite of the
    /// pooled light this realm is built on.</summary>
    private const float InverseSquare = 2.0f;

    /// <summary>One metre, in this realm's units — a raider is ~44 and a 4 m door
    /// is 96. Nothing about the engine knows this, which is the point.</summary>
    private const float Metre = 24f;

    /// <summary>
    /// An energy stated the way a person thinks about light — how bright it is a
    /// METRE away — converted to what Godot actually wants.
    ///
    /// This is the single most expensive thing that has been got wrong in this
    /// realm, and it is invisible until you look at it. Godot's attenuation is
    ///
    ///     (1 − (d/range)⁴)² · d^(−decay)
    ///
    /// and that <c>d</c> is in WORLD UNITS, not metres. Under inverse square, a
    /// realm authored at 24 units to the metre therefore runs every light 24² =
    /// 576× too dim — and dim in a way no amount of squinting diagnoses, because
    /// the geometry is all still there and correctly shaded, just below the
    /// ambient term. Measured before the fix: the brightest pixel in the entire
    /// realm was 0.021, every frame, in every chamber. The lights were
    /// contributing less than the near-black ambient meant to fill behind them.
    ///
    /// So energies below are quoted at one metre and multiplied by Metre^decay.
    /// State a light the way the fiction does; let the arithmetic meet the engine.
    /// </summary>
    private static float Candela(float atOneMetre, float decay = InverseSquare) =>
        atOneMetre * Mathf.Pow(Metre, decay);

    /// <summary>A light must stand this far off any surface, or the near-zero
    /// distance term in Godot's attenuation whites the masonry out.</summary>
    private const float Standoff = 14f;

    private Node3D _lights = null!;
    private int _lampCount;
    private int _shadowCount;

    /// <summary>
    /// Which flames belong to which chamber, and what each one burns at when it
    /// is not guttering. The flicker below needs BOTH: an animation track holds
    /// absolute values, so a light's own base energy has to travel with its name
    /// or every flame in the realm would animate to the same brightness.
    ///
    /// Insertion-ordered, and iterated in that order — a Dictionary's order is
    /// stable here because nothing is ever removed, and the realm has to
    /// regenerate byte for byte.
    /// </summary>
    private readonly Dictionary<string, List<(string Name, float Energy)>> _flames = new();

    private void Flammable(string owner, string name, float energy)
    {
        if (!_flames.TryGetValue(owner, out var list))
            _flames[owner] = list = new List<(string, float)>();
        list.Add((name, energy));
    }

    /// <summary>
    /// The chamber a loose light stands in — how a vignette's own flame joins the
    /// right room's flicker. Falls back to the nearest centre, because a light in
    /// a doorway or over a pit belongs to SOMETHING and a light in no chamber
    /// would simply never gutter.
    /// </summary>
    private string Owning(Vector3 at)
    {
        foreach (var space in _spaces)
            if (at.X >= space.X0 && at.X <= space.X1 && at.Z >= space.Z0 && at.Z <= space.Z1 &&
                at.Y >= space.FloorY - 40f && at.Y <= space.FloorY + Mathf.Max(space.Ceiling, 400f))
                return space.Id;

        var best = _spaces[0];
        var bestDistance = float.MaxValue;
        foreach (var space in _spaces)
        {
            var d = new Vector3(space.MidX, space.FloorY, space.MidZ).DistanceSquaredTo(at);
            if (d < bestDistance)
                (best, bestDistance) = (space, d);
        }
        return best.Id;
    }

    private void Gloom()
    {
        _lights = _scene.Folder("Lights");
        _scene.Add(Environment(), "Gloom");

        foreach (var space in _spaces)
            LightSpace(space);

        // The two lucernaria: shafts from the surface, the only cold DAYLIGHT in
        // the realm and its only vertical reference. One over the porch, one
        // cutting the Fault — a compass without a HUD, and the strongest
        // possible contrast against 1900 K firelight.
        Lucernarium(Named("B1"));
        Lucernarium(Named("B4"));

        // The Wheel's quartz, lit by nothing that burns. The one shadow-caster
        // in the deep, so the boss reads as standing on something.
        //
        // It burns at Candela(70), which looks absurd beside a 9 cd torch and is
        // not: inverse square is unforgiving over distance, and this stands 420
        // units — 17 m — above the cist it has to light. At 9 cd the payoff
        // chamber of the entire descent measured 95.6% pure black. Eight torches
        // CANNOT light a 53 × 37 m hall, and no amount of adding more of them
        // fixes that; the room needed a source scaled to the room.
        var wheel = Named("B9");
        var hero = Lamp(Witchlight, energy: Candela(70f), range: 1400f,
                        new Vector3(wheel.MidX, wheel.FloorY + 420f, wheel.MidZ), fog: 1f);
        hero.ShadowEnabled = true;
        hero.LightSize = 24f;
        _shadowCount++;

        // And the ring answers it. A witchlight at the foot of each orthostat —
        // the stones themselves lit from below, which is both the reason the
        // chamber is readable and the single strongest image the realm has: you
        // do not walk into a lit room, you walk into a ring of standing stones
        // that are burning. Cheap, too: no shadows, and they are only ever seen
        // from inside the one chamber that holds them.
        const int Stones = 12;
        for (var i = 0; i < Stones; i++)
        {
            var a = Mathf.Tau * (i + 0.5f) / Stones;
            // fog: 0. Twelve of these each scattering into the volumetric fog
            // summed to a white-out — the chamber rendered as a milky blue void
            // with the orthostats silhouetted against it. Per-light fog energy is
            // ADDITIVE across every light that touches a froxel, so it is a
            // budget for one or two lights in a room, not a per-light setting.
            Lamp(Witchlight, Candela(10f), 620f, new Vector3(
                     wheel.MidX + Mathf.Cos(a) * (wheel.Width / 2f - Module * 1.5f),
                     wheel.FloorY + 40f,
                     wheel.MidZ + Mathf.Sin(a) * (wheel.Depth / 2f - Module * 1.5f)));
        }

        // The lights the dressing pass asked for — every vignette's own flame,
        // and every hero prop's. They land HERE rather than beside their props so
        // that one folder holds every light in the realm and the flicker below
        // covers all of them without having to go looking.
        foreach (var (at, colour, energy, range) in _vignetteLights)
            Lamp(colour, Candela(energy), range, at);

        Mist();
        Flicker();
    }

    /// <summary>
    /// Light one space in its era's own idiom, laid out FROM the space rather
    /// than from a list of positions — so a wider chamber gets more flames
    /// instead of the same few stretched thinner.
    /// </summary>
    private void LightSpace(Space space)
    {
        var (colour, energy, range, spacing) = space.Era switch
        {
            // Energy, range and SPACING move together, and they were all tuned
            // once the falloff was right. Under a true inverse square a sconce
            // stated at 3 cd lights its own wall and nothing else: at 12.5 m the
            // next sconce along contributed 0.03, so the floor between any two
            // flames was black and the realm read as a row of unrelated dots.
            // Pools have to OVERLAP to describe a room — brighter, further, and
            // closer together, while the falloff keeps the shape honest.
            Era.Minster => (Tallow, Candela(9.0f), 260f, 240f),
            // Era II is DYING light: fewer niches, and most of them out. The
            // gaps are the point — this is a place people stopped tending.
            Era.Souterrain => (Rushlight, Candela(6.0f), 220f, 360f),
            _ => (Witchlight, Candela(26.0f), 700f, 480f),
        };

        var height = space.FloorY + 96f;
        // Wall flames, walked along each face so a longer wall gets more.
        for (var x = space.X0 + spacing / 2f; x < space.X1; x += spacing)
        {
            WallFlame(colour, energy, range, new Vector3(x, height, space.Z0 + Standoff), 0f, space);
            WallFlame(colour, energy, range, new Vector3(x, height, space.Z1 - Standoff), Mathf.Pi, space);
        }
        for (var z = space.Z0 + spacing; z < space.Z1 - spacing / 2f; z += spacing)
        {
            WallFlame(colour, energy, range, new Vector3(space.X0 + Standoff, height, z), -Mathf.Pi / 2f, space);
            WallFlame(colour, energy, range, new Vector3(space.X1 - Standoff, height, z), Mathf.Pi / 2f, space);
        }

        // Braziers on a GRID, not one in the middle. The nave is 93 × 67 m, and a
        // single fire at its centre lit a circle about 15 m across and left the
        // other 85 m black — the room read as one orange blob in a void rather
        // than as a hall. Fires get laid out the way the sconces are: from the
        // room's size, so a bigger floor gets more of them.
        //
        // And each one gets a BASKET modelled under it. A light source with
        // nothing burning beneath it is a floating orb the instant volumetric fog
        // catches it, which is exactly how the first pass of this looked.
        if (space.Width > 600f && space.Depth > 600f && space.Era != Era.Cairn)
        {
            const float BrazierSpacing = 760f;
            var columns = Mathf.Max(1, Mathf.RoundToInt(space.Width / BrazierSpacing));
            var rows = Mathf.Max(1, Mathf.RoundToInt(space.Depth / BrazierSpacing));
            for (var i = 0; i < columns; i++)
            for (var j = 0; j < rows; j++)
            {
                var floor = new Vector3(Mathf.Lerp(space.X0, space.X1, (i + 0.5f) / columns),
                                        space.FloorY,
                                        Mathf.Lerp(space.Z0, space.Z1, (j + 0.5f) / rows));
                var brazier = Lamp(Ember, Candela(22f), 520f, floor + new Vector3(0f, 60f, 0f),
                                   // It has a fire in it, so this one may light air.
                                   fog: 1.2f);
                // Only the FIRST casts. Shadow passes are the expensive half of a
                // light, and a hall wants many fires and few shadows.
                if (i == 0 && j == 0)
                {
                    brazier.ShadowEnabled = true;
                    brazier.LightSize = 8f;
                    _shadowCount++;
                }
                Prop(_fireBasket, _scene.OnFloor(floor), 601 + i * 13 + j, yaw: 0f, loose: 8f);
            }
        }

        // Fill, not illumination: one box-projected interior probe per space
        // lifts the black corners into readable blue-grey and lets damp stone
        // catch a highlight, for a fraction of what another light would cost.
        _scene.Add(new ReflectionProbe
        {
            Position = new Vector3(space.MidX, space.FloorY + 120f, space.MidZ),
            Size = new Vector3(Mathf.Max(space.Width, 160f), 320f, Mathf.Max(space.Depth, 160f)),
            Interior = true,
            BoxProjection = true,
            UpdateMode = ReflectionProbe.UpdateModeEnum.Once,
            AmbientMode = ReflectionProbe.AmbientModeEnum.Color,
            AmbientColor = new Color(0.05f, 0.06f, 0.09f),
            AmbientColorEnergy = 1f,
        }, $"Probe_{space.Id}");
    }

    /// <summary>
    /// A flame on a wall: a spot aimed away from the stone. One shadow pass
    /// instead of six, and nothing is lost, because the wall behind it occludes
    /// nothing anyway.
    /// </summary>
    private void WallFlame(Color colour, float energy, float range, Vector3 at, float yaw, Space space)
    {
        var light = new SpotLight3D
        {
            Position = at,
            LightColor = colour,
            LightEnergy = energy,
            SpotRange = range,
            SpotAngle = 120f,
            SpotAttenuation = InverseSquare,
            // Light LOD, and the two halves of it are priced very differently.
            // SHADOWS are what cost, so they still stop at ~10 m. The light
            // itself is nearly free once it is not casting, so it must reach far
            // enough to describe the room it is in: fading it out at 720 units
            // meant that from the Ossuary's west door the far half of a 70 m hall
            // was not dim but absent, and the screen was black. Gone by 75 m now,
            // which is one chamber — past that there is a wall in the way anyway.
            DistanceFadeEnabled = true,
            DistanceFadeShadow = 240f,
            DistanceFadeBegin = 1400f,
            DistanceFadeLength = 400f,
            // Only the hero light of a chamber lights the fog. Every flame doing
            // so gives a uniform glow; one doing it gives defined shafts.
            LightVolumetricFogEnergy = 0f,
            ShadowEnabled = false,
        };
        light.RotationDegrees = new Vector3(-18f, Mathf.RadToDeg(yaw), 0f);
        light.Name = $"Flame_{space.Id}_{_lampCount++}";
        _lights.AddChild(light);
        Flammable(space.Id, light.Name, energy);
    }

    /// <summary>
    /// A lamp. <paramref name="fog"/> defaults to ZERO rather than to Godot's 1.0
    /// because a bare point light scattering into volumetric fog renders as a
    /// glowing orb hanging in mid-air — and where the light belongs to a coffin
    /// or a shrine, there is no lamp modelled there to justify one. Only lights
    /// with something visibly burning under them, and the lucernaria, light air.
    /// </summary>
    private OmniLight3D Lamp(Color colour, float energy, float range, Vector3 at, float fog = 0f)
    {
        var light = new OmniLight3D
        {
            Position = at,
            LightColor = colour,
            LightEnergy = energy,
            OmniRange = range,
            OmniAttenuation = InverseSquare,
            // Dual-paraboloid rather than a cubemap where one of these does cast:
            // two shadow passes instead of six, and at this scale of stone the
            // deformation it trades for is invisible.
            OmniShadowMode = OmniLight3D.ShadowMode.DualParaboloid,
            DistanceFadeEnabled = true,
            DistanceFadeShadow = 320f,
            DistanceFadeBegin = 1800f,
            DistanceFadeLength = 500f,
            LightVolumetricFogEnergy = fog,
            ShadowEnabled = false,
        };
        light.Name = $"Lamp_{_lampCount++}";
        _lights.AddChild(light);
        Flammable(Owning(at), light.Name, energy);
        return light;
    }

    /// <summary>
    /// A light shaft from the surface: cold, pale, and the only thing in the
    /// realm that is neither flame nor whatever burns in the cairn. The eye goes
    /// to it from any distance, and it defines which way is up.
    /// </summary>
    private void Lucernarium(Space space)
    {
        var shaft = new SpotLight3D
        {
            Position = new Vector3(space.MidX, space.FloorY + 900f, space.MidZ),
            LightColor = Daylight,
            LightEnergy = Candela(6f, decay: 1.0f),
            SpotRange = 1400f,
            SpotAngle = 22f,
            SpotAttenuation = 1.0f,
            ShadowEnabled = true,
            LightSize = 30f,
            // THIS one lights the fog, hard — the shaft is the point, and a
            // low global density plus one enormous per-light contribution is
            // how a visible beam is made without a milky screen.
            LightVolumetricFogEnergy = 48f,
        };
        shaft.RotationDegrees = new Vector3(-90f, 0f, 0f);
        shaft.Name = $"Lucernarium_{space.Id}";
        _lights.AddChild(shaft);
        _shadowCount++;

        // The dust falling through it, which is what makes a shaft read as air
        // rather than as a cone of paint. Particles bear no collision, so this
        // costs the simulation nothing.
        var motes = new GpuParticles3D
        {
            Position = new Vector3(space.MidX, space.FloorY + 300f, space.MidZ),
            Amount = 96,
            Lifetime = 11f,
            FixedFps = 30,
            // The auto-computed box is routinely too small and pops particles
            // out of existence at oblique angles; state it.
            VisibilityAabb = new Godot.Aabb(new Vector3(-160f, -300f, -160f), new Vector3(320f, 900f, 320f)),
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(90f, 300f, 90f),
                Gravity = new Vector3(0f, -6f, 0f),
                ScaleMin = 0.6f,
                ScaleMax = 2.2f,
                Color = new Color(0.85f, 0.88f, 0.95f, 0.5f),
            },
            DrawPass1 = new QuadMesh { Size = new Vector2(3f, 3f) },
        };
        motes.Name = $"Motes_{space.Id}";
        _lights.AddChild(motes);
    }

    /// <summary>
    /// The flames gutter — and the whole of how, because this is the one place a
    /// realm can spend its frame budget by accident.
    ///
    /// It moves ENERGY and nothing else. Godot caches a positional light's shadow
    /// map and invalidates it when the light moves or the geometry under it does,
    /// so a static stone interior pays its shadow cost roughly once. Jitter a
    /// light's transform and every shadow-caster near it re-renders every frame,
    /// for an effect that reads no better than a brightness curve. The particles
    /// are what should move; the light should only breathe.
    ///
    /// One AnimationPlayer per chamber, each at its own SpeedScale, so no two
    /// rooms pulse in phase — a realm that breathes as one is a realm that reads
    /// as a single animation rather than as a hundred separate fires.
    ///
    /// And no script anywhere: the realm is a plain Godot scene of built-in nodes
    /// (REALM-C), so this has to be autoplaying data or it cannot be here at all.
    /// </summary>
    private void Flicker()
    {
        var index = 0;
        foreach (var (owner, lamps) in _flames)
        {
            index++;
            if (lamps.Count == 0)
                continue;

            // 8–16 keys over 1.5–3 s, both per chamber. A fixed key count at a
            // fixed length is a texture of flicker; varying them is what stops
            // the eye finding the loop.
            var keys = 8 + (int)(Hash(index, 0, 901) * 9f);
            var length = 1.5f + Hash(index, 1, 903) * 1.5f;

            var anim = new Animation { Length = length, LoopMode = Animation.LoopModeEnum.Linear };
            var lamp = 0;
            foreach (var (name, energy) in lamps)
            {
                lamp++;
                var track = anim.AddTrack(Animation.TrackType.Value);
                anim.TrackSetPath(track, $"{name}:light_energy");
                anim.TrackSetInterpolationType(track, Animation.InterpolationType.Linear);
                for (var k = 0; k <= keys; k++)
                {
                    // The LAST key repeats the first exactly, or a looping
                    // animation snaps once a cycle — which is far more visible
                    // than the flicker itself.
                    var at = length * k / keys;
                    var phase = k == keys ? 0 : k;
                    // A flame DIPS more than it spikes: down to 0.72, up to 1.06.
                    // Symmetric noise around 1.0 reads as a dimmer being waggled.
                    //
                    // Keyed on the lamp's INDEX. It was keyed on the length of its
                    // name, and every flame in a chamber is named to the same
                    // width — so all six lights in the Porch read back at exactly
                    // 2.762, guttering in perfect unison. A room breathing as one
                    // is worse than a room not breathing at all: it reads as the
                    // lighting being animated rather than as fire.
                    var factor = 0.72f + Hash(lamp, phase, 905) * 0.34f;
                    anim.TrackInsertKey(track, at, energy * factor, 1f);
                }
            }

            var library = new AnimationLibrary();
            library.AddAnimation("flicker", anim);
            var player = new AnimationPlayer
            {
                Autoplay = "flicker",
                SpeedScale = 0.85f + Hash(index, 2, 907) * 0.4f,
                Name = $"Flicker_{owner}",
            };
            player.AddAnimationLibrary("", library);
            // Its RootNode defaults to the parent, which is the Lights folder —
            // so every track path above is just a sibling's name.
            _lights.AddChild(player);
        }
    }

    /// <summary>
    /// Ground mist: shallow FogVolume boxes lying in the chambers that should
    /// feel damp. Distinct from the Environment's volumetric fog, which fills the
    /// whole realm evenly — this is LOCAL, it has a floor and a top, and its
    /// height falloff is what makes it pool at ankle height rather than fill a
    /// room like smoke.
    ///
    /// Only the deep and the wet get it. Mist everywhere is haze; mist in three
    /// rooms is three rooms that are colder than the rest.
    /// </summary>
    private void Mist()
    {
        foreach (var (id, density, height) in new[]
                 {
                     ("B3", 0.020f, 150f), // the charnel, and the damp is the smell of it
                     ("B4", 0.034f, 260f), // the pit — deepest, wettest, and never lit
                     ("B5", 0.024f, 170f), // the deep gallery
                     ("B9", 0.007f, 220f), // the wheel — thin, it has 13 lights in it
                 })
        {
            var space = Named(id);
            _scene.Add(new FogVolume
            {
                Position = new Vector3(space.MidX, space.FloorY + height / 2f, space.MidZ),
                Size = new Vector3(space.Width, height, space.Depth),
                Material = new FogMaterial
                {
                    Density = density,
                    Albedo = new Color(0.74f, 0.78f, 0.86f),
                    // 2–4: high enough that the mist thins out well below head
                    // height, so a raider wades through it instead of swimming.
                    HeightFalloff = 3f,
                    // Without this the box has visible straight edges in mid-air,
                    // which is the single most common way volumetric fog gives
                    // itself away as a box.
                    EdgeFade = 0.4f,
                },
            }, $"Mist_{id}");
        }
    }

    private WorldEnvironment Environment() => new()
    {
        Environment = new Godot.Environment
        {
            // No sky. Underground, the background IS the absence of one.
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.008f, 0.008f, 0.012f),

            // Near-zero ambient: the probes do the filling. The first Crypt ran
            // 0.42 here, which lit every corner evenly and left the torches
            // nothing to do.
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.10f, 0.12f, 0.18f),
            AmbientLightEnergy = 0.20f,

            // FOG IS MEASURED IN THE REALM'S OWN UNITS, and this realm is not
            // metric: a raider is ~44 units and a 4 m door is 96, so ONE METRE IS
            // ABOUT 24 UNITS. Every fog number below therefore has to be divided
            // by 24 from anything an engine tutorial quotes, and the first cut of
            // this realm did not: at a density of 0.010 the optical depth across
            // the Ossuary was SIXTEEN, which is a transmission of about 5e-8. The
            // realm rendered as flat blue-grey with no geometry in it at all, and
            // every interior screenshot came out a solid black rectangle.
            //
            // The arithmetic that replaces the guess: aim for an optical depth
            // near 0.6 across a long chamber (1680 units), i.e. density ≈ 0.0004,
            // and roughly half that again for the exponential fog on top.
            FogEnabled = true,
            FogLightColor = new Color(0.04f, 0.05f, 0.09f),
            FogDensity = 0.00012f,

            // Long enough to cross a chamber, since that is the longest sight
            // line the realm ever offers. Godot's own default is 64 m, which is
            // 1536 units here — the 720 this was first set to is under half of
            // that, and the fog simply stopped in mid-air inside every big room.
            VolumetricFogEnabled = true,
            VolumetricFogLength = 1800f,
            VolumetricFogDensity = 0.0004f,
            VolumetricFogAnisotropy = 0.6f,
            VolumetricFogAlbedo = new Color(0.82f, 0.84f, 0.90f),

            // Glow blooms the FLAME, never the stone: any nonzero bloom lifts
            // the whole screen including the dark, which is the contrast this
            // realm is built out of.
            GlowEnabled = true,
            GlowBloom = 0f,
            GlowHdrThreshold = 1f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen,
            GlowIntensity = 0.35f,

            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapWhite = 3.2f,
        },
    };
}

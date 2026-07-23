using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The visible FIRE — the thing that was missing when the realm had 448 lights
/// and not one flame in it. A light with nothing burning under it reads as a
/// glow on a wall, and every torch in the Crypt was exactly that.
///
/// Two nodes per torch and no more: the <c>SpotLight3D</c> that already existed,
/// which does the illuminating, and one <c>GpuParticles3D</c> that does the
/// burning. Particles simulate themselves, so nothing here needs a script and
/// nothing here needs an animation track — which matters, because the realm is a
/// plain scene of built-in nodes (REALM-C) and always will be.
///
/// EVERY EXPENSIVE RESOURCE IS SHARED. One process material for all the wall
/// torches, one for the braziers, one quad, one material, one sprite, one ramp,
/// one curve. At 386 emitters the alternative is 386 copies of each in the .tscn,
/// and the two dust-mote emitters already in this file show what that looks
/// like: they each carry their own inline ParticleProcessMaterial.
///
/// The three ways this goes wrong, all of them silent:
///
///   A SOFT SPRITE. A gaussian blob is the single most common reason rendered
///   fire looks like a glowing ball of gas. Real flame has a hard, tapering,
///   pinching-off silhouette, so the sprite steps from opaque to nothing over 4%
///   of its radius rather than fading across the whole of it.
///
///   ADDITIVE BLENDING. Add on its own has no silhouette at all — everything it
///   touches only ever gets brighter, so the flame has no edge and reads as
///   luminous smoke. Mix keeps the shape; the HDR colour ramp does the glowing,
///   by crossing GlowHdrThreshold and blooming.
///
///   EMISSION UNDER UNSHADED. Setting EmissionEnabled on an Unshaded material
///   does nothing whatsoever — the fragment output is albedo, full stop. The
///   colour has to arrive as VERTEX COLOUR from the particle system, which in
///   turn does nothing unless VertexColorUseAsAlbedo is set. Two properties, and
///   with either one missing the flames come out black.
/// </summary>
public sealed partial class CryptDesign
{
    private GradientTexture2D _emberDisc = null!;
    private GradientTexture1D _flameRamp = null!;
    private CurveTexture _flameScale = null!;
    private QuadMesh _flameQuad = null!;
    private ParticleProcessMaterial _torchFire = null!;
    private ParticleProcessMaterial _brazierFire = null!;
    private Kit _torchBracket;
    private int _fireCount;

    /// <summary>Built once, referenced by every flame in the realm.</summary>
    private void FireResources()
    {
        // THE SPRITE. A radial gradient, but a hard-edged one: opaque out to
        // 52% of the radius, then a step, then gone by 88%. The corners of the
        // square sit at 0.707 — past the 0.5 fill radius — so they clamp to the
        // transparent last stop and no square edge is ever visible.
        _emberDisc = new GradientTexture2D
        {
            Gradient = new Gradient
            {
                InterpolationMode = Gradient.InterpolationModeEnum.Linear,
                InterpolationColorSpace = Gradient.ColorSpace.Srgb,
                Offsets = new[] { 0.00f, 0.52f, 0.60f, 0.84f, 0.88f },
                Colors = new[]
                {
                    new Color(1f, 1f, 1f, 1.00f),
                    new Color(1f, 1f, 1f, 1.00f),
                    new Color(1f, 1f, 1f, 0.74f),
                    new Color(1f, 1f, 1f, 0.55f),
                    new Color(1f, 1f, 1f, 0.00f),
                },
            },
            Width = 64,
            Height = 64,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1.0f, 0.5f),
            Repeat = GradientTexture2D.RepeatEnum.None,
            UseHdr = false, // it is a MASK; every colour comes from the ramp
        };

        // THE COLOUR OF COMBUSTION, over a particle's life. White-hot at the
        // base, then yellow, orange, deep red, and out. The values above 1.0 are
        // deliberate: they are what crosses GlowHdrThreshold and blooms, and they
        // are the only reason this looks hot rather than merely orange.
        _flameRamp = new GradientTexture1D
        {
            Gradient = new Gradient
            {
                InterpolationMode = Gradient.InterpolationModeEnum.Linear,
                InterpolationColorSpace = Gradient.ColorSpace.Srgb,
                Offsets = new[] { 0.00f, 0.12f, 0.35f, 0.65f, 0.88f, 1.00f },
                Colors = new[]
                {
                    new Color(7.00f, 5.400f, 3.000f, 1.00f),
                    new Color(5.20f, 2.600f, 0.750f, 1.00f),
                    new Color(2.60f, 0.900f, 0.160f, 0.95f),
                    new Color(0.90f, 0.220f, 0.035f, 0.60f),
                    new Color(0.22f, 0.045f, 0.010f, 0.22f),
                    new Color(0.05f, 0.010f, 0.002f, 0.00f),
                },
            },
            Width = 128,
            UseHdr = true,
        };

        // Small at birth, biggest a fifth of the way up, then pinching to
        // nothing. The taper is the silhouette, and the silhouette is the flame.
        var scale = new Curve { MinValue = 0f, MaxValue = 1f }; // set BEFORE AddPoint — it clamps
        scale.AddPoint(new Vector2(0.00f, 0.45f));
        scale.AddPoint(new Vector2(0.22f, 1.00f));
        scale.AddPoint(new Vector2(1.00f, 0.06f));
        _flameScale = new CurveTexture
        {
            Curve = scale,
            Width = 128,
            TextureMode = CurveTexture.TextureModeEnum.Rgb,
        };

        _flameQuad = new QuadMesh
        {
            // A vertical lozenge, not a square: at a random roll a tall quad
            // reads as a tongue of flame and a square one reads as a bubble.
            Size = new Vector2(2.6f, 4.4f),
            CenterOffset = Vector3.Zero,
            Material = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = BaseMaterial3D.BlendModeEnum.Mix,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                BillboardKeepScale = true,  // without it ScaleMin/Max/Curve are discarded
                VertexColorUseAsAlbedo = true, // without it the ramp is a no-op
                VertexColorIsSrgb = false,
                AlbedoColor = Colors.White,
                AlbedoTexture = _emberDisc,
                TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear,
                NoDepthTest = false,        // true draws flames through walls
                DisableFog = true,
                // Stops the billboard slicing visibly into the wall behind it.
                ProximityFadeEnabled = true,
                ProximityFadeDistance = 8f,
            },
        };

        _torchFire = Burn(sphere: 1.2f, spread: 12f, vMin: 4f, vMax: 9f, lift: 13f,
                          radialMin: -6f, radialMax: -2f, scaleMin: 0.80f, scaleMax: 1.35f);
        _brazierFire = Burn(sphere: 5.5f, spread: 26f, vMin: 7f, vMax: 16f, lift: 20f,
                            radialMin: -8f, radialMax: -3f, scaleMin: 1.40f, scaleMax: 2.40f);

        // The bracket the flame burns on. Without it a wall torch is a fire
        // floating four metres up bare masonry, which trades one legible bug for
        // another. KayKit Dungeon Remastered, CC0, already vendored.
        _torchBracket = Kit.Load("../../addons/kaykit_dungeon_remastered/Assets/gltf/torch.gltf.glb", 20f);
    }

    /// <summary>
    /// One fire's behaviour. Every length and every length-per-time here is in
    /// world units, so each is 24× the metric figure it stands for — gravity is
    /// POSITIVE because flame is buoyant, and 13 u/s² is 0.54 m/s² of lift.
    /// </summary>
    private ParticleProcessMaterial Burn(float sphere, float spread, float vMin, float vMax,
                                         float lift, float radialMin, float radialMax,
                                         float scaleMin, float scaleMax) => new()
    {
        EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
        EmissionSphereRadius = sphere,
        Direction = Vector3.Up,
        Spread = spread,
        Flatness = 0f,
        InitialVelocityMin = vMin,
        InitialVelocityMax = vMax,
        Gravity = new Vector3(0f, lift, 0f),
        DampingMin = 0f,
        DampingMax = 3f,
        // NEGATIVE radial acceleration pulls toward the emitter, which pinches
        // the column in and stalls the tip — that is what makes a flame taper
        // instead of spraying. Past about -12 it visibly collapses in on itself.
        RadialAccelMin = radialMin,
        RadialAccelMax = radialMax,
        AngleMin = -180f,
        AngleMax = 180f,
        AngularVelocityMin = -60f,
        AngularVelocityMax = 60f,
        ScaleMin = scaleMin,
        ScaleMax = scaleMax,
        ScaleCurve = _flameScale,
        ColorRamp = _flameRamp,
        HueVariationMin = -0.015f,
        HueVariationMax = 0.015f,
        LifetimeRandomness = 0.35f,
        // Godot's own docs: turbulence has a high GPU cost and should be on "a
        // few particle systems on screen at most". At 386 torches that is not a
        // judgement call. The wobble comes free from spread, angular velocity
        // and lifetime randomness instead.
        TurbulenceEnabled = false,
        ParticleFlagAlignY = false,
        ParticleFlagRotateY = false,
        ParticleFlagDisableZ = false,
        CollisionMode = ParticleProcessMaterial.CollisionModeEnum.Disabled,
    };

    /// <summary>
    /// One burning thing. <paramref name="box"/> is stated rather than left to
    /// Godot, whose default visibility AABB is 8 units cubed — a third of a metre
    /// at this realm's scale, smaller than the flame it is meant to bound. Left
    /// default it is not a performance setting but a CORRECTNESS bug: emitters
    /// vanish while their particles are still on screen.
    /// </summary>
    private void Fire(Vector3 at, ParticleProcessMaterial process, int amount,
                      double lifetime, Godot.Aabb box, Space space)
    {
        var i = _fireCount++;
        var fire = new GpuParticles3D
        {
            Position = at,
            Amount = amount,
            Lifetime = lifetime,
            Randomness = 0.35f,
            // The simulation runs on one frame in four and interpolates between,
            // which costs a single push constant and is invisible on something
            // this slow-moving.
            FixedFps = 15,
            Interpolate = true,
            FractDelta = true,
            LocalCoords = false,
            // De-sync, or every torch in the realm puffs on the same beat — the
            // same failure the chamber flicker had, in a different medium.
            SpeedScale = 0.92f + Hash(i, 5, 921) * 0.26f,
            Preprocess = Hash(i, 6, 923) * 0.9f,
            UseFixedSeed = true,
            Seed = (uint)(i + 1),
            DrawOrder = GpuParticles3D.DrawOrderEnum.Index,
            // The MATERIAL billboards these (BillboardModeEnum.Particles). Doing
            // it here as well would be wasted work at best.
            TransformAlign = GpuParticles3D.TransformAlignEnum.Disabled,
            DrawPass1 = _flameQuad,
            ProcessMaterial = process,
            VisibilityAabb = box,
            // A billboarded quad that casts shadows is a rotating black slab.
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            // Culling gates SIMULATION, not just drawing, so a torch behind a
            // wall or past 37 m costs nothing at all. The light itself fades from
            // 1400, so the flame dies well before its glow does.
            VisibilityRangeEnd = 900f,
            VisibilityRangeEndMargin = 120f,
            VisibilityRangeFadeMode = GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled,
            Emitting = true,
        };
        fire.Name = $"Fire_{space.Id}_{i}";
        _lights.AddChild(fire);
    }

    /// <summary>How high a brazier's rim stands, and so where its fire sits.</summary>
    private const float BrazierRim = 30f;

    /// <summary>
    /// A standing brazier, built from the realm's own stone rather than borrowed
    /// from a kit.
    ///
    /// Neither vendored kit has one. Kenney's fire basket samples a grass-green
    /// patch of a daylight atlas, and the dungeon torch this was swapped to is a
    /// WALL fitting — laying one on the floor put a sconce on its back with the
    /// flame a foot and a half above where it should be. A brazier is four boxes;
    /// borrowing the wrong object to avoid modelling it was the more expensive
    /// choice.
    ///
    /// Foot, stem, and a flared bowl of eight stones. The rim is what the fire
    /// sits in, so BrazierRim is the one number the flame placement reads.
    /// </summary>
    private ArrayMesh Brazier() => Piece("brazier", tool =>
    {
        Stone(tool, -17f, 0f, -17f, 17f, 7f, 17f, 0.70f);    // foot
        Stone(tool, -13f, 7f, -13f, 13f, 11f, 13f, 0.74f);   // step off the foot
        Stone(tool, -7f, 11f, -7f, 7f, 21f, 7f, 0.78f);      // stem

        // The bowl: eight stones round a ring, each canted outward so the rim
        // flares. A cylinder would read as a bucket.
        const int Stones = 8;
        for (var i = 0; i < Stones; i++)
        {
            var a = Mathf.Tau * i / Stones;
            var centre = new Vector3(Mathf.Cos(a) * 13f, 0f, Mathf.Sin(a) * 13f);
            var basis = new Basis(Vector3.Up, -a);
            var lo = centre + basis * new Vector3(-6.5f, 21f, -4f);
            var hi = centre + basis * new Vector3(6.5f, BrazierRim, 5f);
            Stone(tool, lo.Min(hi), lo.Max(hi), 0.72f + Jitter(i, 0, 931, 0.09f));
        }
        return "";
    });

    /// <summary>
    /// A wall torch: the bracket, angled off the wall, and the fire in its cup.
    ///
    /// The tilt is the whole point. A torch standing bolt upright with its base
    /// hanging in space beside a wall reads as a floating stick — the bracket has
    /// to LEAN, so its foot meets the stone and its head carries the flame out
    /// over the floor. That is also how a real sconce works: the weight is taken
    /// at the wall and the fire is held clear of it.
    ///
    /// The flame position is derived from the SAME basis the bracket is given.
    /// Placing the fire from the light's own position was what left it burning
    /// sixteen units clear of the wood the first time, and tilting the model
    /// without tilting the flame would do it again, sideways.
    /// </summary>
    private void TorchFire(Vector3 lightAt, float yaw, Space space)
    {
        // MEASURED, not guessed: torch.gltf.glb spans y -0.40..+0.64 in its own
        // units, so at scale 20 its foot is 8 BELOW the node origin and its cup
        // 13 above (tools/probe_torch.gd).
        const float BracketDrop = 26f;
        const float CupAboveBracket = 13f;
        const float FootBelowBracket = 8f;
        // 24 degrees off vertical. Enough that the foot plainly meets the wall
        // and the head is clearly out over the floor; much more and it reads as
        // falling off rather than as mounted.
        const float Lean = 0.42f;

        // Godot's euler order is YXZ, so the X term is applied AFTER the yaw and
        // therefore turns about the wall's own line — which is what tilts the
        // torch out into the room rather than sideways along the wall.
        var euler = new Vector3(Lean, yaw, 0f);
        var basis = Basis.FromEuler(euler);

        // Set the FOOT against the stone and let the lean carry everything else
        // outward, instead of pushing the whole model clear and leaving a gap.
        var inward = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
        var foot = lightAt - inward * Standoff - new Vector3(0f, BracketDrop, 0f);
        var origin = foot - basis * new Vector3(0f, -FootBelowBracket, 0f);

        var bracket = _torchBracket.Scene.Instantiate<Node3D>();
        bracket.Position = origin;
        bracket.Rotation = euler;
        bracket.Scale = new Vector3(_torchBracket.Scale, _torchBracket.Scale, _torchBracket.Scale);
        bracket.Name = $"Sconce_{space.Id}_{_fireCount}";
        _dressing.AddChild(bracket);

        Fire(origin + basis * new Vector3(0f, CupAboveBracket, 0f), _torchFire, 14, 0.85,
             new Godot.Aabb(new Vector3(-10f, -8f, -10f), new Vector3(20f, 34f, 20f)), space);
    }
}

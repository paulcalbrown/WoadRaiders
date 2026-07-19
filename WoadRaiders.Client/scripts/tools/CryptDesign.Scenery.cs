using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's LOOK and dressing — the half of the design that touches the
/// engine, kept apart from the engine-free layout math in CryptDesign.cs.
///
/// Everything here is this realm's alone: a starless dark instead of a sky,
/// torch-fed volumetric gloom, flagstone and ashlar materials grown from
/// cellular noise, the candle seas and soulfire, and the masonry and dead that
/// dress the carved stone. Deliberately PRIVATE — the next realm states its
/// own look; nothing here can leak into the game's defaults.
/// </summary>
public sealed partial class CryptDesign
{
    // ------------------------------------------------------------- the dark

    /// <summary>A buried world: no sky, black void above the rock mass, a cold
    /// whisper of ambient so shapes never fully drown, and TWO fogs — a plain
    /// depth fog that sinks far galleries, and volumetric fog near the camera
    /// that the torches pour their light into.</summary>
    private static void DressWithDark(RealmScene scene)
    {
        scene.Add(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.008f, 0.008f, 0.012f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.36f, 0.42f, 0.52f), // cold stone-blue
                AmbientLightEnergy = 0.30f,

                // Depth fog: distant galleries sink into the dark rather than
                // pop. Kept thin — the torch pools have to carry across a hall.
                FogEnabled = true,
                FogLightColor = new Color(0.022f, 0.026f, 0.034f),
                FogDensity = 0.00035f,

                // Volumetric gloom: the torch pools become visible air. Length
                // is tuned to the chase camera's ~430-unit boom — the froxel
                // buffer covers the played space, and the depth fog owns the
                // far. Density is per WORLD UNIT (this game's units are ~1/24
                // of a metre) and kept LOW: extinction compounds over hundreds
                // of units, and a thicker haze drank the torchlight whole.
                VolumetricFogEnabled = true,
                VolumetricFogDensity = 0.0008f,
                VolumetricFogAlbedo = new Color(0.42f, 0.44f, 0.50f),
                VolumetricFogLength = 1400f,
                VolumetricFogAmbientInject = 0.04f,

                // Contact shadow in the niches and under every lintel.
                SsaoEnabled = true,
                SsaoIntensity = 2.4f,
                SsaoRadius = 24f,

                // The flames bloom; the stone does not. The threshold sits just
                // under the unshaded flame billboards' peak, or nothing blooms.
                GlowEnabled = true,
                GlowIntensity = 0.55f,
                GlowBloom = 0.05f,
                GlowHdrThreshold = 0.88f,

                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        }, "Environment");

        // The one shaft of the upper world: pale cold light falling through the
        // collapsed ceiling over the entrance — the Crypt's only shadow-caster.
        scene.Add(new SpotLight3D
        {
            Position = ShaftMouth.ToGodot(),
            RotationDegrees = new Vector3(-90f, 0f, 0f),
            LightColor = new Color(0.72f, 0.80f, 1.0f),
            LightEnergy = 7f,
            SpotRange = 280f,
            SpotAngle = 26f,
            SpotAngleAttenuation = 1.6f,
            ShadowEnabled = true,
        }, "DaylightShaft");
    }

    // ------------------------------------------------------------- materials

    /// <summary>The Crypt's ground: worn flagstones grown from cellular noise —
    /// Distance2Sub reads as grout lines between slabs — with a matching relief
    /// normal map, world-triplanar so the pattern lies flat across every floor
    /// and shears naturally on the carved walls. Vertex colour carries the
    /// palette; this material reads it back.</summary>
    private static StandardMaterial3D CryptSurface()
    {
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.52f, 0.50f, 0.48f)); // grout shadow
        ramp.SetColor(1, new Color(1.04f, 1.03f, 1.00f)); // slab face
        ramp.AddPoint(0.22f, new Color(0.78f, 0.77f, 0.75f)); // after the ends — AddPoint reindexes

        var slabNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.Distance2Sub,
            CellularJitter = 0.9f,
            Frequency = 0.055f,
            Seed = 31,
        };
        var albedo = new NoiseTexture2D { Noise = slabNoise, Width = 512, Height = 512, Seamless = true, ColorRamp = ramp };

        var reliefNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.Distance2Sub,
            CellularJitter = 0.9f,
            Frequency = 0.055f,
            Seed = 31, // the same cells as the albedo — cracks shade their own grout
        };
        var normal = new NoiseTexture2D
        {
            Noise = reliefNoise, Width = 512, Height = 512, Seamless = true, AsNormalMap = true, BumpStrength = 6f,
        };

        return new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            Roughness = 0.96f,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.016f, 0.016f, 0.016f),
        };
    }

    /// <summary>Dressed ashlar for the piers, the span, and the sarcophagi:
    /// larger cellular blocks than the floor's slabs, a colder grey, deep
    /// joints.</summary>
    private static StandardMaterial3D Masonry()
    {
        var ramp = new Gradient();
        ramp.SetColor(0, new Color(0.16f, 0.16f, 0.19f));
        ramp.SetColor(1, new Color(0.42f, 0.41f, 0.44f));
        ramp.AddPoint(0.18f, new Color(0.27f, 0.26f, 0.29f)); // after the ends — AddPoint reindexes

        var blockNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.Distance2Sub,
            CellularJitter = 0.55f, // straighter joints — this stone was CUT
            Frequency = 0.035f,
            Seed = 47,
        };
        var albedo = new NoiseTexture2D { Noise = blockNoise, Width = 256, Height = 256, Seamless = true, ColorRamp = ramp };

        var jointNoise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular,
            CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.Distance2Sub,
            CellularJitter = 0.55f,
            Frequency = 0.035f,
            Seed = 47,
        };
        var normal = new NoiseTexture2D
        {
            Noise = jointNoise, Width = 256, Height = 256, Seamless = true, AsNormalMap = true, BumpStrength = 4f,
        };

        return new StandardMaterial3D
        {
            AlbedoTexture = albedo,
            NormalEnabled = true,
            NormalTexture = normal,
            Roughness = 0.92f,
            Uv1Triplanar = true,
            Uv1WorldTriplanar = true,
            Uv1Scale = new Vector3(0.014f, 0.014f, 0.014f),
        };
    }

    private static StandardMaterial3D DarkStone() => new()
    {
        AlbedoColor = new Color(0.10f, 0.10f, 0.12f), Roughness = 1f,
    };

    private static StandardMaterial3D Iron() => new()
    {
        AlbedoColor = new Color(0.09f, 0.08f, 0.08f), Roughness = 0.85f, Metallic = 0.4f,
    };

    private static StandardMaterial3D Bone() => new()
    {
        AlbedoColor = new Color(0.78f, 0.74f, 0.62f), Roughness = 0.9f,
    };

    // ------------------------------------------------------------- palette

    /// <summary>The Crypt's ground palette, baked into the terrain vertices.
    /// Position-aware (the x, z overload): the same depth wears bone-dust in
    /// the ossuary, moss at the cloister water, cold slate in the Mausoleum.
    /// Keyed to ABSOLUTE features of this realm's own layout — the room
    /// centres and elevations named in CryptDesign.cs — which is exactly why
    /// it is private to this design.</summary>
    private static Color CryptColour(float x, float z, float height, float normalY)
    {
        var flag = new Color(0.34f, 0.32f, 0.30f);   // walked flagstone
        var dust = new Color(0.38f, 0.34f, 0.28f);   // the entrance's dry earth-dust
        var boneDust = new Color(0.45f, 0.42f, 0.33f); // ossuary and charnel floors
        var moss = new Color(0.22f, 0.30f, 0.22f);   // the cloister's damp green
        var slate = new Color(0.27f, 0.27f, 0.33f);  // the mausoleum's cold stone
        var rockWall = new Color(0.115f, 0.11f, 0.13f); // carved faces
        var voidTop = new Color(0.030f, 0.030f, 0.040f); // the unexcavated mass

        // Region tints, blended by plan-view distance to this realm's rooms.
        static float Near(float px, float pz, float cx, float cz, float r, float fade) =>
            Mathf.Clamp(1f - (new Vector2(px, pz).DistanceTo(new Vector2(cx, cz)) - r) / fade, 0f, 1f);

        var floor = flag;
        floor = floor.Lerp(dust, Near(x, z, 520, 1800, 200f, 220f));
        floor = floor.Lerp(boneDust, Near(x, z, 1450, 680, 240f, 200f));           // the ossuary
        floor = floor.Lerp(boneDust, 0.8f * Near(x, z, 2380, 1500, 150f, 240f));   // the charnel chasm...
        floor = floor.Lerp(boneDust, 0.8f * Near(x, z, 2380, 1800, 150f, 240f));   // ...along its whole
        floor = floor.Lerp(boneDust, 0.8f * Near(x, z, 2380, 2100, 150f, 240f));   // ...north-south run
        floor = floor.Lerp(moss, Near(x, z, 1450, 2920, 260f, 240f));              // the flooded cloister
        floor = floor.Lerp(slate, Near(x, z, 2320, 3260, 340f, 260f));             // the mausoleum
        floor = floor.Lerp(slate, 0.7f * Near(x, z, 3000, 3060, 200f, 220f));      // the antechamber

        // Standing water stains the basin below the cloister's waterline.
        if (height < PoolWaterLevel + 4f)
            floor = floor.Lerp(new Color(0.10f, 0.14f, 0.12f),
                               Mathf.Clamp((PoolWaterLevel + 4f - height) / 8f, 0f, 1f));

        // The unexcavated mass reads as void: ground with no room influence
        // fades to near-black — keyed off the LAYOUT (PlayWeightAt), not
        // absolute height, so the roof stays void even over the deepest halls.
        var top = Mathf.Clamp((0.55f - PlayWeightAt(x, z)) / 0.45f, 0f, 1f);

        // Carved faces shed their dressing: steep ground is bare dark rock.
        var wall = Mathf.Clamp((0.80f - normalY) / 0.35f, 0f, 1f);

        return floor.Lerp(rockWall, wall).Lerp(voidTop, top);
    }

    // ------------------------------------------------------------- lights

    /// <summary>Set every burning thing in the Crypt: standing torches along
    /// the processional routes, candle pools where the dead were tended,
    /// soulfire where they tend themselves — then one looping flicker
    /// animation over all of it, saved into the scene (no scripts).</summary>
    private static void DressWithLights(RealmScene scene)
    {
        var lights = scene.Folder("Lights");

        var count = 0;
        foreach (var (x, z) in TorchSpots)
        {
            var torch = MakeTorch(scene.OnGround(x, z));
            torch.Name = $"Torch{count++}";
            lights.AddChild(torch);
        }

        count = 0;
        foreach (var (x, z) in CandleSpots)
        {
            var candles = MakeCandles(scene.OnGround(x, z), Hash((int)x, (int)z, 977));
            candles.Name = $"Candles{count++}";
            lights.AddChild(candles);
        }

        count = 0;
        foreach (var (x, z) in SoulfireSpots)
        {
            var fire = MakeSoulfire(scene.OnGround(x, z));
            fire.Name = $"Soulfire{count++}";
            lights.AddChild(fire);
        }

        scene.Add(MakeFlicker(lights), "Flicker");
    }

    /// <summary>A standing iron torch: a driven stake, a small cage, a flame,
    /// and a warm pool of light. Dozens burn at once, so no shadows.</summary>
    private static Node3D MakeTorch(Vector3 position)
    {
        var torch = new Node3D { Position = position };
        var iron = Iron();

        torch.AddChild(new MeshInstance3D
        {
            Name = "Stake",
            Mesh = new CylinderMesh { TopRadius = 2.2f, BottomRadius = 3.2f, Height = 58f, RadialSegments = 8, Material = iron },
            Position = new Vector3(0f, 29f, 0f),
        });
        torch.AddChild(new MeshInstance3D
        {
            Name = "Cage",
            Mesh = new CylinderMesh { TopRadius = 7f, BottomRadius = 4f, Height = 10f, RadialSegments = 8, Material = iron },
            Position = new Vector3(0f, 60f, 0f),
        });
        torch.AddChild(new OmniLight3D
        {
            Name = "Ember",
            Position = new Vector3(0f, 74f, 0f),
            LightColor = new Color(1.0f, 0.60f, 0.28f),
            LightEnergy = 9f,
            OmniRange = 400f,
            ShadowEnabled = false,
        });
        var flame = MakeCryptFlame(new Vector3(0f, 66f, 0f), 0.55f, warm: true);
        flame.Name = "Flame";
        torch.AddChild(flame);
        return torch;
    }

    /// <summary>A pool of votive candles: a handful of wax stubs (emissive, so
    /// they gutter in the glow), one shared small flame, one soft light.</summary>
    private static Node3D MakeCandles(Vector3 position, float jitter)
    {
        var cluster = new Node3D { Position = position };
        var wax = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.86f, 0.80f, 0.62f),
            EmissionEnabled = true,
            Emission = new Color(0.9f, 0.65f, 0.30f),
            EmissionEnergyMultiplier = 0.55f,
            Roughness = 0.7f,
        };

        var stubs = 4 + (int)(jitter * 3f);
        for (var k = 0; k < stubs; k++)
        {
            var ang = k * Mathf.Tau / stubs + jitter * 5f;
            var r = 6f + (k % 3) * 5f;
            cluster.AddChild(new MeshInstance3D
            {
                Name = $"Candle{k}",
                Mesh = new CylinderMesh
                {
                    TopRadius = 2.4f, BottomRadius = 2.8f,
                    Height = 6f + ((k * 7 + (int)(jitter * 10)) % 9),
                    RadialSegments = 6,
                    Material = wax,
                },
                Position = new Vector3(Mathf.Cos(ang) * r, 4f, Mathf.Sin(ang) * r),
            });
        }

        cluster.AddChild(new OmniLight3D
        {
            Name = "Glow",
            Position = new Vector3(0f, 22f, 0f),
            LightColor = new Color(1.0f, 0.70f, 0.36f),
            LightEnergy = 4f,
            OmniRange = 190f,
            ShadowEnabled = false,
        });
        var flame = MakeCryptFlame(new Vector3(0f, 12f, 0f), 0.3f, warm: true);
        flame.Name = "Flame";
        cluster.AddChild(flame);
        return cluster;
    }

    /// <summary>Soulfire: a black iron bowl burning cold green — the light the
    /// dead keep for themselves. Ossuary, charnel pit, and the Mausoleum ring.</summary>
    private static Node3D MakeSoulfire(Vector3 position)
    {
        var fire = new Node3D { Position = position };
        fire.AddChild(new MeshInstance3D
        {
            Name = "Bowl",
            Mesh = new CylinderMesh { TopRadius = 13f, BottomRadius = 7f, Height = 22f, RadialSegments = 10, Material = Iron() },
            Position = new Vector3(0f, 11f, 0f),
        });
        fire.AddChild(new OmniLight3D
        {
            Name = "Ember",
            Position = new Vector3(0f, 40f, 0f),
            LightColor = new Color(0.35f, 1.0f, 0.55f),
            LightEnergy = 8f,
            OmniRange = 400f,
            ShadowEnabled = false,
        });
        var flame = MakeCryptFlame(new Vector3(0f, 24f, 0f), 0.8f, warm: false);
        flame.Name = "Flame";
        fire.AddChild(flame);
        return fire;
    }

    /// <summary>The torch-flame recipe at crypt scale: the proven billboarded
    /// ember column, sized by <paramref name="scale"/>, in hearth-orange or
    /// soulfire green.</summary>
    private static GpuParticles3D MakeCryptFlame(Vector3 position, float scale, bool warm)
    {
        var colorRamp = new Gradient();
        if (warm)
        {
            colorRamp.SetColor(0, new Color(0.9f, 0.16f, 0.04f));
            colorRamp.SetColor(1, new Color(0.35f, 0.01f, 0.005f, 0f));
            colorRamp.AddPoint(0.45f, new Color(0.72f, 0.06f, 0.02f)); // after the ends — AddPoint reindexes
        }
        else
        {
            colorRamp.SetColor(0, new Color(0.10f, 0.85f, 0.35f));
            colorRamp.SetColor(1, new Color(0.01f, 0.25f, 0.10f, 0f));
            colorRamp.AddPoint(0.45f, new Color(0.05f, 0.55f, 0.25f)); // after the ends — AddPoint reindexes
        }

        var scaleCurve = new Curve();
        scaleCurve.AddPoint(new Vector2(0f, 1f));
        scaleCurve.AddPoint(new Vector2(0.5f, 0.4f));
        scaleCurve.AddPoint(new Vector2(1f, 0f));

        var dot = new Gradient();
        dot.SetColor(0, Colors.White);
        dot.SetColor(1, new Color(1f, 1f, 1f, 0f));
        dot.AddPoint(0.6f, Colors.White); // after the ends — AddPoint reindexes

        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 3f * scale,
            Direction = new Vector3(0f, 1f, 0f),
            Spread = 5f,
            Gravity = new Vector3(0f, 6f * scale, 0f),
            InitialVelocityMin = 34f * scale,
            InitialVelocityMax = 52f * scale,
            ScaleMin = 9f * scale,
            ScaleMax = 15f * scale,
            ScaleCurve = new CurveTexture { Curve = scaleCurve },
            Color = Colors.White,
            ColorRamp = new GradientTexture1D { Gradient = colorRamp },
        };

        var flameMaterial = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            AlbedoTexture = new GradientTexture2D
            {
                Gradient = dot, Width = 32, Height = 32,
                Fill = GradientTexture2D.FillEnum.Radial,
                FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(0.5f, 0f),
            },
        };

        return new GpuParticles3D
        {
            Position = position,
            Amount = 14,
            Lifetime = 0.6f,
            Randomness = 0.4f,
            Preprocess = 0.6f,
            ProcessMaterial = process,
            DrawPass1 = new QuadMesh { Material = flameMaterial, Size = new Vector2(1f, 1f) },
        };
    }

    /// <summary>One looping animation guttering every torch and soulfire ember
    /// — a value track per light, keys staggered by hash so no two flames
    /// breathe together. AnimationPlayer + autoplay ride in the saved scene,
    /// so the flicker needs no script at runtime.</summary>
    private static AnimationPlayer MakeFlicker(Node3D lights)
    {
        const float length = 2.8f;
        var anim = new Animation { Length = length, LoopMode = Animation.LoopModeEnum.Linear };

        var lightIndex = 0;
        foreach (var holder in lights.GetChildren())
        {
            if (holder.GetNodeOrNull<OmniLight3D>("Ember") is not { } ember)
                continue;
            var track = anim.AddTrack(Animation.TrackType.Value);
            anim.TrackSetPath(track, $"Lights/{holder.Name}/Ember:light_energy");

            var baseEnergy = ember.LightEnergy;
            var phase = Hash(lightIndex * 37, lightIndex * 91, 1409);
            const int keys = 6;
            for (var k = 0; k < keys; k++)
            {
                var wobble = 0.78f + 0.44f * Hash(lightIndex, k, 1423);
                anim.TrackInsertKey(track, (k + phase) / keys * length, baseEnergy * wobble);
            }
            // Close the loop where it began, so the seam never pops.
            anim.TrackInsertKey(track, 0f, baseEnergy * (0.78f + 0.44f * Hash(lightIndex, keys, 1423)));
            anim.TrackInsertKey(track, length, baseEnergy * (0.78f + 0.44f * Hash(lightIndex, keys, 1423)));
            lightIndex++;
        }

        var library = new AnimationLibrary();
        library.AddAnimation("gutter", anim);
        var player = new AnimationPlayer();
        player.AddAnimationLibrary("", library);
        player.Autoplay = "gutter";
        return player;
    }

    // ------------------------------------------------------------- masonry
    // Pure dressing past here: nothing the simulation ever sees.

    /// <summary>The worked stone the carving wears: portal frames at every
    /// doorway, vault ribs over the grand walks, burial niches lining the
    /// galleries, and the rubble of the collapse.</summary>
    private static void DressWithMasonry(RealmScene scene)
    {
        var masonry = scene.Folder("Masonry");
        var stone = Masonry();
        var dark = DarkStone();

        // Portal frames: two jambs and a lintel over every doorway in the
        // shared Doorways table — the jambs' collision is in Solids(), so the
        // frame a raider sees is the frame the server enforces. The jamb
        // visuals stand on their OWN ground height (a frame on a stair's foot
        // has jambs on different treads), so each piece is placed absolutely.
        foreach (var (x, z, alongX, width) in Doorways)
        {
            var frame = new Node3D { Name = $"Portal{masonry.GetChildCount()}", Position = new Vector3(x, 0f, z) };
            var half = width / 2f;
            var lintelBase = 0f;
            foreach (var side in new[] { -1f, 1f })
            {
                var (jx, jz) = alongX ? (x, z + side * half) : (x + side * half, z);
                var ground = scene.GroundAt(jx, jz);
                lintelBase = Mathf.Max(lintelBase, ground + 116f);
                frame.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(20f, 116f, 20f), Material = stone },
                    Position = new Vector3(jx - x, ground + 58f, jz - z),
                });
            }
            frame.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh
                {
                    Size = alongX ? new Vector3(24f, 26f, width + 40f) : new Vector3(width + 40f, 26f, 24f),
                    Material = stone,
                },
                Position = new Vector3(0f, lintelBase + 13f, 0f),
            });
            masonry.AddChild(frame);
        }

        // Vault ribs over the Processional: pairs of leaning sides and a
        // capstone, an arch's silhouette without an arch's mesh.
        foreach (var rx in new[] { 1860f, 2040f, 2220f, 2560f })
        {
            var ground = scene.GroundAt(rx, 1800);
            var rib = new Node3D { Name = $"Rib{masonry.GetChildCount()}", Position = new Vector3(rx, ground, 1800) };
            foreach (var side in new[] { -1f, 1f })
            {
                rib.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(18f, 96f, 22f), Material = stone },
                    Position = new Vector3(0f, 118f, side * 118f),
                    Rotation = new Vector3(side * 0.35f, 0f, 0f),
                });
            }
            rib.AddChild(new MeshInstance3D
            {
                Mesh = new BoxMesh { Size = new Vector3(18f, 20f, 190f), Material = stone },
                Position = new Vector3(0f, 168f, 0f),
            });
            masonry.AddChild(rib);
        }

        // Burial niches: columbarium blocks set against the gallery walls —
        // a dark recess under a stone frame, repeated the way loculi are.
        void NicheRow(float x0, float z0, float x1, float z1, float spacing)
        {
            var a = new Vector2(x0, z0);
            var b = new Vector2(x1, z1);
            var run = a.DistanceTo(b);
            var dir = (b - a).Normalized();
            for (var d = 0f; d <= run; d += spacing)
            {
                var p = a + dir * d;
                var pos = scene.OnGround(p.X, p.Y);
                var niche = new Node3D { Name = $"Niche{masonry.GetChildCount()}", Position = pos };
                // The recess: a dark box sunk toward the wall.
                niche.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(30f, 38f, 16f), Material = dark },
                    Position = new Vector3(0f, 26f, 0f),
                    Rotation = new Vector3(0f, Mathf.Atan2(dir.X, dir.Y), 0f),
                });
                // The frame: a proud stone surround.
                niche.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(40f, 8f, 20f), Material = stone },
                    Position = new Vector3(0f, 48f, 0f),
                    Rotation = new Vector3(0f, Mathf.Atan2(dir.X, dir.Y), 0f),
                });
                masonry.AddChild(niche);
            }
        }

        // The galleries' wall lines (the walkable edge, wall-side).
        NicheRow(1372, 1520, 1372, 1020, 96f);   // north gallery, west wall
        NicheRow(1528, 1520, 1528, 1020, 96f);   // north gallery, east wall
        NicheRow(1372, 2080, 1372, 2580, 96f);   // south gallery, west wall
        NicheRow(1528, 2080, 1528, 2580, 96f);   // south gallery, east wall
        NicheRow(2960, 1520, 3640, 1520, 128f);  // maze gallery H1's south wall
        NicheRow(2960, 2080, 3640, 2080, 128f);  // maze gallery H3's north wall
        NicheRow(2830, 3005, 3170, 3005, 110f);  // the antechamber's kings' wall

        // The collapse: rubble under the entrance shaft and at the chasm rims.
        var rubble = scene.Folder("Rubble");
        void RubbleField(float cx, float cz, float radius, int seedSalt, int count)
        {
            for (var k = 0; k < count; k++)
            {
                var ang = Hash(k, seedSalt, 733) * Mathf.Tau;
                var r = MathF.Sqrt(Hash(k, seedSalt, 739)) * radius;
                var px = cx + Mathf.Cos(ang) * r;
                var pz = cz + Mathf.Sin(ang) * r;
                var size = 6f + Hash(k, seedSalt, 743) * 14f;
                var chunk = new MeshInstance3D
                {
                    Name = $"Rubble{rubble.GetChildCount()}",
                    Mesh = new BoxMesh { Size = new Vector3(size, size * 0.7f, size * 0.85f), Material = stone },
                    Position = scene.OnGround(px, pz) + new Vector3(0f, size * 0.2f, 0f),
                    Rotation = new Vector3(Hash(k, seedSalt, 751) * 0.5f, Hash(k, seedSalt, 757) * Mathf.Tau, Hash(k, seedSalt, 761) * 0.5f),
                };
                chunk.AddToGroup("no_fade", persistent: true);
                rubble.AddChild(chunk);
            }
        }
        RubbleField(520, 1800, 130f, 1, 14);   // under the fallen shaft
        RubbleField(2380, 1420, 100f, 2, 8);   // the chasm's north lip
        RubbleField(2380, 2180, 100f, 3, 8);   // and its south lip
        RubbleField(2380, 1800, 110f, 4, 10);  // the pit floor's spill

        // The cloister's dark water, floated over the basin: still, glassy,
        // faintly green where the torchlight strikes it.
        scene.Add(new MeshInstance3D
        {
            Mesh = new PlaneMesh
            {
                Size = new Vector2(300f, 300f),
                Material = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.05f, 0.09f, 0.08f, 0.94f),
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    Metallic = 0.85f,
                    Roughness = 0.06f,
                    EmissionEnabled = true,
                    Emission = new Color(0.04f, 0.10f, 0.07f),
                    EmissionEnergyMultiplier = 0.35f,
                },
            },
            Position = new Vector3(1450f, PoolWaterLevel, 2920f),
        }, "CloisterWater");

        // Dust motes drifting in the entrance's one shaft of day.
        scene.Add(MakeDust(new Vector3(520f, 60f, 1800f)), "ShaftDust");
    }

    /// <summary>Slow dust drifting in the light shaft — the air of a place
    /// sealed for centuries, disturbed for the first time.</summary>
    private static GpuParticles3D MakeDust(Vector3 position)
    {
        var fade = new Gradient();
        fade.SetColor(0, new Color(1f, 1f, 1f, 0f));
        fade.SetColor(1, new Color(1f, 1f, 1f, 0f));
        fade.AddPoint(0.2f, new Color(0.9f, 0.92f, 1f, 0.5f)); // after the ends — AddPoint reindexes
        fade.AddPoint(0.8f, new Color(0.9f, 0.92f, 1f, 0.45f));

        var process = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(45f, 60f, 45f),
            Gravity = new Vector3(0f, -1.2f, 0f),
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 2.5f,
            Direction = new Vector3(0.3f, -1f, 0.2f),
            Spread = 180f,
            ScaleMin = 0.6f,
            ScaleMax = 1.6f,
            ColorRamp = new GradientTexture1D { Gradient = fade },
        };

        var mote = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            BillboardKeepScale = true,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = new Color(1f, 1f, 1f, 0.5f),
        };

        return new GpuParticles3D
        {
            Position = position,
            Amount = 40,
            Lifetime = 9f,
            Preprocess = 9f,
            Randomness = 0.8f,
            ProcessMaterial = process,
            DrawPass1 = new QuadMesh { Material = mote, Size = new Vector2(1.6f, 1.6f) },
        };
    }

    // ------------------------------------------------------------- the dead

    /// <summary>The interred and the spilled: bone heaps in the ossuary and the
    /// charnel pit, remains in the niches' shadows, scattered skulls where the
    /// dead were less carefully kept. Imported kit pieces join this pass once
    /// the asset kits land; the generated bones beneath them stay.</summary>
    private static void DressWithTheDead(RealmScene scene)
    {
        var dead = scene.Folder("TheDead");
        var bone = Bone();

        // A bone heap: a low mound of pale ellipsoids with a few skulls proud.
        void BoneHeap(float cx, float cz, float radius, int seedSalt)
        {
            var heap = new Node3D { Name = $"Bones{dead.GetChildCount()}", Position = scene.OnGround(cx, cz) };
            var mound = 5 + (int)(Hash(seedSalt, 3, 811) * 4f);
            for (var k = 0; k < mound; k++)
            {
                var ang = Hash(k, seedSalt, 821) * Mathf.Tau;
                var r = MathF.Sqrt(Hash(k, seedSalt, 823)) * radius;
                var size = 7f + Hash(k, seedSalt, 827) * 9f;
                heap.AddChild(new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 1f, Height = 1.1f, RadialSegments = 8, Rings = 5, Material = bone },
                    Position = new Vector3(Mathf.Cos(ang) * r, size * 0.28f, Mathf.Sin(ang) * r),
                    Scale = new Vector3(size, size * 0.45f, size * 0.8f),
                    Rotation = new Vector3(0f, Hash(k, seedSalt, 829) * Mathf.Tau, 0f),
                });
            }
            var skulls = 2 + (int)(Hash(seedSalt, 5, 839) * 3f);
            for (var k = 0; k < skulls; k++)
            {
                var ang = Hash(k, seedSalt, 841) * Mathf.Tau;
                var r = MathF.Sqrt(Hash(k, seedSalt, 853)) * radius * 0.8f;
                heap.AddChild(new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 4.2f, Height = 7.6f, RadialSegments = 10, Rings = 6, Material = bone },
                    Position = new Vector3(Mathf.Cos(ang) * r, 4.6f, Mathf.Sin(ang) * r),
                    Rotation = new Vector3(Hash(k, seedSalt, 857) * 0.6f - 0.3f, Hash(k, seedSalt, 859) * Mathf.Tau, 0f),
                });
            }
            dead.AddChild(heap);
        }

        // The ossuary: the bones of centuries, stacked and spilled.
        BoneHeap(1360, 600, 46f, 11);
        BoneHeap(1545, 615, 40f, 12);
        BoneHeap(1450, 830, 36f, 13);
        BoneHeap(1300, 730, 30f, 14);
        BoneHeap(1590, 760, 30f, 15);
        // The charnel pit: the nameless dead the collapse swallowed.
        BoneHeap(2350, 1560, 42f, 21);
        BoneHeap(2410, 1880, 46f, 22);
        BoneHeap(2340, 2080, 38f, 23);
        // Less careful corners: the maze's crossings, the stair's foot.
        BoneHeap(3060, 1440, 24f, 31);
        BoneHeap(3660, 1800, 24f, 32);
        BoneHeap(3360, 2160, 26f, 33);
        BoneHeap(1240, 1855, 22f, 34);
        // The antechamber and the Mausoleum's threshold.
        BoneHeap(2890, 3060, 26f, 41);
        BoneHeap(2540, 3260, 30f, 42);
    }
}

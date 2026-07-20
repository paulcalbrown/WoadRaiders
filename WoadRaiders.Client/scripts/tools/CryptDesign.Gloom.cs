using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The Crypt's light. A necropolis this size cannot be lit the way the first
/// small cut was — a handful of lamps that carried 400 units now leave most of
/// a chamber black — so the lighting is LAID OUT FROM THE CHAMBERS rather than
/// from a list of positions: every room walls itself with sconces at a fixed
/// world spacing and hangs lamps from its own roof, so a wider hall is lit by
/// more lights rather than by the same few stretched thinner.
///
/// The palette does the storytelling. Cold blue witchfire is the crypt's own
/// light, and it dominates; warm amber marks where the living have been —
/// the braziers at the span, the candles of the Mausoleum shrine, the fires
/// beside a doorway. The contrast is what makes either colour read at all.
///
/// Two lessons from the smaller crypt still hold. Fog density is PER UNIT, so
/// stretching the realm 3.16× would have made the old 0.0012 read three times
/// as thick and drown every lamp — it comes down accordingly. And a torch can
/// only "light" a scene that is otherwise dim, so ambient stays low even
/// though there is far more of it now.
/// </summary>
public sealed partial class CryptDesign
{
    private const float SconceSpacing = 900f;   // along a wall
    private const float SconceInset = 150f;     // in from the wall face
    private const float LampSpacing = 1250f;    // hanging lamps across a roof

    private static readonly Color Witchfire = new(0.48f, 0.70f, 1.0f);
    private static readonly Color Emberlight = new(1.0f, 0.62f, 0.28f);

    private void Gloom()
    {
        _scene.Add(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.01f, 0.01f, 0.02f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.34f, 0.38f, 0.52f),
                AmbientLightEnergy = 0.42f,
                FogEnabled = true,
                FogLightColor = new Color(0.05f, 0.07f, 0.12f),
                FogDensity = 0.00035f, // per unit: the realm got 3.16x longer
            },
        }, "Gloom");

        var lights = _scene.Folder("Lights");

        void Lamp(float x, float y, float z, Color colour, float energy, float range) =>
            lights.AddChild(new OmniLight3D
            {
                Position = new Vector3(x, y, z),
                LightColor = colour,
                LightEnergy = energy,
                OmniRange = range,
                // Dozens of lights at this scale: shadows are left to the few
                // hero lamps below, not paid for on every sconce.
                ShadowEnabled = false,
            });

        // ---- the chambers: sconces round the walls, lamps under the roof.
        foreach (var c in _chambers)
        {
            var tiers = c.TopY - c.FloorY > 520f
                ? new[] { c.FloorY + 210f, c.FloorY + 470f } // tall rooms light twice up the wall
                : new[] { c.FloorY + 210f };

            foreach (var y in tiers)
            {
                for (var x = c.X0 + SconceInset; x <= c.X1 - SconceInset; x += SconceSpacing)
                {
                    Lamp(x, y, c.Z0 + SconceInset, Witchfire, 3.4f, 1150f);
                    Lamp(x, y, c.Z1 - SconceInset, Witchfire, 3.4f, 1150f);
                }
                for (var z = c.Z0 + SconceInset + SconceSpacing; z <= c.Z1 - SconceInset - SconceSpacing;
                     z += SconceSpacing)
                {
                    Lamp(c.X0 + SconceInset, y, z, Witchfire, 3.4f, 1150f);
                    Lamp(c.X1 - SconceInset, y, z, Witchfire, 3.4f, 1150f);
                }
            }

            // Hung from the roof, so the middle of a great floor is not a void.
            for (var x = c.X0 + LampSpacing; x <= c.X1 - LampSpacing * 0.5f; x += LampSpacing)
                for (var z = c.Z0 + LampSpacing; z <= c.Z1 - LampSpacing * 0.5f; z += LampSpacing)
                    Lamp(x, c.TopY - 140f, z, Witchfire, 4.2f, 1500f);
        }

        // ---- the passages: a lamp every stride, following the stair down.
        foreach (var p in _passages)
        {
            var run = p.AlongZ ? p.Z1 - p.Z0 : p.X1 - p.X0;
            var steps = Mathf.Max(2, Mathf.RoundToInt(run / 800f));
            for (var i = 0; i <= steps; i++)
            {
                var f = i / (float)steps;
                var x = p.AlongZ ? (p.X0 + p.X1) * 0.5f : Mathf.Lerp(p.X0, p.X1, f);
                var z = p.AlongZ ? Mathf.Lerp(p.Z0, p.Z1, f) : (p.Z0 + p.Z1) * 0.5f;
                Lamp(x, p.TopY - 120f, z, Witchfire, 3.6f, 1000f);
            }
        }

        // ---- set pieces, in living firelight.
        // The span: braziers at both ends of the bridge, and a low glow in the
        // chasm so the fall reads as a place rather than a hole.
        var span = Named("span");
        var deck = S(-160f);
        Lamp(span.X0 + 220f, deck + 150f, span.MidZ, Emberlight, 7f, 1500f);
        Lamp(span.X1 - 220f, deck + 150f, span.MidZ, Emberlight, 7f, 1500f);
        Lamp(span.MidX, deck + 190f, span.MidZ, Emberlight, 5f, 1700f);
        Lamp(span.MidX, span.FloorY + 260f, span.Z0 + 500f, Witchfire, 4.5f, 1800f);
        Lamp(span.MidX, span.FloorY + 260f, span.Z1 - 500f, Witchfire, 4.5f, 1800f);

        // The Mausoleum: the shrine burns warm against the west wall, and the
        // dais is lit from above — the one shadow-casting light in the realm,
        // so the boss reads as standing on something.
        var court = Named("mausoleum");
        Lamp(court.X0 + 260f, court.FloorY + 240f, court.MidZ, Emberlight, 8f, 1700f);
        Lamp(court.X0 + 300f, court.FloorY + 180f, court.MidZ - 420f, Emberlight, 5f, 1100f);
        Lamp(court.X0 + 300f, court.FloorY + 180f, court.MidZ + 420f, Emberlight, 5f, 1100f);
        lights.AddChild(new OmniLight3D
        {
            Position = new Vector3(court.MidX, court.TopY - 220f, court.MidZ),
            LightColor = Witchfire,
            LightEnergy = 6.5f,
            OmniRange = 2200f,
            ShadowEnabled = true,
        });

        // The undercroft's doorway: the last warm fire before the descent.
        var entry = Named("undercroft");
        Lamp(entry.X1 - 320f, entry.FloorY + 200f, entry.MidZ - 300f, Emberlight, 6f, 1200f);
        Lamp(entry.X1 - 320f, entry.FloorY + 200f, entry.MidZ + 300f, Emberlight, 6f, 1200f);
    }
}

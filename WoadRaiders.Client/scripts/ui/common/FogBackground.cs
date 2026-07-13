using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Full-screen backdrop for menu screens: a near-black vertical gradient
/// with slow banks of woad-blue and sickly green fog, a sourceless moon-glow
/// from above, and a heavy vignette. All of it lives in one canvas shader;
/// the node is just the ColorRect carrying it.
/// </summary>
public partial class FogBackground : ColorRect
{
    private const string ShaderCode = """
        shader_type canvas_item;

        uniform vec3 sky_top : source_color = vec3(0.006, 0.009, 0.018);
        uniform vec3 sky_bottom : source_color = vec3(0.016, 0.030, 0.022);
        uniform vec3 fog_woad : source_color = vec3(0.10, 0.16, 0.30);
        uniform vec3 fog_green : source_color = vec3(0.07, 0.24, 0.08);

        float hash2(vec2 p) {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
        }

        float vnoise(vec2 p) {
            vec2 i = floor(p);
            vec2 f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(mix(hash2(i), hash2(i + vec2(1.0, 0.0)), u.x),
                       mix(hash2(i + vec2(0.0, 1.0)), hash2(i + vec2(1.0, 1.0)), u.x), u.y);
        }

        float fbm(vec2 p) {
            float v = 0.0;
            float a = 0.5;
            for (int i = 0; i < 4; i++) {
                v += a * vnoise(p);
                p = p * 2.03 + vec2(17.0);
                a *= 0.5;
            }
            return v;
        }

        void fragment() {
            vec3 col = mix(sky_top, sky_bottom, UV.y);

            // Two fog layers crawling in opposite directions, denser low down.
            float f1 = fbm(vec2(UV.x * 3.0 + TIME * 0.015, UV.y * 2.2 - TIME * 0.006));
            float f2 = fbm(vec2(UV.x * 5.5 - TIME * 0.010, UV.y * 4.5 + 3.7));
            float depth = smoothstep(0.25, 1.0, UV.y);
            col += fog_woad * f1 * f1 * 0.48 * (0.3 + 0.7 * depth);
            col += fog_green * f2 * f2 * 0.44 * depth;

            // Cold light bleeding in from somewhere above the frame.
            col += vec3(0.07, 0.09, 0.14) * smoothstep(0.9, 0.0, distance(UV, vec2(0.5, -0.1)));

            // Vignette closes the corners in.
            float vig = smoothstep(1.15, 0.35, distance(UV, vec2(0.5, 0.5)) * 1.35);
            col *= mix(0.50, 1.0, vig);

            COLOR = vec4(col, 1.0);
        }
        """;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // Anchors AND offsets: a plain anchors preset on an in-tree node keeps
        // its current (zero) rect, leaving the backdrop invisible.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Material = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
    }
}

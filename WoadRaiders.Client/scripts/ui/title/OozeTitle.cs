using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The animated game title. The label is rendered alone into a SubViewport so
/// its texture's alpha means "there is a glyph here", and a shader over that
/// texture grows drips of radioactive green ooze from the letters: each drip
/// lane swells, hangs a bulb, lets a drop fall, while a green soak creeps up
/// the glyph bottoms. A pulsing halo sits behind. The control only reserves
/// the glyph area in layout; the drip zone below overflows behind whatever
/// the screen stacks under it, so the ooze falls toward the menu.
/// </summary>
public partial class OozeTitle : Control
{
    private const int TexWidth = 1500;   // texture the title renders into
    private const int TexHeight = 430;   // includes the drip zone under the glyphs
    private const int VisibleHeight = 285; // what layout reserves: just the glyph area

    private const string OozeShaderCode = """
        shader_type canvas_item;

        // The texture is the title Label rendered alone, so alpha == glyph.
        // Lanes across the texture each run their own slow grow / hang / drop
        // cycle, hanging from whatever glyph sits above them.

        uniform vec4 ooze_bright : source_color = vec4(0.55, 1.0, 0.22, 1.0);
        uniform vec4 ooze_deep : source_color = vec4(0.16, 0.52, 0.10, 1.0);
        uniform float lanes = 88.0;     // drip lanes across the texture
        uniform float density = 0.5;    // fraction of lanes that ever drip
        uniform float reach = 0.40;     // longest drip, fraction of texture height
        uniform float wet_band = 0.055; // green soak height above glyph bottoms
        uniform float aspect = 3.5;     // texture width / height, keeps drops round

        float hash1(float n) {
            return fract(sin(n * 127.1 + 311.7) * 43758.5453123);
        }

        void fragment() {
            vec4 tex = texture(TEXTURE, UV);
            vec4 col = tex;

            float lane = floor(UV.x * lanes);
            float lane_x = (lane + 0.5) / lanes;
            float dx = (UV.x - lane_x) * lanes; // -0.5 .. 0.5 across the lane
            float to_lane = lanes / aspect;     // uv-height -> lane widths

            if (tex.a > 0.5) {
                // Inside a glyph: cold metal shading, then the soak — green
                // climbing up from every bottom edge as if the letters steep
                // in the stuff.
                col.rgb *= mix(1.10, 0.78, clamp(UV.y * 2.4, 0.0, 1.0));
                float soak = (1.0 - texture(TEXTURE, UV + vec2(0.0, wet_band * 0.25)).a) * 0.35
                           + (1.0 - texture(TEXTURE, UV + vec2(0.0, wet_band * 0.5)).a) * 0.30
                           + (1.0 - texture(TEXTURE, UV + vec2(0.0, wet_band * 0.75)).a) * 0.20
                           + (1.0 - texture(TEXTURE, UV + vec2(0.0, wet_band)).a) * 0.15;
                // Mostly the low strokes: without this, every ledge above a
                // counter (tops of O, D, R bowls) turns green too.
                soak *= 0.25 + 0.75 * smoothstep(0.18, 0.48, UV.y);
                float pulse = 0.75 + 0.25 * sin(TIME * 1.7 + lane * 0.9);
                vec3 wet = mix(ooze_deep.rgb, ooze_bright.rgb, 0.75 * pulse);
                col.rgb = mix(col.rgb, wet, clamp(soak, 0.0, 1.0) * 0.9);
            } else if (hash1(lane + 211.0) <= density) {
                // Below the glyphs: find the nearest glyph straight up this
                // lane; if one is close enough, this fragment may be ooze.
                float above = -1.0;
                for (int i = 1; i <= 30; i++) {
                    float d = (float(i) / 30.0) * reach;
                    if (UV.y - d < 0.0) { break; }
                    if (texture(TEXTURE, vec2(lane_x, UV.y - d)).a > 0.5) {
                        above = d;
                        break;
                    }
                }

                vec4 goo = vec4(0.0);
                if (above > 0.0) {
                    float h_len = hash1(lane);
                    float t = fract(TIME * (0.04 + 0.05 * hash1(lane + 57.0)) + hash1(lane + 113.0));
                    float grow = pow(smoothstep(0.0, 0.72, t), 1.7);
                    float release = smoothstep(0.90, 0.96, t);
                    float full = reach * (0.30 + 0.55 * h_len);
                    float len = full * grow * (1.0 - release);

                    // Keep the sway well under the tip width or thin streams
                    // shear into dashes.
                    float wob = sin(TIME * 1.1 + lane * 3.7 + UV.y * 6.0)
                              * 0.03 * min(above * to_lane, 1.5);
                    float ldx = abs(dx + wob);

                    if (above < len) {
                        float p = above / max(len, 1e-4);
                        float pulse = 0.8 + 0.2 * sin(TIME * 1.7 + lane * 0.9);
                        // Narrowing stream with a hanging drop swelling at the tip.
                        float w = mix(0.38, 0.14, p) + 0.30 * smoothstep(0.55, 1.0, p);
                        float body = smoothstep(w, w * 0.5, ldx);
                        vec3 rgb = mix(ooze_deep.rgb, ooze_bright.rgb, (0.50 + 0.50 * p) * pulse);
                        rgb += ooze_bright.rgb * 0.4 * smoothstep(0.14, 0.0, abs(dx + 0.08)); // slick highlight
                        goo = vec4(rgb, body);
                        // Faint radioactive haze hugging the stream.
                        goo.a = max(goo.a, 0.20 * smoothstep(w * 2.8, w, ldx));
                    }
                    if (release > 0.001) {
                        // The freed drop: falls away from the retracting stream
                        // and burns out before it leaves the lane's reach.
                        float fall = release * release * (reach - full);
                        vec2 dv = vec2(ldx, (above - (full + fall)) * to_lane);
                        float drop_a = smoothstep(0.34, 0.18, length(dv))
                                     * (1.0 - smoothstep(0.55, 1.0, release));
                        if (drop_a > goo.a) {
                            goo = vec4(mix(ooze_deep.rgb, ooze_bright.rgb, 0.85), drop_a);
                        }
                    }
                }

                col.rgb = mix(tex.rgb, goo.rgb, clamp(goo.a, 0.0, 1.0));
                col.a = max(tex.a, goo.a);
            }

            COLOR = col;
        }
        """;

    private const string HaloShaderCode = """
        shader_type canvas_item;

        uniform vec4 core_color : source_color = vec4(0.30, 0.85, 0.25, 1.0);
        uniform vec4 rim_color : source_color = vec4(0.25, 0.45, 0.90, 1.0);
        uniform float strength = 0.17;

        void fragment() {
            vec2 d = UV - vec2(0.5, 0.5);
            d.x *= 2.2; // wide ellipse behind the wordmark
            float r = length(d);
            float pulse = 0.82 + 0.18 * sin(TIME * 1.15);
            vec3 rgb = mix(core_color.rgb, rim_color.rgb, smoothstep(0.02, 0.42, r));
            COLOR = vec4(rgb, smoothstep(0.52, 0.02, r) * strength * pulse);
        }
        """;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(TexWidth, VisibleHeight);
        MouseFilter = MouseFilterEnum.Ignore;

        var viewport = new SubViewport
        {
            Size = new Vector2I(TexWidth, TexHeight),
            TransparentBg = true,
            Disable3D = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        AddChild(viewport);

        const string wordmark = "WOAD RAIDERS";
        var font = TitleTheme.DisplayFont();
        // As big as the texture allows: the bundled blackletter is narrow and
        // takes the full 265, the system-serif fallback is wider and shrinks.
        var fontSize = 265;
        float maxWidth = TexWidth - 90; // breathing room for the outline
        float width = font.GetStringSize(wordmark, HorizontalAlignment.Left, -1, fontSize).X;
        if (width > maxWidth)
            fontSize = (int)(fontSize * maxWidth / width);

        var label = new Label
        {
            Text = wordmark,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        label.SetAnchorsPreset(LayoutPreset.TopWide);
        // Pulled up into the font's internal leading so the pointed blackletter
        // feet stay inside the layout box instead of stabbing the tagline.
        label.OffsetTop = -15;
        label.AddThemeFontOverride("font", font);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", TitleTheme.BoneSilver);
        label.AddThemeColorOverride("font_outline_color", new Color(0.02f, 0.04f, 0.03f));
        label.AddThemeConstantOverride("outline_size", 14);
        viewport.AddChild(label);

        // Pulsing radioactive halo behind the glyphs, wider than the control.
        var halo = new ColorRect
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Material = new ShaderMaterial { Shader = new Shader { Code = HaloShaderCode } },
        };
        halo.SetAnchorsPreset(LayoutPreset.FullRect);
        halo.OffsetLeft = -140;
        halo.OffsetRight = 140;
        halo.OffsetTop = -120;
        halo.OffsetBottom = 120;
        AddChild(halo);

        var oozeMaterial = new ShaderMaterial { Shader = new Shader { Code = OozeShaderCode } };
        oozeMaterial.SetShaderParameter("aspect", (float)TexWidth / TexHeight);
        var rect = new TextureRect
        {
            Texture = viewport.GetTexture(),
            Material = oozeMaterial,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        // Taller than the control on purpose: the drip zone hangs below it.
        rect.SetAnchorsPreset(LayoutPreset.TopWide);
        rect.OffsetBottom = TexHeight;
        AddChild(rect);
    }
}

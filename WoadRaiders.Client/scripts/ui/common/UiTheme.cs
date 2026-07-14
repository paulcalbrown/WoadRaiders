using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// Shared palette, fonts and widget dressing for the game's Celtic/gothic UI.
/// The colors run the game's woad blue against a radioactive ooze green over
/// a near-black night. The display face prefers a real blackletter font file —
/// drop any .ttf/.otf into res://assets/fonts and it is picked up without a
/// code change — and otherwise condenses and emboldens the best installed
/// serif toward blackletter weight.
/// </summary>
public static class UiTheme
{
    public static readonly Color Night = new(0.012f, 0.016f, 0.024f); // matches the dungeon sky
    public static readonly Color WoadBlue = new(0.55f, 0.75f, 1f);
    public static readonly Color WoadDim = new(0.38f, 0.52f, 0.74f);
    public static readonly Color BoneSilver = new(0.80f, 0.85f, 0.94f);
    public static readonly Color OozeGreen = new(0.55f, 1f, 0.22f);
    public static readonly Color OozeDeep = new(0.10f, 0.42f, 0.06f);
    public static readonly Color Verdigris = new(0.42f, 0.68f, 0.38f);

    private static Font? _display;
    private static Font? _body;

    /// <summary>Heavy display face: titles and menu entries.</summary>
    public static Font DisplayFont() => _display ??= BuildDisplayFont();

    /// <summary>Book face: field labels, inputs, and body text.</summary>
    public static Font BodyFont() => _body ??= new SystemFont
    {
        FontNames = ["Palatino Linotype", "Constantia", "Georgia", "Times New Roman"],
    };

    private static Font BuildDisplayFont()
    {
        // A real blackletter needs no dressing up.
        if (LoadBundledFont() is { } bundled)
            return bundled;

        return new FontVariation
        {
            BaseFont = new SystemFont
            {
                FontNames = ["Old English Text MT", "Palatino Linotype", "Constantia", "Georgia"],
                FontWeight = 700,
            },
            // Condensed and over-inked, a bold serif approaches the dense
            // vertical color of blackletter. Kept moderate — heavier synthetic
            // embolden shoots faint spikes above the glyph extrema.
            VariationEmbolden = 0.30f,
            VariationTransform = new Transform2D(new Vector2(0.86f, 0f), new Vector2(0f, 1f), Vector2.Zero),
        };
    }

    /// <summary>Dress an input in the shared dark-panel look: woad border at
    /// rest; radioactive green border, glow and caret when focused.</summary>
    public static void StyleInput(LineEdit edit)
    {
        var normal = new StyleBoxFlat
        {
            BgColor = new Color(0.03f, 0.05f, 0.08f, 0.85f),
            BorderColor = new Color(WoadDim, 0.4f),
        };
        normal.SetBorderWidthAll(1);
        normal.SetContentMarginAll(8);
        var focus = new StyleBoxFlat
        {
            BgColor = new Color(0.04f, 0.08f, 0.06f, 0.9f),
            BorderColor = new Color(OozeGreen, 0.8f),
            ShadowColor = new Color(OozeGreen, 0.18f),
            ShadowSize = 10,
        };
        focus.SetBorderWidthAll(1);
        focus.SetContentMarginAll(8);

        edit.AddThemeStyleboxOverride("normal", normal);
        edit.AddThemeStyleboxOverride("focus", focus);
        edit.AddThemeFontOverride("font", BodyFont());
        edit.AddThemeFontSizeOverride("font_size", 20);
        edit.AddThemeColorOverride("font_color", BoneSilver);
        edit.AddThemeColorOverride("caret_color", OozeGreen);
        edit.AddThemeColorOverride("selection_color", new Color(OozeGreen, 0.3f));
    }

    private static Font? LoadBundledFont()
    {
        using var dir = DirAccess.Open("res://assets/fonts");
        if (dir == null)
            return null;
        foreach (var listed in dir.GetFiles())
        {
            // An exported pack does not contain the raw ttf: it lists the
            // import stub ("font.ttf.remap") whose source name ResourceLoader
            // still resolves. Strip the stub suffix or the extension check
            // below skips every bundled font in a release build (shipping the
            // system-font fallback instead — that bug hid until a screenshot
            // of an exported build was compared against the editor's).
            var file = listed;
            if (file.EndsWith(".remap"))
                file = file[..^".remap".Length];
            else if (file.EndsWith(".import"))
                file = file[..^".import".Length];
            if (!file.EndsWith(".ttf") && !file.EndsWith(".otf"))
                continue;
            var path = $"res://assets/fonts/{file}";
            // The imported resource exists once the editor has scanned the file
            // (and is all an exported build ships); reading the raw ttf covers
            // running from the CLI before that first import.
            if (ResourceLoader.Exists(path))
                return ResourceLoader.Load<FontFile>(path);
            var font = new FontFile();
            if (font.LoadDynamicFont(path) == Error.Ok)
                return font;
        }
        return null;
    }
}

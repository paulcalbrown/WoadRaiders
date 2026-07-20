using System;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// A schematic map of a realm, drawn from its real geometry: the floors as a
/// shaded relief (low halls glow deep, high courts pale, the void draws
/// nothing), the player spawn as a green sigil, the boss as a violet one,
/// enemy posts as ember dots. What the card shows is what the server
/// simulates — the preview can never drift from the map.
/// </summary>
public partial class DungeonMapView : Control
{
    private const float Pad = 14f; // breathing room inside the control

    // How far the relief ramp extends past the heights the cast stands at
    // (see PlayableBand) before ground reads as pit-dark or wild rock.
    private const float BandMargin = 60f;

    private RealmDefinition? _realm;

    /// <summary>Load and display the floorplan for a catalog realm.</summary>
    public void ShowDungeon(in DungeonInfo info)
    {
        // res:// so it works from the editor tree and an exported pck alike
        // (text-to-binary conversion is off in project settings so the .tscn
        // stays readable text in exports). A card must never crash the menu
        // over a map file — an unreadable map just draws blank.
        try
        {
            var text = Godot.FileAccess.GetFileAsString($"res://maps/{info.MapFile}");
            _realm = string.IsNullOrEmpty(text)
                ? null
                : info.MapFile.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                    ? RealmSceneFile.Parse(text, info.ScenePath)
                    : RealmDefinitionFile.Parse(text);
        }
        catch (Exception e)
        {
            GD.PushWarning($"map preview for '{info.MapFile}' failed to parse: {e.Message}");
            _realm = null;
        }
        QueueRedraw();
    }

    /// <summary>Enemy count and boss presence, for the card's stats line.</summary>
    public (int Enemies, bool HasBoss) Census =>
        _realm is { } g ? (g.EnemySpawns.Count, g.BossSpawn is not null) : (0, false);

    /// <summary>The band of heights the realm is PLAYED at: the span its
    /// spawn, camps, and boss stand on, padded by <see cref="BandMargin"/>.</summary>
    private static (float Deep, float Wild) PlayableBand(RealmDefinition g)
    {
        float lo = g.SpawnPoint.Y, hi = g.SpawnPoint.Y;
        void Fold(float y)
        {
            lo = Mathf.Min(lo, y);
            hi = Mathf.Max(hi, y);
        }
        foreach (var spawn in g.EnemySpawns)
            Fold(spawn.Position.Y);
        if (g.BossSpawn is { } boss)
            Fold(boss.Y);
        return (lo - BandMargin, hi + BandMargin);
    }

    public override void _Draw()
    {
        if (_realm is not { } g || g.Soup is null)
            return;

        // Fit the realm's bounds into this control, preserving aspect.
        var bounds = g.Bounds;
        var size = bounds.Size;
        var scale = Mathf.Min((Size.X - 2 * Pad) / size.X, (Size.Y - 2 * Pad) / size.Z);
        var origin = new Vector2(
            (Size.X - size.X * scale) / 2f - bounds.Min.X * scale,
            (Size.Y - size.Z * scale) / 2f - bounds.Min.Z * scale);
        Vector2 Map(float x, float z) => origin + new Vector2(x, z) * scale;

        // The floors themselves, as shaded relief sampled from the soup. The
        // shading bands come from the realm's OWN elevations — the heights its
        // cast stands at — so an open climb (the Crag) and a sunken descent
        // (the Crypt) both draw their walked ground in the ramp; where there is
        // no floor at all, the card stays dark: the void is the wall.
        {
            var (deepBand, wildBand) = PlayableBand(g);
            var swatch = MathF.Max(20f, MathF.Max(size.X, size.Z) / 96f); // world units per square
            var gorgeDark = new Color(0.03f, 0.05f, 0.10f);
            var lowland = new Color(0.10f, 0.22f, 0.20f);
            var upland = new Color(0.55f, 0.62f, 0.58f);
            var crag = new Color(0.10f, 0.11f, 0.16f);
            for (var z = bounds.Min.Z; z < bounds.Max.Z; z += swatch)
            {
                for (var x = bounds.Min.X; x < bounds.Max.X; x += swatch)
                {
                    // Sample from the top of the playable band DOWNWARD, so an
                    // indoor realm draws the halls its raiders walk rather than
                    // the rock roofing them over.
                    if (g.Soup.GroundBelow(x + swatch * 0.5f, z + swatch * 0.5f, wildBand, 0f) is not { } h)
                        continue;
                    var color = h < deepBand ? gorgeDark
                        : h > wildBand ? crag
                        : lowland.Lerp(upland, (h - deepBand) / (wildBand - deepBand));
                    var a = Map(x, z);
                    var b = Map(x + swatch, z + swatch);
                    DrawRect(new Rect2(a, b - a), color);
                }
            }
        }

        // The garrison: embers for the rank and file.
        var ember = new Color(0.85f, 0.35f, 0.2f, 0.8f);
        foreach (var spawn in g.EnemySpawns)
            DrawCircle(Map(spawn.Position.X, spawn.Position.Z), 2.5f, ember);

        // The way in, and what waits at the end.
        var spawnAt = Map(g.SpawnPoint.X, g.SpawnPoint.Z);
        DrawCircle(spawnAt, 6f, new Color(UiTheme.OozeGreen, 0.95f));
        DrawArc(spawnAt, 9f, 0, Mathf.Tau, 24, new Color(UiTheme.OozeGreen, 0.5f), 1.5f, true);
        if (g.BossSpawn is { } boss)
        {
            var bossAt = Map(boss.X, boss.Z);
            DrawCircle(bossAt, 7f, new Color(0.7f, 0.3f, 1f, 0.95f));
            DrawArc(bossAt, 11f, 0, Mathf.Tau, 24, new Color(0.7f, 0.3f, 1f, 0.5f), 1.5f, true);
        }
    }
}

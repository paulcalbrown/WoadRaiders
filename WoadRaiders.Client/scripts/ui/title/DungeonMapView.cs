using System;
using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// A schematic map of a realm, drawn from its real geometry: the terrain as a
/// shaded relief (glens glow, gorges darken, the wild crags frame it), wall
/// slabs as woad-line strokes, the player spawn as a green sigil, the boss as a
/// violet one, enemy posts as ember dots. What the card shows is what the
/// server simulates — the preview can never drift from the map.
/// </summary>
public partial class DungeonMapView : Control
{
    private const float Pad = 14f; // breathing room inside the control

    // Relief shading bands (world heights): below Deep is gorge-dark, above
    // Wild is crag-rock; the playable band in between ramps dark → pale.
    private const float DeepBand = -50f;
    private const float WildBand = 320f;

    private DungeonGeometry? _geometry;

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
            _geometry = string.IsNullOrEmpty(text)
                ? null
                : info.MapFile.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase)
                    ? DungeonSceneFile.Parse(text, info.ScenePath)
                    : DungeonGeometryFile.Parse(text);
        }
        catch (Exception e)
        {
            GD.PushWarning($"map preview for '{info.MapFile}' failed to parse: {e.Message}");
            _geometry = null;
        }
        QueueRedraw();
    }

    /// <summary>Enemy count and boss presence, for the card's stats line.</summary>
    public (int Enemies, bool HasBoss) Census =>
        _geometry is { } g ? (g.EnemySpawns.Count, g.BossSpawn is not null) : (0, false);

    public override void _Draw()
    {
        if (_geometry is not { } g || (g.Solids.Count == 0 && g.Terrain is null))
            return;

        // Fit the realm's bounds into this control, preserving aspect.
        var bounds = g.Bounds;
        var size = bounds.Size;
        var scale = Mathf.Min((Size.X - 2 * Pad) / size.X, (Size.Y - 2 * Pad) / size.Z);
        var origin = new Vector2(
            (Size.X - size.X * scale) / 2f - bounds.Min.X * scale,
            (Size.Y - size.Z * scale) / 2f - bounds.Min.Z * scale);
        Vector2 Map(float x, float z) => origin + new Vector2(x, z) * scale;

        // The land itself, as shaded relief.
        if (g.Terrain is { } terrain)
        {
            const int step = 2; // 2x2 samples per swatch keeps the draw cheap
            var gorgeDark = new Color(0.03f, 0.05f, 0.10f);
            var lowland = new Color(0.10f, 0.22f, 0.20f);
            var upland = new Color(0.55f, 0.62f, 0.58f);
            var crag = new Color(0.10f, 0.11f, 0.16f);
            for (var j = 0; j < terrain.Depth - 1; j += step)
            {
                for (var i = 0; i < terrain.Width - 1; i += step)
                {
                    var h = terrain.At(i, j);
                    var color = h < DeepBand ? gorgeDark
                        : h > WildBand ? crag
                        : lowland.Lerp(upland, (h - DeepBand) / (WildBand - DeepBand));
                    var x = terrain.OriginX + i * terrain.CellSize;
                    var z = terrain.OriginZ + j * terrain.CellSize;
                    var a = Map(x, z);
                    var b = Map(x + step * terrain.CellSize, z + step * terrain.CellSize);
                    DrawRect(new Rect2(a, b - a), color);
                }
            }
        }

        // The walls, as the mason left them.
        var wall = new Color(UiTheme.WoadDim, 0.85f);
        foreach (var solid in g.Solids)
        {
            var a = Map(solid.Min.X, solid.Min.Z);
            var b = Map(solid.Max.X, solid.Max.Z);
            DrawRect(new Rect2(a, b - a), wall);
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

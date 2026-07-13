using Godot;
using WoadRaiders.Core;

namespace WoadRaiders.Client;

/// <summary>
/// A schematic floorplan of a dungeon, drawn from its real collision geometry:
/// wall slabs as woad-line strokes, the player spawn as a green sigil, the boss
/// as a violet one, enemy posts as ember dots. What the card shows is what the
/// server simulates — the preview can never drift from the map.
/// </summary>
public partial class DungeonMapView : Control
{
    private const float Pad = 14f; // breathing room inside the control

    private DungeonGeometry? _geometry;

    /// <summary>Load and display the floorplan for a catalog dungeon.</summary>
    public void ShowDungeon(in DungeonInfo info)
    {
        // res:// so it works from the editor tree and an exported pck alike.
        var json = Godot.FileAccess.GetFileAsString($"res://maps/{info.MapFile}");
        _geometry = string.IsNullOrEmpty(json) ? null : DungeonGeometryFile.Parse(json);
        QueueRedraw();
    }

    /// <summary>Enemy count and boss presence, for the card's stats line.</summary>
    public (int Enemies, bool HasBoss) Census =>
        _geometry is { } g ? (g.EnemySpawns.Count, g.BossSpawn is not null) : (0, false);

    public override void _Draw()
    {
        if (_geometry is not { } g || g.Solids.Count == 0)
            return;

        // Fit the dungeon's bounds into this control, preserving aspect.
        var bounds = g.Bounds;
        var size = bounds.Size;
        var scale = Mathf.Min((Size.X - 2 * Pad) / size.X, (Size.Y - 2 * Pad) / size.Z);
        var origin = new Vector2(
            (Size.X - size.X * scale) / 2f - bounds.Min.X * scale,
            (Size.Y - size.Z * scale) / 2f - bounds.Min.Z * scale);
        Vector2 Map(float x, float z) => origin + new Vector2(x, z) * scale;

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

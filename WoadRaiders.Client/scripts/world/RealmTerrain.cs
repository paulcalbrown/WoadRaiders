using System;
using Godot;

namespace WoadRaiders.Client;

/// <summary>
/// The terrain of an open realm as a scene node: it stores the heightfield
/// (origin, cell size, and the row-major height samples) in the .tscn and
/// builds its smooth vertex-coloured mesh when it enters the tree — in the
/// editor (it is a tool script, so map makers see the land while placing
/// things) and in the game alike. The mesh lives on an owner-less child, so
/// saving the scene never bakes megabytes of vertices into the file.
///
/// Where it is used: hand-made realm scenes MAY use one as their terrain, as an
/// alternative to sculpting real meshes (Core.DungeonSceneFile reads its stored
/// properties, so such a scene needs no bake). GENERATED realms do not carry the
/// node — they bake a real ArrayMesh into the .tscn instead — so this class is
/// an AUTHORING convenience, not a step in any pipeline. The mesh itself is
/// built by <see cref="HeightFieldMesh"/>, which both routes share.
///
/// Nothing here is realm-specific, including its colours: the ground is shaded
/// by height RELATIVE to this field's own lowest and highest samples, so it
/// reads sensibly whether the realm sits at sea level or on a summit. A scene
/// wanting particular colours sculpts its own mesh and states them there. And
/// <see cref="CellSize"/>'s 40 is not a style choice but a contract: Core.
/// DungeonSceneFile mirrors that default when parsing, so changing it here
/// silently changes how every hand-made scene reads.
///
/// The node and its mesh join the "no_fade" group — the land is what everything
/// stands on; the occlusion fader must never dissolve it.
/// </summary>
[Tool]
public partial class RealmTerrain : Node3D
{
    [Export] public float OriginX { get; set; }
    [Export] public float OriginZ { get; set; }
    [Export] public float CellSize { get; set; } = 40f;
    [Export] public int TerrainWidth { get; set; }
    [Export] public int TerrainDepth { get; set; }

    /// <summary>Row-major height samples ([z * TerrainWidth + x]), world-space Y.</summary>
    [Export] public float[] Heights { get; set; } = Array.Empty<float>();

    private const string MeshChildName = "GeneratedTerrainMesh";

    public override void _Ready()
    {
        AddToGroup("no_fade");
        Rebuild();
    }

    /// <summary>A neutral ground shading: low ground green, high ground pale, and
    /// bare rock wherever the surface tips past walkable. Driven by RELATIVE
    /// height (0 at this field's lowest sample, 1 at its highest) rather than
    /// absolute world units, which is what lets one palette suit any realm.</summary>
    private static Color GroundColour(float height, float normalY, float lowest, float highest)
    {
        var span = highest - lowest;
        var t = span > 0.001f ? Mathf.Clamp((height - lowest) / span, 0f, 1f) : 0f;

        var low = new Color(0.24f, 0.30f, 0.19f);   // sheltered ground
        var high = new Color(0.34f, 0.33f, 0.25f);  // exposed upland
        var rock = new Color(0.33f, 0.32f, 0.35f);

        var rockiness = Mathf.Clamp((0.80f - normalY) / 0.35f, 0f, 1f);
        return low.Lerp(high, t).Lerp(rock * 0.9f, rockiness);
    }

    /// <summary>Build (or rebuild) the terrain mesh child from the stored heights.</summary>
    public void Rebuild()
    {
        if (GetNodeOrNull(MeshChildName) is Node stale)
            stale.QueueFree();

        if (TerrainWidth < 2 || TerrainDepth < 2 || Heights.Length != TerrainWidth * TerrainDepth)
        {
            if (Heights.Length > 0)
                GD.PushWarning($"RealmTerrain '{Name}': expected {TerrainWidth}x{TerrainDepth} " +
                               $"= {TerrainWidth * TerrainDepth} heights, got {Heights.Length} — not building");
            return;
        }

        // Shade against this field's OWN range, so the node needs no knowledge of
        // where in the world its realm sits.
        float lowest = float.MaxValue, highest = float.MinValue;
        foreach (var h in Heights)
        {
            lowest = Mathf.Min(lowest, h);
            highest = Mathf.Max(highest, h);
        }

        var child = new MeshInstance3D
        {
            Name = MeshChildName,
            Mesh = HeightFieldMesh.Build(OriginX, OriginZ, CellSize, TerrainWidth, TerrainDepth, Heights,
                                         (h, normalY) => GroundColour(h, normalY, lowest, highest)),
            MaterialOverride = TerrainSurface.Material(),
        };
        AddChild(child);           // no Owner: the editor never saves the generated mesh
        child.AddToGroup("no_fade");
    }
}

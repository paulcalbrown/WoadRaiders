using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Builds a <see cref="DungeonGeometry"/> straight from a Godot .tscn text
/// scene — the shared scene-to-geometry pipeline behind the map tools: the
/// bake tool (RealmBaker) reads a scene's markers, collision, and props
/// through it when baking server geometry JSON, and ValidateRealm accepts
/// scenes via it. Plain C#: unit-tested without an engine. (The server itself
/// hosts baked JSON — scenes are the AUTHORING format, not the serving one.)
///
/// Authoring conventions it reads (any scene built in the Godot editor):
///   - Solids:       CollisionShape3D nodes with a BoxShape3D (rotated boxes
///                   become their world AABB — keep them axis-aligned).
///   - Player spawn: a Marker3D named exactly "PlayerSpawn" (required).
///   - Enemy spawns: Marker3D nodes named "EnemySpawn*" — type from the name:
///                   contains "Rogue" → Rogue, "Mage" → Mage, else Minion.
///   - Boss:         a Marker3D named "BossSpawn" (optional).
///   - Terrain, first match wins:
///       1. metadata on any node (the generated realms' pure-built-in form —
///          usually the scene root): metadata/terrain_heights (row-major
///          PackedFloat32Array) + terrain_width/depth, with terrain_origin_x/z
///          (default 0) and terrain_cell_size (default 40) — all world-space,
///          independent of any node transform;
///       2. a RealmTerrain node (group "realm_terrain" or the RealmTerrain.cs
///          script) — its stored heightfield read verbatim;
///       3. arbitrary meshes in the "terrain" group need the in-Godot bake
///          tool instead (it samples the meshes and hands the result in via
///          <paramref name="sampledTerrain"/>).
///   - Braziers:     nodes in the group "brazier" (or named "Brazier*") become
///                   cosmetic fire props.
/// </summary>
public static class DungeonSceneFile
{
    public static DungeonGeometry Load(string path, string? scenePath = null) =>
        Parse(File.ReadAllText(path), scenePath ?? $"res://maps/{Path.GetFileName(path)}");

    public static DungeonGeometry Parse(string text, string? scenePath = null, HeightField? sampledTerrain = null)
    {
        var doc = TscnDocument.Parse(text);

        Vector3? spawn = null;
        Vector3? boss = null;
        var solids = new List<Aabb>();
        var enemySpawns = new List<EnemySpawnPoint>();
        var props = new List<DungeonProp>();
        HeightField? metadataTerrain = null;
        HeightField? realmTerrain = null;
        var terrainMeshes = 0;

        // World transform per node path, composed root-down (nodes appear in
        // tree order, parents before children).
        var transforms = new Dictionary<string, Xf> { [""] = Xf.Identity };

        foreach (var node in doc.Nodes)
        {
            var name = node.AttributeString("name") ?? "";
            var parent = node.AttributeString("parent");
            var path = parent switch
            {
                null => "",       // the scene root
                "." => name,
                _ => $"{parent}/{name}",
            };
            var parentPath = parent is null or "." ? "" : parent;
            if (!transforms.TryGetValue(parentPath, out var parentXf))
                parentXf = Xf.Identity; // orphaned parent path — tolerate

            var local = node.Properties.TryGetValue("transform", out var t) && t.Kind == TscnValue.ValueKind.Call
                ? Xf.FromTransform3D(t.Floats())
                : Xf.Identity;
            var world = parentXf.Compose(local);
            transforms[path] = world;

            var groups = Groups(node);
            var type = node.AttributeString("type");

            // Metadata terrain is orthogonal to what the node otherwise is
            // (it usually rides on the scene root): world-space by definition,
            // no transform coupling.
            if (node.Properties.ContainsKey("metadata/terrain_heights"))
                metadataTerrain ??= ReadMetadataTerrain(node, name);

            if (groups.Contains("realm_terrain") || HasRealmTerrainScript(doc, node))
            {
                if (!world.IsIdentity)
                    throw new InvalidDataException(
                        $"RealmTerrain node '{name}' is transformed — its heightfield ignores transforms; keep it at the origin");
                realmTerrain ??= ReadRealmTerrain(node, name);
            }
            else if (type == "MeshInstance3D" && groups.Contains("terrain"))
            {
                terrainMeshes++;
            }
            else if (IsBrazier(name, groups))
            {
                props.Add(new DungeonProp(PropType.Brazier, world.Origin));
            }
            else if (type == "CollisionShape3D" && TryReadBoxSize(doc, node, out var size))
            {
                solids.Add(WorldAabb(world, size * 0.5f));
            }
            else if (type == "Marker3D")
            {
                if (name == "PlayerSpawn")
                    spawn = world.Origin;
                else if (name.StartsWith("BossSpawn", StringComparison.Ordinal))
                    boss = world.Origin; // several markers: the last wins
                else if (name.StartsWith("EnemySpawn", StringComparison.Ordinal))
                    enemySpawns.Add(new EnemySpawnPoint(world.Origin, TypeFromName(name)));
            }
        }

        var terrain = metadataTerrain ?? realmTerrain ?? sampledTerrain;
        if (terrain is null && terrainMeshes > 0)
            throw new InvalidDataException(
                $"the scene's terrain is {terrainMeshes} mesh(es) in the 'terrain' group — sample them with the " +
                "in-Godot bake tool (WoadRaiders.Client/tools/bake_realm.gd), or use a RealmTerrain node");
        if (spawn is null)
            throw new InvalidDataException("the scene has no Marker3D named 'PlayerSpawn'");

        return new DungeonGeometry(spawn.Value, solids, enemySpawns, terrain)
        {
            ScenePath = scenePath,
            BossSpawn = boss,
            Props = props,
        };
    }

    private static HashSet<string> Groups(TscnDocument.Section node)
    {
        var groups = new HashSet<string>(StringComparer.Ordinal);
        if (node.Attributes.TryGetValue("groups", out var list) && list.Kind == TscnValue.ValueKind.List)
            foreach (var item in list.Items)
                if (item.Kind == TscnValue.ValueKind.Text)
                    groups.Add(item.AsString);
        return groups;
    }

    private static bool HasRealmTerrainScript(TscnDocument doc, TscnDocument.Section node)
    {
        if (!node.Properties.TryGetValue("script", out var script) || script.Kind != TscnValue.ValueKind.Call
            || script.CallName != "ExtResource" || script.Items.Count != 1)
            return false;
        var ext = doc.ExtResource(script.Items[0].AsString);
        return ext?.AttributeString("path")?.EndsWith("RealmTerrain.cs", StringComparison.Ordinal) == true;
    }

    /// <summary>Read the metadata heightfield convention — plain built-in node
    /// metadata, as the generated realms carry (usually on the scene root).</summary>
    private static HeightField ReadMetadataTerrain(TscnDocument.Section node, string name)
    {
        float Prop(string key, float fallback) =>
            node.Properties.TryGetValue($"metadata/{key}", out var v) ? v.AsFloat : fallback;

        var width = (int)Prop("terrain_width", 0);
        var depth = (int)Prop("terrain_depth", 0);
        var heights = node.Properties["metadata/terrain_heights"]
            is { Kind: TscnValue.ValueKind.Call, CallName: "PackedFloat32Array" } packed
            ? packed.Floats()
            : Array.Empty<float>();
        if (width < 2 || depth < 2 || heights.Length != width * depth)
            throw new InvalidDataException(
                $"node '{name}' carries {heights.Length} metadata terrain heights for a {width}x{depth} grid — " +
                "set metadata/terrain_width, terrain_depth, and terrain_heights consistently");

        return new HeightField(Prop("terrain_origin_x", 0f), Prop("terrain_origin_z", 0f),
                               Prop("terrain_cell_size", 40f), width, depth, heights);
    }

    /// <summary>Read the RealmTerrain node's exported heightfield. Properties the
    /// editor left at their defaults are absent from the file, so the defaults
    /// here MIRROR RealmTerrain.cs exactly.</summary>
    private static HeightField ReadRealmTerrain(TscnDocument.Section node, string name)
    {
        float Prop(string key, float fallback) =>
            node.Properties.TryGetValue(key, out var v) ? v.AsFloat : fallback;

        var width = (int)Prop("TerrainWidth", 0);
        var depth = (int)Prop("TerrainDepth", 0);
        var heights = node.Properties.TryGetValue("Heights", out var packed)
                      && packed is { Kind: TscnValue.ValueKind.Call, CallName: "PackedFloat32Array" }
            ? packed.Floats()
            : Array.Empty<float>();
        if (width < 2 || depth < 2 || heights.Length != width * depth)
            throw new InvalidDataException(
                $"RealmTerrain node '{name}' carries {heights.Length} heights for a {width}x{depth} grid — " +
                "set TerrainWidth/TerrainDepth/Heights consistently");

        return new HeightField(Prop("OriginX", 0f), Prop("OriginZ", 0f), Prop("CellSize", 40f),
                               width, depth, heights);
    }

    private static bool IsBrazier(string name, HashSet<string> groups) =>
        groups.Contains("brazier")
        || (name.StartsWith("Brazier", StringComparison.Ordinal)
            && !name.StartsWith("Braziers", StringComparison.Ordinal)); // "Braziers" = a folder node

    private static EnemyType TypeFromName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("rogue"))
            return EnemyType.Rogue;
        if (lower.Contains("mage"))
            return EnemyType.Mage;
        return EnemyType.Minion;
    }

    private static bool TryReadBoxSize(TscnDocument doc, TscnDocument.Section node, out Vector3 size)
    {
        size = default;
        if (!node.Properties.TryGetValue("shape", out var shape) || shape.Kind != TscnValue.ValueKind.Call
            || shape.CallName != "SubResource" || shape.Items.Count != 1)
            return false;
        var sub = doc.SubResource(shape.Items[0].AsString);
        if (sub?.AttributeString("type") != "BoxShape3D")
            return false;

        // Godot omits properties left at their defaults; a BoxShape3D defaults to 1x1x1.
        if (sub.Properties.TryGetValue("size", out var v)
            && v is { Kind: TscnValue.ValueKind.Call, CallName: "Vector3" })
        {
            var f = v.Floats();
            size = new Vector3(f[0], f[1], f[2]);
        }
        else
        {
            size = Vector3.One;
        }
        return true;
    }

    private static Aabb WorldAabb(in Xf world, Vector3 half)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var p = world.Apply(new Vector3(
                (corner & 1) == 0 ? -half.X : half.X,
                (corner & 2) == 0 ? -half.Y : half.Y,
                (corner & 4) == 0 ? -half.Z : half.Z));
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        return new Aabb(min, max);
    }

    /// <summary>A 3D transform as .tscn stores it: a row-major 3x3 basis + origin.</summary>
    private readonly struct Xf
    {
        private readonly Vector3 _r0, _r1, _r2;
        public readonly Vector3 Origin;

        public static readonly Xf Identity = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Zero);

        private Xf(Vector3 r0, Vector3 r1, Vector3 r2, Vector3 origin)
        {
            _r0 = r0;
            _r1 = r1;
            _r2 = r2;
            Origin = origin;
        }

        /// <summary>From the 12 floats of a Transform3D(...) literal (basis rows, then origin).</summary>
        public static Xf FromTransform3D(float[] m) => m.Length == 12
            ? new Xf(new Vector3(m[0], m[1], m[2]), new Vector3(m[3], m[4], m[5]),
                     new Vector3(m[6], m[7], m[8]), new Vector3(m[9], m[10], m[11]))
            : throw new InvalidDataException($"Transform3D needs 12 numbers, found {m.Length}");

        public Vector3 Apply(Vector3 p) =>
            new Vector3(Vector3.Dot(_r0, p), Vector3.Dot(_r1, p), Vector3.Dot(_r2, p)) + Origin;

        /// <summary>Rotate a direction (no translation) — for composing bases.</summary>
        private Vector3 Rotate(Vector3 p) => new(Vector3.Dot(_r0, p), Vector3.Dot(_r1, p), Vector3.Dot(_r2, p));

        public Xf Compose(in Xf child)
        {
            // Rows of (this * child): this's rows dotted with child's columns.
            var c0 = new Vector3(child._r0.X, child._r1.X, child._r2.X);
            var c1 = new Vector3(child._r0.Y, child._r1.Y, child._r2.Y);
            var c2 = new Vector3(child._r0.Z, child._r1.Z, child._r2.Z);
            return new Xf(
                new Vector3(Vector3.Dot(_r0, c0), Vector3.Dot(_r0, c1), Vector3.Dot(_r0, c2)),
                new Vector3(Vector3.Dot(_r1, c0), Vector3.Dot(_r1, c1), Vector3.Dot(_r1, c2)),
                new Vector3(Vector3.Dot(_r2, c0), Vector3.Dot(_r2, c1), Vector3.Dot(_r2, c2)),
                Rotate(child.Origin) + Origin);
        }

        public bool IsIdentity =>
            Vector3.Distance(_r0, Vector3.UnitX) < 1e-4f && Vector3.Distance(_r1, Vector3.UnitY) < 1e-4f &&
            Vector3.Distance(_r2, Vector3.UnitZ) < 1e-4f && Origin.Length() < 1e-4f;
    }
}

/// <summary>
/// Loads a map file by its format: .tscn scenes through the shared
/// scene-to-geometry pipeline, .json through the classic geometry format.
/// For the TOOLS (ValidateRealm accepts either); the server hosts JSON.
/// </summary>
public static class MapLoader
{
    public static DungeonGeometry Load(string path) =>
        Path.GetExtension(path).Equals(".tscn", StringComparison.OrdinalIgnoreCase)
            ? DungeonSceneFile.Load(path)
            : DungeonGeometryFile.Load(path);
}

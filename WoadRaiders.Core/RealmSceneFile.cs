using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Builds a <see cref="RealmDefinition"/> straight from a Godot .tscn text
/// scene — the shared scene-to-geometry pipeline behind the map tools: the
/// bake tool (RealmBaker) reads a scene's markers and geometry through it when
/// baking server geometry JSON, and ValidateRealm accepts scenes via it. Plain
/// C#: unit-tested without an engine. (The server itself hosts baked JSON —
/// scenes are the AUTHORING format, not the serving one.)
///
/// THE AUTHORING CONVENTIONS, in full — this comment is the one place they are
/// stated, and the rest of the pipeline points here rather than restating them.
/// There is exactly one naming rule in the format, and it is the spawn markers.
///
///   - Geometry:     every mesh the realm is MODELLED from, whatever it is and
///                   wherever it sits. No group, no name, no privileged mesh
///                   type. What is baked is not a curated subset — it is the
///                   realm. Instanced sub-scenes (kit props, braziers, bone
///                   piles) are DRESSING and are skipped whole: you model the
///                   architecture and you drop in the props, which is the line
///                   authors already draw. BoxMesh slabs parse straight from
///                   the scene text here; any OTHER mesh needs the in-Godot
///                   bake tool (Client RealmBaker), which samples real
///                   triangles and hands the whole soup in via
///                   <paramref name="sampledSoup"/> — it is also what enforces
///                   the instanced-sub-scene rule, since scene text alone
///                   cannot see inside an instance.
///   - Player spawn: a Marker3D named exactly "PlayerSpawn" (required).
///   - Enemy spawns: Marker3D nodes named "EnemySpawn*" — type from the name:
///                   contains "Rogue" → Rogue, "Mage" → Mage, else Minion.
///   - Boss:         a Marker3D named "BossSpawn" (optional).
///
/// Everything else the simulation needs is DERIVED, which is why there is so
/// little to remember:
///   - ground vs wall  a surface's own normal (TriangleSoup.WallNormalY, ~87°;
///                     deliberately not the navmesh's slope limit, so ground
///                     stays descendable at any grade).
///   - walkable        the navmesh's answer (NavMeshBuilder), from slope,
///                     headroom and step height.
///   - too small       nothing has to say so: Recast's voxels and agent-radius
///                     erosion discard sub-mover detail unaided.
/// The one thing geometry cannot state about itself is which way a surface
/// faces, so WINDING matters: counter-clockwise seen from above, or a floor
/// reads as an overhang. SoupBuilder keeps that straight for shapes it builds,
/// and the engine bake flips Godot's clockwise faces as it samples them.
///
/// Groups play no part in any of this. "no_fade" survives in the shipping
/// scenes, but it is a rendering hint for the occlusion fader alone and the
/// bake never looks at it.
/// </summary>
public static class RealmSceneFile
{
    public static RealmDefinition Load(string path, string? scenePath = null) =>
        Parse(File.ReadAllText(path), scenePath ?? $"res://maps/{Path.GetFileName(path)}");

    public static RealmDefinition Parse(string text, string? scenePath = null, TriangleSoup? sampledSoup = null)
    {
        var doc = TscnDocument.Parse(text);

        Vector3? spawn = null;
        Vector3? boss = null;
        var enemySpawns = new List<EnemySpawnPoint>();
        var builder = new SoupBuilder();
        var slabs = 0;
        var unsampledMeshes = 0;

        // World transform per node path, composed root-down (nodes appear in
        // tree order, parents before children).
        var transforms = new Dictionary<string, Xf> { [""] = Xf.Identity };
        Span<Vector3> corners = stackalloc Vector3[8];

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

            var type = node.AttributeString("type");

            if (type == "MeshInstance3D")
            {
                if (sampledSoup is not null)
                {
                    // The engine bake sampled every mesh in the scene already.
                }
                else if (TryReadBoxMeshSize(doc, node, out var size))
                {
                    for (var k = 0; k < 8; k++)
                        corners[k] = world.Apply(SoupBuilder.LocalCorner(k, size * 0.5f));
                    builder.AddBoxCorners(corners);
                    slabs++;
                }
                else
                {
                    unsampledMeshes++;
                }
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

        if (sampledSoup is null && unsampledMeshes > 0)
            throw new InvalidDataException(
                $"{unsampledMeshes} mesh(es) in this scene are not BoxMesh slabs — this engine-free reader can only " +
                "measure boxes from scene text; sample them with the in-Godot bake tool " +
                "(WoadRaiders.Client/tools/bake_realm.gd)");
        if (spawn is null)
            throw new InvalidDataException("the scene has no Marker3D named 'PlayerSpawn'");

        return new RealmDefinition(spawn.Value, sampledSoup ?? (slabs > 0 ? builder.Build() : null), enemySpawns)
        {
            ScenePath = scenePath,
            BossSpawn = boss,
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

    private static EnemyType TypeFromName(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.Contains("rogue"))
            return EnemyType.Rogue;
        if (lower.Contains("mage"))
            return EnemyType.Mage;
        return EnemyType.Minion;
    }

    private static bool TryReadBoxMeshSize(TscnDocument doc, TscnDocument.Section node, out Vector3 size)
    {
        size = default;
        if (!node.Properties.TryGetValue("mesh", out var mesh) || mesh.Kind != TscnValue.ValueKind.Call
            || mesh.CallName != "SubResource" || mesh.Items.Count != 1)
            return false;
        var sub = doc.SubResource(mesh.Items[0].AsString);
        if (sub?.AttributeString("type") != "BoxMesh")
            return false;

        // Godot omits properties left at their defaults; a BoxMesh defaults to 1x1x1.
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
    public static RealmDefinition Load(string path) =>
        Path.GetExtension(path).Equals(".tscn", StringComparison.OrdinalIgnoreCase)
            ? RealmSceneFile.Load(path)
            : RealmDefinitionFile.Load(path);
}

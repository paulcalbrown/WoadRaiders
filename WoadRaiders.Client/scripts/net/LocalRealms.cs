using Godot;
using WoadRaiders.Core;
using WoadRaiders.Shared;
using FileAccess = Godot.FileAccess;

namespace WoadRaiders.Client;

/// <summary>
/// The realms this build already carries, read off disk as the very packet the
/// server would otherwise have sent.
///
/// That framing is the whole trick. The client does not parse the map into some
/// parallel shape and hope it lines up — it assembles a
/// <see cref="RealmGeometryPacket"/> from the same baked JSON the server loads
/// and the same navmesh artifact the server loads, so
/// <see cref="RealmSnapshot.Digest"/> over it is byte-identical to the server's
/// by construction rather than by agreement. A matching digest then means the
/// two ends will move on the same polygons, which is the only claim worth
/// making: prediction that clamps to different stone from the server is
/// rubber-banding nobody can trace back to its cause.
///
/// A realm this build does NOT ship simply yields null, the join offers no
/// digest, and the server sends its geometry exactly as it always did. Every
/// failure here — a missing file, a truncated one, a realm the server invented
/// with --map — lands on that same safe path.
/// </summary>
public static class LocalRealms
{
    /// <summary>
    /// The geometry packet for a shipped realm, or null if this build has no
    /// copy of it (or cannot read the copy it has).
    /// </summary>
    public static RealmGeometryPacket? Load(DungeonId dungeon)
    {
        var info = DungeonCatalog.Of(dungeon);
        var json = FileAccess.GetFileAsString($"res://maps/{info.MapFile}");
        if (string.IsNullOrEmpty(json))
            return null;

        // The navmesh is a build artifact beside the map, not something baked
        // here: two machines baking Recast separately is exactly the agreement
        // this class exists to avoid needing.
        var navPath = $"res://maps/{System.IO.Path.GetFileNameWithoutExtension(info.MapFile)}.navmesh";
        var navMesh = FileAccess.GetFileAsBytes(navPath);
        if (navMesh is not { Length: > 0 })
            return null;

        try
        {
            var realm = RealmDefinitionFile.Parse(json);
            // The server names the scene from the map path it loaded; the JSON
            // carries the same string, so the digests agree on it too.
            return RealmSnapshot.From(realm, navMesh);
        }
        catch (System.Exception e)
        {
            // A corrupt or half-written map is not worth crashing a raid over —
            // say so and let the server send the realm.
            GD.PushWarning($"local realm {info.MapFile} unreadable ({e.Message}); asking the server for it");
            return null;
        }
    }
}

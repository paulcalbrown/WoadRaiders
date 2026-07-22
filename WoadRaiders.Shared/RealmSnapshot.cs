using System.Numerics;
using System.Security.Cryptography;
using LiteNetLib.Utils;
using WoadRaiders.Core;

namespace WoadRaiders.Shared;

/// <summary>
/// Projects a <see cref="RealmDefinition"/> into the <see cref="RealmGeometryPacket"/>
/// sent to a client on join, and rebuilds the realm from it on the receiving side. The
/// realm is immutable once loaded, so the server builds the packet once and
/// reuses it for every connection. Lives in Shared — the wire-protocol seam —
/// alongside <see cref="WorldSnapshot"/>, so both world↔packet projections sit
/// together and are testable in isolation (including as a round trip).
/// </summary>
public static class RealmSnapshot
{
    public static RealmGeometryPacket From(RealmDefinition realm, byte[]? navMesh = null)
    {
        var packet = new RealmGeometryPacket
        {
            SpawnX = realm.SpawnPoint.X,
            SpawnY = realm.SpawnPoint.Y,
            SpawnZ = realm.SpawnPoint.Z,
            ScenePath = realm.ScenePath ?? "",
            NavMesh = navMesh ?? Array.Empty<byte>(),
        };
        if (realm.Soup is { } soup)
        {
            packet.SoupVertices = soup.Vertices;
            packet.SoupTriangles = soup.Triangles;
        }
        return packet;
    }

    /// <summary>
    /// Rebuilds the realm's DATA from the packet — the client-side inverse of
    /// <see cref="From"/> (the soup floats cross the wire bit-exact). Enemy
    /// spawns are server-only and never on the wire, so the rebuilt definition
    /// carries none.
    /// </summary>
    public static RealmDefinition ToDefinition(RealmGeometryPacket packet)
    {
        var soup = packet.SoupTriangles.Length > 0
            ? new TriangleSoup(packet.SoupVertices, packet.SoupTriangles)
            : null;
        return new RealmDefinition(
            new Vector3(packet.SpawnX, packet.SpawnY, packet.SpawnZ),
            soup, Array.Empty<EnemySpawnPoint>())
        {
            ScenePath = string.IsNullOrEmpty(packet.ScenePath) ? null : packet.ScenePath,
        };
    }

    /// <summary>
    /// The geometry a peer MOVES on: the navmesh the server baked (identical
    /// bytes for every peer) over the soup rebuilt from this same packet — so
    /// prediction clamps to exactly the polygons the server does. Null when
    /// the packet ships no geometry (the flat test arena): the world falls
    /// back to its open-arena clamp rules, on both ends alike.
    /// </summary>
    public static IRealmGeometry? ToMovementGeometry(RealmGeometryPacket packet)
    {
        var realm = ToDefinition(packet);
        if (realm.Soup is not { } soup || packet.NavMesh.Length == 0)
            return null;
        return new RealmGeometry(NavMeshBuilder.Deserialize(packet.NavMesh), soup, realm.SpawnPoint);
    }

    /// <summary>
    /// A STABLE digest of everything the packet carries — the same bytes on
    /// every machine and every run, so it can be sent and compared across the
    /// wire. This is what lets a raider say "I already hold this realm" on join
    /// and be believed: the client builds the very packet the server would have
    /// sent, from its own shipped files, and digests THAT. Equal digests mean
    /// the two sides are moving on identical geometry, not merely similar.
    ///
    /// Distinct from <see cref="Fingerprint"/>, which is process-local and
    /// deliberately cannot leave the process. Both exist because they answer
    /// different questions: Fingerprint asks "is this the same map I was just
    /// given?", Digest asks "is the map I already have the one you would send?".
    ///
    /// SHA-256 for want of any reason to be cleverer — it is computed once per
    /// map at load, and a truncated or homebrew hash would only invite the
    /// reader to wonder about collisions. There is no adversary here: a client
    /// that lies about its digest desyncs nobody but itself, since the server
    /// remains authoritative over every position it publishes.
    /// </summary>
    public static byte[] Digest(RealmGeometryPacket packet)
    {
        var w = new NetDataWriter();
        // Field order is the packet's own, and the packet is what both ends
        // build — so nothing can drift between what is hashed and what is meant.
        w.Put(packet.SpawnX);
        w.Put(packet.SpawnY);
        w.Put(packet.SpawnZ);
        w.Put(packet.ScenePath);
        foreach (var v in packet.SoupVertices)
            w.Put(v);
        foreach (var t in packet.SoupTriangles)
            w.Put(t);
        w.Put(packet.NavMesh);
        return SHA256.HashData(w.CopyData());
    }

    /// <summary>True when two digests name the same realm. Length-safe, and a
    /// missing digest (a client that shipped no copy of this realm) is never a
    /// match — the geometry is sent, which is the safe direction to fail.</summary>
    public static bool DigestMatches(byte[]? a, byte[]? b) =>
        a is { Length: > 0 } && b is { Length: > 0 } && a.AsSpan().SequenceEqual(b);

    /// <summary>
    /// A fingerprint over everything the packet carries, so a client can tell
    /// whether a re-sent geometry is the same map or a different one (a reconnect
    /// can land on a restarted server serving a new arena). Process-local only —
    /// System.HashCode is seeded per process — which is exactly the lifetime of
    /// the comparison; never persist or send it.
    /// </summary>
    public static int Fingerprint(RealmGeometryPacket packet)
    {
        var hash = new HashCode();
        hash.Add(packet.SpawnX);
        hash.Add(packet.SpawnY);
        hash.Add(packet.SpawnZ);
        hash.Add(packet.ScenePath);
        foreach (var v in packet.SoupVertices)
            hash.Add(v);
        foreach (var t in packet.SoupTriangles)
            hash.Add(t);
        hash.AddBytes(packet.NavMesh);
        return hash.ToHashCode();
    }
}

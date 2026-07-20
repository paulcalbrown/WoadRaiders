using System;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

/// <summary>
/// The built-realm geometry wire format (v17): the triangle soup and the baked
/// navmesh must survive world → packet → wire → world bit-exact — prediction
/// moves on the client's rebuild.
/// </summary>
public class GeometrySnapshotTests
{
    private static RealmDefinition Realm()
    {
        var soup = new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-80, -20, -120), new Vector3(80, 0, 40)))
            .AddBox(new Aabb(new Vector3(0, 0, 0), new Vector3(40, 40, 40)))
            .Build();
        return new RealmDefinition(new Vector3(10, 0, 20), soup, Array.Empty<EnemySpawnPoint>())
        {
            ScenePath = "realm:crag",
        };
    }

    private static RealmGeometryPacket Roundtrip(RealmGeometryPacket packet)
    {
        var writer = new NetDataWriter();
        packet.Serialize(writer);
        var back = new RealmGeometryPacket();
        back.Deserialize(new NetDataReader(writer.Data, 0, writer.Length));
        return back;
    }

    [Fact]
    public void The_soup_survives_the_wire_bit_exact()
    {
        var realm = Realm();
        var packet = Roundtrip(RealmSnapshot.From(realm));
        var rebuilt = RealmSnapshot.ToDefinition(packet);

        Assert.Equal(realm.Soup!.Vertices, rebuilt.Soup!.Vertices);
        Assert.Equal(realm.Soup.Triangles, rebuilt.Soup.Triangles);
        Assert.Equal("realm:crag", rebuilt.ScenePath);
        Assert.Equal(realm.SpawnPoint, rebuilt.SpawnPoint);
        Assert.Equal(realm.Soup.TopSurfaceAt(10f, -100f), rebuilt.Soup.TopSurfaceAt(10f, -100f));
    }

    [Fact]
    public void A_flat_map_still_travels_without_a_soup()
    {
        var flat = new RealmDefinition(Vector3.Zero, null, Array.Empty<EnemySpawnPoint>());
        var packet = Roundtrip(RealmSnapshot.From(flat));

        Assert.Empty(packet.SoupVertices);
        Assert.Null(RealmSnapshot.ToDefinition(packet).Soup);
        Assert.Null(RealmSnapshot.ToMovementGeometry(packet)); // → the open-arena clamp rules
    }

    [Fact]
    public void The_baked_navmesh_ships_and_moves_identically_on_both_ends()
    {
        // Bake the way the server does at load, ship the bytes, and rebuild the
        // movement geometry the way a client does — then walk both. This is the
        // determinism contract: identical polygons on every peer.
        var realm = Realm();
        var baked = NavMeshBuilder.BuildMeshData(realm.Soup!);
        var packet = Roundtrip(RealmSnapshot.From(realm, NavMeshBuilder.Serialize(baked)));

        Assert.NotEmpty(packet.NavMesh);
        var clientSide = RealmSnapshot.ToMovementGeometry(packet);
        var serverSide = new RealmGeometry(NavMeshBuilder.ToNavMesh(baked), realm.Soup!, realm.SpawnPoint);
        Assert.IsType<RealmGeometry>(clientSide);

        var from = serverSide.Move(realm.SpawnPoint, Vector3.Zero);
        for (var i = 0; i < 20; i++)
        {
            var delta = new Vector3(SimConstants.PlayerMoveSpeed * SimConstants.TickDelta, 0, 0);
            var server = serverSide.Move(from, delta);
            var client = clientSide!.Move(from, delta);
            Assert.Equal(server, client); // bit-exact, or prediction drifts
            from = server;
        }
    }

    [Fact]
    public void Fingerprint_tells_realms_apart_by_their_geometry()
    {
        var a = RealmSnapshot.From(Realm());
        var b = RealmSnapshot.From(Realm());
        Assert.Equal(RealmSnapshot.Fingerprint(a), RealmSnapshot.Fingerprint(b));

        b.SoupVertices = (float[])b.SoupVertices.Clone();
        b.SoupVertices[4] += 1f; // one vertex differs → a different realm
        Assert.NotEqual(RealmSnapshot.Fingerprint(a), RealmSnapshot.Fingerprint(b));
    }

    [Fact]
    public void Hostile_lengths_are_rejected_not_allocated()
    {
        // A hand-rolled hostile stream: valid spawn + scene, then an absurd
        // vertex count. Deserialization must throw (the server's receive path
        // disconnects the sender), never allocate.
        var evilVerts = new NetDataWriter();
        evilVerts.Put(1f);
        evilVerts.Put(2f);
        evilVerts.Put(3f);
        evilVerts.Put("");
        evilVerts.Put(int.MaxValue);
        Assert.ThrowsAny<Exception>(() =>
            new RealmGeometryPacket().Deserialize(new NetDataReader(evilVerts.Data, 0, evilVerts.Length)));

        // Same for the navmesh blob on an otherwise-valid flat packet.
        var evilNavMesh = new NetDataWriter();
        evilNavMesh.Put(1f);
        evilNavMesh.Put(2f);
        evilNavMesh.Put(3f);
        evilNavMesh.Put("");
        evilNavMesh.Put(0);            // no vertices
        evilNavMesh.Put(0);            // no triangles
        evilNavMesh.Put(int.MaxValue); // an absurd navmesh
        Assert.ThrowsAny<Exception>(() =>
            new RealmGeometryPacket().Deserialize(new NetDataReader(evilNavMesh.Data, 0, evilNavMesh.Length)));
    }
}

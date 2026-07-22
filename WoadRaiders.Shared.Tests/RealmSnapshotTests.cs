using System;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class RealmSnapshotTests
{
    private static RealmDefinition SampleRealm() => new(
        new Vector3(1, 0, 3),
        new SoupBuilder()
            .AddBox(new Aabb(new Vector3(-50, -20, -50), new Vector3(50, 0, 50)))
            .AddBox(new Aabb(new Vector3(-5, 0, -5), new Vector3(5, 8, 5)))
            .Build(),
        new[] { new EnemySpawnPoint(new Vector3(100, 0, 0), EnemyType.Minion) })
    {
        ScenePath = "res://maps/Barrow.tscn",
    };

    [Fact]
    public void From_carries_spawn_scene_and_the_soup()
    {
        var realm = SampleRealm();

        var packet = RealmSnapshot.From(realm);

        Assert.Equal(1f, packet.SpawnX);
        Assert.Equal(0f, packet.SpawnY);
        Assert.Equal(3f, packet.SpawnZ);
        Assert.Equal("res://maps/Barrow.tscn", packet.ScenePath);
        Assert.Equal(realm.Soup!.Vertices, packet.SoupVertices);
        Assert.Equal(realm.Soup.Triangles, packet.SoupTriangles);
    }

    [Fact]
    public void From_maps_a_null_scene_path_to_empty_string()
    {
        var realm = new RealmDefinition(Vector3.Zero, null, Array.Empty<EnemySpawnPoint>());
        Assert.Equal("", RealmSnapshot.From(realm).ScenePath);
    }

    [Fact]
    public void Geometry_survives_the_full_wire_round_trip()
    {
        // Server side: project + serialize. Client side: deserialize + rebuild.
        // This is the invariant prediction depends on: the client must move
        // over exactly the triangles the server authored.
        var realm = SampleRealm();

        var writer = new NetDataWriter();
        RealmSnapshot.From(realm).Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var packet = new RealmGeometryPacket();
        packet.Deserialize(reader);

        var back = RealmSnapshot.ToDefinition(packet);

        Assert.Equal(realm.SpawnPoint, back.SpawnPoint);
        Assert.Equal(realm.ScenePath, back.ScenePath);
        Assert.Equal(realm.Soup!.Vertices, back.Soup!.Vertices);
        Assert.Equal(realm.Soup.Triangles, back.Soup.Triangles);
        Assert.Empty(back.EnemySpawns);            // spawns are server-only, never on the wire
    }

    [Fact]
    public void ToDefinition_maps_an_empty_scene_path_to_null()
    {
        // "" on the wire means "no authored scene"; the client's fallback rendering
        // keys off a null ScenePath.
        var bare = new RealmDefinition(Vector3.Zero, null, Array.Empty<EnemySpawnPoint>());
        Assert.Null(RealmSnapshot.ToDefinition(RealmSnapshot.From(bare)).ScenePath);
    }

    [Fact]
    public void Fingerprint_is_stable_for_identical_maps()
    {
        // Two packets independently projected from the same realm must match —
        // this is what lets a reconnect recognize "same map, keep the visuals".
        Assert.Equal(
            RealmSnapshot.Fingerprint(RealmSnapshot.From(SampleRealm())),
            RealmSnapshot.Fingerprint(RealmSnapshot.From(SampleRealm())));
    }

    [Fact]
    public void Fingerprint_changes_when_the_map_changes()
    {
        var baseline = RealmSnapshot.Fingerprint(RealmSnapshot.From(SampleRealm()));

        var movedSpawn = RealmSnapshot.From(SampleRealm());
        movedSpawn.SpawnX += 1f;
        Assert.NotEqual(baseline, RealmSnapshot.Fingerprint(movedSpawn));

        var movedWall = RealmSnapshot.From(SampleRealm());
        movedWall.SoupVertices = (float[])movedWall.SoupVertices.Clone();
        movedWall.SoupVertices[3] += 1f;
        Assert.NotEqual(baseline, RealmSnapshot.Fingerprint(movedWall));

        var otherScene = RealmSnapshot.From(SampleRealm());
        otherScene.ScenePath = "res://maps/Other.tscn";
        Assert.NotEqual(baseline, RealmSnapshot.Fingerprint(otherScene));
    }

    [Fact]
    public void Packet_survives_a_serialize_round_trip()
    {
        var packet = RealmSnapshot.From(SampleRealm());

        var writer = new NetDataWriter();
        packet.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new RealmGeometryPacket();
        back.Deserialize(reader);

        Assert.Equal(packet.SpawnX, back.SpawnX);
        Assert.Equal(packet.SpawnZ, back.SpawnZ);
        Assert.Equal(packet.ScenePath, back.ScenePath);
        Assert.Equal(packet.SoupVertices, back.SoupVertices);
        Assert.Equal(packet.SoupTriangles, back.SoupTriangles);
    }

    // The digest is what a client stakes "I already hold this realm" on, so the
    // property that matters is not that equal realms agree — it is that UNEQUAL
    // ones cannot. A digest blind to any field it covers would let a client
    // predict on different stone from the server, and the symptom would be
    // rubber-banding with no visible cause.
    public class Digest
    {
        private static RealmGeometryPacket Sample() =>
            RealmSnapshot.From(SampleRealm(), new byte[] { 1, 2, 3, 4 });

        [Fact]
        public void Is_stable_across_calls_and_equal_for_equal_realms()
        {
            var a = RealmSnapshot.Digest(Sample());
            var b = RealmSnapshot.Digest(Sample());

            Assert.Equal(32, a.Length); // SHA-256
            Assert.Equal(a, b);
            Assert.True(RealmSnapshot.DigestMatches(a, b));
        }

        [Fact]
        public void Changes_when_any_covered_field_changes()
        {
            var baseline = RealmSnapshot.Digest(Sample());

            var moved = Sample();
            moved.SpawnX += 0.5f;

            var renamed = Sample();
            renamed.ScenePath = "res://maps/Elsewhere.tscn";

            var reshaped = Sample();
            reshaped.SoupVertices = (float[])reshaped.SoupVertices.Clone();
            reshaped.SoupVertices[0] += 0.01f; // one corner, one hundredth of a unit

            var retriangulated = Sample();
            retriangulated.SoupTriangles = (int[])retriangulated.SoupTriangles.Clone();
            (retriangulated.SoupTriangles[0], retriangulated.SoupTriangles[1]) =
                (retriangulated.SoupTriangles[1], retriangulated.SoupTriangles[0]); // winding flip

            var rebaked = Sample();
            rebaked.NavMesh = new byte[] { 1, 2, 3, 5 };

            foreach (var changed in new[] { moved, renamed, reshaped, retriangulated, rebaked })
                Assert.NotEqual(baseline, RealmSnapshot.Digest(changed));
        }

        [Fact]
        public void An_absent_digest_is_never_a_match()
        {
            // A client shipping no copy of this realm sends nothing, and nothing
            // must never read as agreement — the geometry has to be sent.
            var real = RealmSnapshot.Digest(Sample());

            Assert.False(RealmSnapshot.DigestMatches(Array.Empty<byte>(), real));
            Assert.False(RealmSnapshot.DigestMatches(real, Array.Empty<byte>()));
            Assert.False(RealmSnapshot.DigestMatches(null, real));
            Assert.False(RealmSnapshot.DigestMatches(real, null));
            Assert.False(RealmSnapshot.DigestMatches(Array.Empty<byte>(), Array.Empty<byte>()));
        }

        [Fact]
        public void Survives_the_join_packet_round_trip()
        {
            // The digest is only useful if it crosses the wire intact.
            var join = new JoinRequest { Name = "Bran", RealmDigest = RealmSnapshot.Digest(Sample()) };
            var writer = new NetDataWriter();
            join.Serialize(writer);

            var reader = new NetDataReader();
            reader.SetSource(writer);
            var back = new JoinRequest();
            back.Deserialize(reader);

            Assert.Equal(join.RealmDigest, back.RealmDigest);
            Assert.True(RealmSnapshot.DigestMatches(back.RealmDigest, RealmSnapshot.Digest(Sample())));
        }
    }
}

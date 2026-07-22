using System;
using System.Linq;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

/// <summary>
/// A realm is only sent on the fallback path, but when it is sent it has to
/// arrive however large the realm has grown. One fragmented reliable message
/// cannot promise that: its ceiling is 65535 fragments times the NEGOTIATED
/// MTU, so it moves with the path (measured on loopback by
/// tools/MeasureReliableLimit.cs — 62 MB intact, 64 MB refused). Chunking is
/// what makes realm size a design decision rather than a property of the
/// player's network.
/// </summary>
public class GeometryChunksTests
{
    private static RealmGeometryPacket Realm(int boxes, int navMeshBytes)
    {
        var builder = new SoupBuilder();
        for (var i = 0; i < boxes; i++)
            builder.AddBox(new Aabb(new Vector3(i * 10, 0, 0), new Vector3(i * 10 + 8, 20, 8)));

        // RANDOM, not a pattern: RealmGeometryPacket brotli-compresses on
        // serialize, so a regular fixture collapses to a couple of kilobytes and
        // never exercises the split at all. Seeded, so the test is deterministic,
        // and still detects a bad splice — random bytes are the least forgiving
        // thing to reassemble wrongly.
        var navMesh = new byte[navMeshBytes];
        new Random(4242).NextBytes(navMesh);

        return RealmSnapshot.From(
            new RealmDefinition(new Vector3(1, 2, 3), builder.Build(), Array.Empty<EnemySpawnPoint>())
            {
                ScenePath = "res://maps/Big.tscn",
            },
            navMesh);
    }

    private static RealmGeometryPacket RoundTrip(RealmGeometryPacket realm, int chunkBytes, out int chunks)
    {
        var split = GeometryChunks.Split(realm, chunkBytes);
        chunks = split.Count;

        var assembler = new GeometryAssembler();
        RealmGeometryPacket? rebuilt = null;
        foreach (var chunk in split)
        {
            var reader = new NetDataReader(chunk.CopyData());
            Assert.Equal((byte)MessageType.RealmGeometry, reader.GetByte()); // framing the client strips
            if (assembler.TryAdd(reader, out var whole))
                rebuilt = whole;
        }
        Assert.NotNull(rebuilt);
        return rebuilt;
    }

    [Fact]
    public void A_realm_survives_being_split_and_rebuilt()
    {
        var realm = Realm(boxes: 400, navMeshBytes: 40_000);
        var rebuilt = RoundTrip(realm, chunkBytes: 8192, out var chunks);

        Assert.True(chunks > 1, "the fixture should actually exercise the split");
        Assert.Equal(realm.SpawnX, rebuilt.SpawnX);
        Assert.Equal(realm.SpawnZ, rebuilt.SpawnZ);
        Assert.Equal(realm.ScenePath, rebuilt.ScenePath);
        Assert.Equal(realm.SoupVertices, rebuilt.SoupVertices);
        Assert.Equal(realm.SoupTriangles, rebuilt.SoupTriangles);
        Assert.Equal(realm.NavMesh, rebuilt.NavMesh);
    }

    [Fact]
    public void A_realm_smaller_than_one_chunk_still_goes_through_the_same_path()
    {
        // The single-chunk case is the common one, so it must not be a special
        // case that only gets exercised the day a realm finally outgrows it.
        var realm = Realm(boxes: 2, navMeshBytes: 16);
        var rebuilt = RoundTrip(realm, GeometryChunks.ChunkBytes, out var chunks);

        Assert.Equal(1, chunks);
        Assert.Equal(realm.SoupVertices, rebuilt.SoupVertices);
        Assert.Equal(realm.NavMesh, rebuilt.NavMesh);
    }

    [Fact]
    public void The_default_chunk_is_small_enough_to_fragment_under_any_plausible_mtu()
    {
        // LiteNetLib refuses a message needing more than ushort.MaxValue
        // fragments. Even at a 576-byte path MTU a default chunk must stay well
        // under that, or a realm becomes undeliverable to somebody behind a
        // tunnel — a failure that would only ever appear in the field.
        const int WorstCasePayload = 576 - 32;
        var fragments = GeometryChunks.ChunkBytes / WorstCasePayload;

        Assert.True(fragments < ushort.MaxValue / 4,
            $"a default chunk is {fragments} fragments at a 576-byte MTU; too close to the limit");
    }

    [Fact]
    public void A_duplicated_chunk_is_a_broken_contract_and_says_so()
    {
        // Realms ride ReliableOrdered. A repeat means the transport broke its
        // promise, and silently tolerating it risks handing the simulation a
        // realm stitched from two different ones.
        var split = GeometryChunks.Split(Realm(boxes: 200, navMeshBytes: 20_000), chunkBytes: 4096);
        Assert.True(split.Count > 2, $"only {split.Count} chunks — the fixture must exercise the split");

        var assembler = new GeometryAssembler();
        Read(assembler, split[0]);
        Assert.Throws<ArgumentException>(() => Read(assembler, split[0]));
    }

    [Fact]
    public void A_malformed_header_is_rejected()
    {
        var assembler = new GeometryAssembler();
        var bad = new NetDataWriter();
        bad.Put((ushort)3);  // index
        bad.Put((ushort)2);  // ...of two
        Assert.Throws<ArgumentException>(() =>
            assembler.TryAdd(new NetDataReader(bad.CopyData()), out _));
    }

    [Fact]
    public void Reset_abandons_a_half_delivered_realm()
    {
        // A reconnect can land on a restarted server serving something else;
        // the leftover chunks must not be spliced into it.
        var first = GeometryChunks.Split(Realm(boxes: 200, navMeshBytes: 20_000), chunkBytes: 4096);
        var assembler = new GeometryAssembler();
        Read(assembler, first[0]);
        assembler.Reset();

        // The same chunk index may now arrive again without complaint.
        Read(assembler, first[0]);
    }

    private static void Read(GeometryAssembler assembler, NetDataWriter chunk)
    {
        var reader = new NetDataReader(chunk.CopyData());
        reader.GetByte(); // the message-type byte the dispatcher strips
        assembler.TryAdd(reader, out _);
    }
}

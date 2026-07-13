using System;
using System.Linq;
using LiteNetLib.Utils;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class SnapshotChunksTests
{
    private const int MaxPacket = 100; // small on purpose, so tests exercise real splits

    /// <summary>A snapshot with enough enemies to need several chunks at <see cref="MaxPacket"/>.</summary>
    private static WorldSnapshotPacket BigSnapshot(int tick, int enemies = 40) => new()
    {
        ServerTick = tick,
        Players = [new PlayerSnapshot { Id = 1, X = 10, Z = 20, Health = 80, LastProcessedInput = 7 }],
        Enemies = Enumerable.Range(0, enemies)
            .Select(i => new EnemySnapshot { Id = i, X = i, Z = -i, Health = 50, Type = 1 })
            .ToArray(),
        GroundItems = [new GroundItemSnapshot { Id = 3, X = 5, Z = 6, Kind = 1, Rarity = 2, Type = 3 }],
        Projectiles = [new ProjectileSnapshot { Id = 9, X = 1, Y = 2, Z = 3 }],
    };

    /// <summary>Presents a framed chunk the way ClientConnection does: type byte consumed.</summary>
    private static NetDataReader AsReceived(NetDataWriter chunk)
    {
        var reader = new NetDataReader(chunk.CopyData());
        Assert.Equal((byte)MessageType.WorldSnapshot, reader.GetByte());
        return reader;
    }

    private static void AssertEquivalent(WorldSnapshotPacket expected, WorldSnapshotPacket actual)
    {
        Assert.Equal(expected.ServerTick, actual.ServerTick);
        Assert.Equal(expected.Players.Length, actual.Players.Length);
        Assert.Equal(expected.Enemies.Length, actual.Enemies.Length);
        Assert.Equal(expected.GroundItems.Length, actual.GroundItems.Length);
        Assert.Equal(expected.Projectiles.Length, actual.Projectiles.Length);
        Assert.Equal(expected.Players[0].LastProcessedInput, actual.Players[0].LastProcessedInput);
        Assert.Equal(expected.Enemies.Select(e => (e.Id, e.X, e.Health)), actual.Enemies.Select(e => (e.Id, e.X, e.Health)));
        Assert.Equal(expected.GroundItems[0].Kind, actual.GroundItems[0].Kind);
        Assert.Equal(expected.Projectiles[0].Id, actual.Projectiles[0].Id);
    }

    [Fact]
    public void Small_snapshot_is_a_single_chunk_and_round_trips()
    {
        var snap = new WorldSnapshotPacket { ServerTick = 5 };
        var chunks = SnapshotChunks.Split(snap, maxPacketSize: 1020);
        Assert.Single(chunks);

        var assembler = new SnapshotAssembler();
        Assert.True(assembler.TryAdd(AsReceived(chunks[0]), out var back));
        Assert.Equal(5, back.ServerTick);
    }

    [Fact]
    public void Oversized_snapshot_splits_into_bounded_chunks_that_reassemble()
    {
        var snap = BigSnapshot(tick: 42);
        var chunks = SnapshotChunks.Split(snap, MaxPacket);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= MaxPacket, $"chunk of {c.Length} bytes exceeds {MaxPacket}"));

        var assembler = new SnapshotAssembler();
        WorldSnapshotPacket? assembled = null;
        foreach (var chunk in chunks)
            if (assembler.TryAdd(AsReceived(chunk), out var back))
                assembled = back;

        Assert.NotNull(assembled);
        AssertEquivalent(snap, assembled);
    }

    [Fact]
    public void Chunks_reassemble_regardless_of_arrival_order()
    {
        var snap = BigSnapshot(tick: 7);
        var chunks = SnapshotChunks.Split(snap, MaxPacket);
        var shuffled = chunks.OrderBy(_ => Guid.NewGuid()).ToList(); // any order must work

        var assembler = new SnapshotAssembler();
        WorldSnapshotPacket? assembled = null;
        var completions = 0;
        foreach (var chunk in shuffled)
            if (assembler.TryAdd(AsReceived(chunk), out var back))
            {
                assembled = back;
                completions++;
            }

        Assert.Equal(1, completions); // fires exactly once, on the final chunk
        Assert.NotNull(assembled);
        AssertEquivalent(snap, assembled);
    }

    [Fact]
    public void Duplicated_chunks_are_ignored_without_double_delivery()
    {
        var chunks = SnapshotChunks.Split(BigSnapshot(tick: 3), MaxPacket);

        var assembler = new SnapshotAssembler();
        Assert.False(assembler.TryAdd(AsReceived(chunks[0]), out _));
        Assert.False(assembler.TryAdd(AsReceived(chunks[0]), out _)); // dup mid-assembly

        var completions = chunks.Skip(1).Count(c => assembler.TryAdd(AsReceived(c), out _));
        Assert.Equal(1, completions);

        // A dup arriving after delivery is stale and stays silent.
        Assert.False(assembler.TryAdd(AsReceived(chunks[^1]), out _));
    }

    [Fact]
    public void Newer_tick_abandons_a_partial_older_one_and_older_never_completes()
    {
        var oldChunks = SnapshotChunks.Split(BigSnapshot(tick: 10), MaxPacket);
        var newChunks = SnapshotChunks.Split(BigSnapshot(tick: 11), MaxPacket);

        var assembler = new SnapshotAssembler();
        // Tick 10 arrives missing one chunk (lost datagram)...
        foreach (var chunk in oldChunks.Take(oldChunks.Count - 1))
            Assert.False(assembler.TryAdd(AsReceived(chunk), out _));

        // ...then tick 11 lands whole and is delivered.
        WorldSnapshotPacket? assembled = null;
        foreach (var chunk in newChunks)
            if (assembler.TryAdd(AsReceived(chunk), out var back))
                assembled = back;
        Assert.NotNull(assembled);
        Assert.Equal(11, assembled.ServerTick);

        // The straggler from tick 10 must never resurrect the stale snapshot.
        Assert.False(assembler.TryAdd(AsReceived(oldChunks[^1]), out _));
    }

    [Fact]
    public void Reset_allows_a_restarted_server_with_lower_ticks()
    {
        var assembler = new SnapshotAssembler();
        Assert.True(assembler.TryAdd(AsReceived(SnapshotChunks.Split(new WorldSnapshotPacket { ServerTick = 500 }, 1020)[0]), out _));

        // Same assembler, server restarted: tick restarts low. Without a reset the
        // stale guard would swallow it forever.
        Assert.False(assembler.TryAdd(AsReceived(SnapshotChunks.Split(new WorldSnapshotPacket { ServerTick = 2 }, 1020)[0]), out _));
        assembler.Reset();
        Assert.True(assembler.TryAdd(AsReceived(SnapshotChunks.Split(new WorldSnapshotPacket { ServerTick = 2 }, 1020)[0]), out var back));
        Assert.Equal(2, back.ServerTick);
    }

    [Fact]
    public void Malformed_chunk_headers_throw_like_any_corrupt_packet()
    {
        var assembler = new SnapshotAssembler();

        var zeroCount = new NetDataWriter();
        zeroCount.Put(1);           // tick
        zeroCount.Put((ushort)0);   // index
        zeroCount.Put((ushort)0);   // count — invalid
        Assert.Throws<ArgumentException>(() => assembler.TryAdd(new NetDataReader(zeroCount.CopyData()), out _));

        var indexPastCount = new NetDataWriter();
        indexPastCount.Put(1);
        indexPastCount.Put((ushort)2); // index
        indexPastCount.Put((ushort)2); // count
        Assert.Throws<ArgumentException>(() => assembler.TryAdd(new NetDataReader(indexPastCount.CopyData()), out _));
    }

    [Fact]
    public void Split_rejects_a_budget_smaller_than_its_own_header()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SnapshotChunks.Split(new WorldSnapshotPacket(), SnapshotChunks.HeaderBytes));
    }
}

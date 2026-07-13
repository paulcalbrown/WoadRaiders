using LiteNetLib.Utils;

namespace WoadRaiders.Shared;

/// <summary>
/// Splits a <see cref="WorldSnapshotPacket"/> across one or more UDP-sized packets.
/// Snapshots ride unreliable delivery, which LiteNetLib caps at roughly one MTU and
/// never fragments — but the world's size is unbounded (enemies respawn, loot piles
/// up), so the snapshot must not be. Every snapshot is framed the same way, chunked
/// or not, so the multi-chunk path is exercised constantly rather than only on the
/// day the world finally outgrows a packet:
/// <code>[MessageType.WorldSnapshot][tick:int][index:ushort][count:ushort][payload]</code>
/// The tick is duplicated outside the payload so <see cref="SnapshotAssembler"/> can
/// group chunks without deserializing partial data. A ushort chunk count makes
/// overflow unreachable: entity arrays are ushort-counted, so a maximal snapshot
/// (~6 MB) still splits into far fewer than 65535 chunks.
/// </summary>
public static class SnapshotChunks
{
    /// <summary>Framing overhead per chunk: type byte + tick + index + count.</summary>
    public const int HeaderBytes = 1 + 4 + 2 + 2;

    /// <summary>
    /// Frame <paramref name="snapshot"/> as chunks, each at most
    /// <paramref name="maxPacketSize"/> bytes (use the smallest connected peer's
    /// <c>GetMaxSinglePacketSize</c> so one send plan fits every peer).
    /// </summary>
    public static List<NetDataWriter> Split(WorldSnapshotPacket snapshot, int maxPacketSize)
    {
        if (maxPacketSize <= HeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPacketSize),
                $"Must exceed the {HeaderBytes}-byte chunk header.");

        var payload = new NetDataWriter();
        snapshot.Serialize(payload);

        var budget = maxPacketSize - HeaderBytes;
        var count = Math.Max(1, (payload.Length + budget - 1) / budget);

        var chunks = new List<NetDataWriter>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = index * budget;
            var chunk = new NetDataWriter();
            chunk.Put((byte)MessageType.WorldSnapshot);
            chunk.Put(snapshot.ServerTick);
            chunk.Put((ushort)index);
            chunk.Put((ushort)count);
            chunk.Put(payload.Data, offset, Math.Min(budget, payload.Length - offset));
            chunks.Add(chunk);
        }
        return chunks;
    }
}

/// <summary>
/// The client-side mirror of <see cref="SnapshotChunks.Split"/>: collects chunks and
/// yields each snapshot once its last chunk lands. Chunks ride unreliable delivery,
/// so any of them can be lost, reordered, or duplicated; the tick guard here restores
/// the one property the old Sequenced channel provided — a stale snapshot is never
/// delivered after a newer one. Only one tick is assembled at a time: a chunk from a
/// newer tick abandons the partial older one (its missing chunks were likely lost,
/// and fresher state trumps complete-but-stale state anyway).
/// </summary>
public sealed class SnapshotAssembler
{
    private int _tick = int.MinValue;      // the tick currently being assembled
    private int _delivered = int.MinValue; // newest tick handed to the caller
    private byte[]?[] _parts = [];
    private int _received;

    /// <summary>
    /// Consume one chunk (positioned after the type byte). Returns true with the
    /// rebuilt snapshot when this chunk completes it. Throws on a malformed header —
    /// callers treat that like any other corrupt packet.
    /// </summary>
    public bool TryAdd(NetDataReader reader, out WorldSnapshotPacket snapshot)
    {
        snapshot = null!;
        var tick = reader.GetInt();
        int index = reader.GetUShort();
        int count = reader.GetUShort();
        if (count == 0 || index >= count)
            throw new ArgumentException($"Malformed snapshot chunk ({index}/{count}).");

        if (tick <= _delivered || tick < _tick)
            return false; // stale — a newer snapshot was delivered or is assembling

        if (tick > _tick)
        {
            _tick = tick;
            _parts = new byte[count][];
            _received = 0;
        }
        else if (count != _parts.Length)
        {
            throw new ArgumentException($"Snapshot chunk count changed mid-tick ({count} vs {_parts.Length}).");
        }

        if (_parts[index] is not null)
            return false; // duplicated datagram
        _parts[index] = reader.GetRemainingBytes();
        if (++_received < _parts.Length)
            return false;

        snapshot = Assemble();
        _delivered = tick;
        _parts = [];
        return true;
    }

    /// <summary>Forget everything — call on (re)connect, where ticks may restart.</summary>
    public void Reset()
    {
        _tick = int.MinValue;
        _delivered = int.MinValue;
        _parts = [];
        _received = 0;
    }

    private WorldSnapshotPacket Assemble()
    {
        var total = 0;
        foreach (var part in _parts)
            total += part!.Length;

        var payload = new byte[total];
        var offset = 0;
        foreach (var part in _parts)
        {
            Buffer.BlockCopy(part!, 0, payload, offset, part!.Length);
            offset += part.Length;
        }

        var snapshot = new WorldSnapshotPacket();
        snapshot.Deserialize(new NetDataReader(payload));
        return snapshot;
    }
}

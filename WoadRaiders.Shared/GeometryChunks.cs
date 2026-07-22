using LiteNetLib.Utils;

namespace WoadRaiders.Shared;

/// <summary>
/// Splits a <see cref="RealmGeometryPacket"/> across several reliable messages.
///
/// A realm is sent whole only on the fallback path — a client that cannot prove
/// it already ships the realm (see <see cref="RealmSnapshot.Digest"/>) — but
/// when it IS sent it must arrive however large the realm has grown, and a
/// single send cannot promise that. LiteNetLib fragments one reliable message
/// into at most <c>ushort.MaxValue</c> parts, so the ceiling is 65535 × the
/// NEGOTIATED MTU: measured on loopback (tools/MeasureReliableLimit.cs) 62 MB
/// arrived intact and 64 MB threw <c>TooBigPacketException</c>. That ceiling
/// moves with the path — a peer behind a tunnel or a low-MTU link gets a lower
/// one — so it is not a number a realm can be designed against. Chunking
/// removes the dependency: each chunk is small enough to fragment safely under
/// any MTU worth supporting, and a realm may be any size.
///
/// The framing mirrors <see cref="SnapshotChunks"/>:
/// <code>[MessageType.RealmGeometry][index:ushort][count:ushort][payload]</code>
/// with no tick, because a realm is static — there is only ever one of them in
/// flight, and it rides ReliableOrdered, so nothing can be lost, reordered or
/// duplicated. That is why this assembler is a fraction of the snapshot one:
/// the hard parts of that problem do not exist here.
/// </summary>
public static class GeometryChunks
{
    /// <summary>Framing overhead per chunk: type byte + index + count.</summary>
    public const int HeaderBytes = 1 + 2 + 2;

    /// <summary>
    /// Bytes of realm per chunk. Four megabytes is chosen against the WORST
    /// plausible MTU rather than the usual one: even at a 576-byte path MTU a
    /// chunk is ~7,500 fragments, an order under the 65535 limit, so no link
    /// this game can reach turns a large realm into a failed join. Bigger
    /// chunks would buy nothing — the cost is per byte, not per chunk, and 62 MB
    /// is only 16 of these.
    /// </summary>
    public const int ChunkBytes = 4 * 1024 * 1024;

    /// <summary>Frame a realm as reliable chunks, in order.</summary>
    public static List<NetDataWriter> Split(RealmGeometryPacket geometry, int chunkBytes = ChunkBytes)
    {
        if (chunkBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkBytes));

        var payload = new NetDataWriter();
        geometry.Serialize(payload);

        var count = Math.Max(1, (payload.Length + chunkBytes - 1) / chunkBytes);
        if (count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(geometry),
                $"a realm of {payload.Length / 1048576} MB needs {count} chunks; raise {nameof(ChunkBytes)}");

        var chunks = new List<NetDataWriter>(count);
        for (var index = 0; index < count; index++)
        {
            var offset = index * chunkBytes;
            var chunk = new NetDataWriter();
            chunk.Put((byte)MessageType.RealmGeometry);
            chunk.Put((ushort)index);
            chunk.Put((ushort)count);
            chunk.Put(payload.Data, offset, Math.Min(chunkBytes, payload.Length - offset));
            chunks.Add(chunk);
        }
        return chunks;
    }
}

/// <summary>
/// The client-side mirror of <see cref="GeometryChunks.Split"/>: collects chunks
/// and yields the realm once the last one lands.
///
/// Deliberately strict where <see cref="SnapshotAssembler"/> is forgiving. A
/// snapshot rides unreliable delivery and must tolerate loss, reordering and
/// duplication; a realm rides ReliableOrdered, so any of those would mean the
/// transport had broken its own contract. Rather than paper over that with a
/// tolerant reassembly — which would hand the simulation a realm stitched from
/// pieces of two different ones — this throws, and the caller treats it like any
/// other corrupt packet.
/// </summary>
public sealed class GeometryAssembler
{
    private byte[]?[] _parts = [];
    private int _received;

    /// <summary>
    /// Consume one chunk (positioned after the type byte). Returns true with the
    /// rebuilt realm when this chunk completes it.
    /// </summary>
    public bool TryAdd(NetDataReader reader, out RealmGeometryPacket geometry)
    {
        geometry = null!;
        int index = reader.GetUShort();
        int count = reader.GetUShort();
        if (count == 0 || index >= count)
            throw new ArgumentException($"Malformed geometry chunk ({index}/{count}).");

        if (_parts.Length != count)
        {
            // The first chunk of a realm — or a second realm arriving, which a
            // reconnect onto a restarted server can legitimately do.
            _parts = new byte[count][];
            _received = 0;
        }
        if (_parts[index] is not null)
            throw new ArgumentException($"Geometry chunk {index} arrived twice on a reliable channel.");

        _parts[index] = reader.GetRemainingBytes();
        if (++_received < count)
            return false;

        geometry = Assemble();
        Reset();
        return true;
    }

    /// <summary>Forget any partial realm — call on (re)connect.</summary>
    public void Reset()
    {
        _parts = [];
        _received = 0;
    }

    private RealmGeometryPacket Assemble()
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

        var geometry = new RealmGeometryPacket();
        geometry.Deserialize(new NetDataReader(payload));
        return geometry;
    }
}

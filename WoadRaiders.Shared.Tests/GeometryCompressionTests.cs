using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

/// <summary>
/// What the fallback geometry send COSTS, which is a different question from
/// what it weighs — and the one this project got wrong.
///
/// The realm's size had a budget and a test. The time to compress it had
/// neither, so when the Crypt grew from 2,280 triangles to 325,594 the brotli
/// pass grew with it, from imperceptible to fifteen seconds — and it ran on the
/// server's game loop, the first time any raider turned out to need it. Nothing
/// failed. The server logged `Sim stalled 15029 ms`, dropped the lost time, and
/// carried on; every instance on it froze, and no one else could connect.
///
/// These are timing tests, which are usually a bad idea. They are defensible
/// here only because the margin is enormous: the setting that caused the bug is
/// 200x slower than the one that fixes it, so the bound below separates them by
/// two orders of magnitude and cannot flake without something being genuinely,
/// catastrophically wrong.
/// </summary>
public class GeometryCompressionTests
{
    /// <summary>
    /// Generous by design. Optimal packs the shipping Crypt in ~60 ms and
    /// SmallestSize took 12,172 ms; anything in between is a machine having a
    /// bad day, and anything above it is the old setting come back.
    /// </summary>
    private const int PackBudgetMs = 3000;

    [Fact]
    public void The_shipping_realm_compresses_in_well_under_a_frame_budget()
    {
        if (ShippingCrypt() is not { } realm)
            return;

        var packet = RealmSnapshot.From(realm, Array.Empty<byte>());
        var clock = Stopwatch.StartNew();
        packet.Precompress();
        var elapsed = clock.ElapsedMilliseconds;

        Assert.True(elapsed < PackBudgetMs,
            $"packing the realm took {elapsed} ms. Brotli SmallestSize costs 200x Optimal " +
            "for a saving BUDGET-003 does not need, and this runs where players are waiting.");
    }

    [Fact]
    public void Precompressing_takes_the_cost_off_the_send()
    {
        if (ShippingCrypt() is not { } realm)
            return;

        var packet = RealmSnapshot.From(realm, Array.Empty<byte>()).Precompress();

        // The whole point of paying it at load: every send afterwards is a memcpy.
        // A raider joining must never be what triggers the compression.
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 8; i++)
        {
            var w = new NetDataWriter();
            packet.Serialize(w);
            Assert.True(w.Length > 0);
        }
        Assert.True(clock.ElapsedMilliseconds < PackBudgetMs,
            $"eight sends of a warmed packet took {clock.ElapsedMilliseconds} ms — it is recompressing per peer");
    }

    [Fact]
    public void A_packet_survives_the_round_trip_whatever_level_packed_it()
    {
        // Brotli streams are self-describing, so the compression level is not
        // part of the wire contract: a client built when the server used
        // SmallestSize reads an Optimal payload without knowing it changed.
        // That is why v22 needed no format change — only a release.
        if (ShippingCrypt() is not { } realm)
            return;

        var sent = RealmSnapshot.From(realm, new byte[] { 1, 2, 3, 4 }).Precompress();
        var w = new NetDataWriter();
        sent.Serialize(w);

        var received = new RealmGeometryPacket();
        received.Deserialize(new NetDataReader(w.CopyData()));

        Assert.Equal(sent.SoupVertices.Length, received.SoupVertices.Length);
        Assert.Equal(sent.SoupTriangles.Length, received.SoupTriangles.Length);
        Assert.Equal(sent.NavMesh, received.NavMesh);
        Assert.Equal(RealmSnapshot.Digest(sent), RealmSnapshot.Digest(received));
    }

    /// <summary>The realm as shipped, or null in a checkout that has not generated
    /// one — these check a built artefact, not a broken checkout.</summary>
    private static RealmDefinition? ShippingCrypt()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "WoadRaiders.Client", "maps", "Crypt.json");
            if (File.Exists(candidate))
                return RealmDefinitionFile.Load(candidate);
        }
        return null;
    }
}

using System;
using System.Numerics;
using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

// The end-of-run wire pieces: the portal riding the world snapshot and the
// RunComplete summary packet.
public class PortalProtocolTests
{
    private static T RoundTrip<T>(T packet) where T : INetSerializable, new()
    {
        var writer = new NetDataWriter();
        packet.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new T();
        back.Deserialize(reader);
        return back;
    }

    [Fact]
    public void Snapshot_round_trips_an_open_portal()
    {
        var back = RoundTrip(new WorldSnapshotPacket
        {
            ServerTick = 42,
            PortalOpen = true,
            PortalX = 300f,
            PortalY = 0f,
            PortalZ = -120f,
        });

        Assert.True(back.PortalOpen);
        Assert.Equal(300f, back.PortalX);
        Assert.Equal(0f, back.PortalY);
        Assert.Equal(-120f, back.PortalZ);
    }

    [Fact]
    public void Snapshot_round_trips_a_closed_portal()
    {
        var back = RoundTrip(new WorldSnapshotPacket { ServerTick = 7 });
        Assert.False(back.PortalOpen);
    }

    [Fact]
    public void Snapshot_projection_carries_the_world_portal()
    {
        var world = new GameWorld(new Random(1));
        Assert.False(WorldSnapshot.From(world).PortalOpen); // closed until the boss falls

        world.OpenPortal(new Vector3(50, 0, 60));
        var snap = WorldSnapshot.From(world);

        Assert.True(snap.PortalOpen);
        Assert.Equal(50f, snap.PortalX);
        Assert.Equal(60f, snap.PortalZ);
    }

    [Fact]
    public void Run_complete_round_trips_the_summary()
    {
        var back = RoundTrip(new RunCompletePacket
        {
            Dungeon = (byte)DungeonId.Cairn,
            RaidName = "Boudica's revenge",
            DurationSeconds = 272,
            Gold = 154,
            ItemsLooted = 3,
            FoesSlain = 27,
        });

        Assert.Equal((byte)DungeonId.Cairn, back.Dungeon);
        Assert.Equal("Boudica's revenge", back.RaidName);
        Assert.Equal(272, back.DurationSeconds);
        Assert.Equal(154, back.Gold);
        Assert.Equal(3, back.ItemsLooted);
        Assert.Equal(27, back.FoesSlain);
    }
}

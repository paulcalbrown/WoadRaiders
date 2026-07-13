using LiteNetLib.Utils;
using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

// The dungeon-instance handshake: forge-or-join requests, the pinned instance id
// in the welcome, the browsable instance list, and the deny path.
public class InstanceProtocolTests
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
    public void Join_request_round_trips_the_join_mode_and_target_instance()
    {
        var back = RoundTrip(new JoinRequest
        {
            Name = "Bran",
            Class = (byte)CharacterClass.Rogue,
            Mode = (byte)JoinMode.Join,
            InstanceId = 42,
        });

        Assert.Equal((byte)JoinMode.Join, back.Mode);
        Assert.Equal(42, back.InstanceId);
    }

    [Fact]
    public void Welcome_round_trips_the_instance_id()
    {
        var back = RoundTrip(new WelcomePacket { PlayerId = 3, ServerTick = 1234, InstanceId = 7 });

        Assert.Equal(3, back.PlayerId);
        Assert.Equal(1234, back.ServerTick);
        Assert.Equal(7, back.InstanceId);
    }

    [Fact]
    public void Instance_list_round_trips_every_entry()
    {
        var back = RoundTrip(new InstanceListPacket
        {
            Instances =
            [
                new InstanceEntry { Id = 1, Dungeon = (byte)DungeonId.Barrow, Name = "First light", Players = 3, MaxPlayers = 8 },
                new InstanceEntry { Id = 9, Dungeon = (byte)DungeonId.Cairn, Name = "Stones", Players = 8, MaxPlayers = 8 },
            ],
        });

        Assert.Collection(back.Instances,
            e =>
            {
                Assert.Equal(1, e.Id);
                Assert.Equal((byte)DungeonId.Barrow, e.Dungeon);
                Assert.Equal("First light", e.Name);
                Assert.Equal(3, e.Players);
                Assert.Equal(8, e.MaxPlayers);
            },
            e =>
            {
                Assert.Equal(9, e.Id);
                Assert.Equal((byte)DungeonId.Cairn, e.Dungeon);
                Assert.Equal("Stones", e.Name);
                Assert.Equal(8, e.Players);
            });
    }

    [Fact]
    public void Instance_list_round_trips_empty()
    {
        var back = RoundTrip(new InstanceListPacket());
        Assert.Empty(back.Instances);
    }

    [Fact]
    public void Join_denied_round_trips_the_reason()
    {
        var back = RoundTrip(new JoinDeniedPacket { Reason = (byte)JoinDenyReason.InstanceFull });
        Assert.Equal((byte)JoinDenyReason.InstanceFull, back.Reason);
    }
}

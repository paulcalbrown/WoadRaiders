using LiteNetLib.Utils;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class InputPacketTests
{
    [Fact]
    public void Input_survives_a_serialize_round_trip_including_aim()
    {
        var packet = new InputPacket
        {
            MoveX = 0.5f,
            MoveZ = -0.5f,
            AimX = 0.6f,
            AimZ = -0.8f,
            Sequence = 4242u,
            Attack = true,
        };

        var writer = new NetDataWriter();
        packet.Serialize(writer);
        var reader = new NetDataReader();
        reader.SetSource(writer);
        var back = new InputPacket();
        back.Deserialize(reader);

        Assert.Equal(packet.MoveX, back.MoveX);
        Assert.Equal(packet.MoveZ, back.MoveZ);
        Assert.Equal(packet.AimX, back.AimX);
        Assert.Equal(packet.AimZ, back.AimZ);
        Assert.Equal(packet.Sequence, back.Sequence);
        Assert.Equal(packet.Attack, back.Attack);
    }
}

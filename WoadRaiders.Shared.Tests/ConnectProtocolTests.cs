using LiteNetLib.Utils;
using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

// The connect-time refusal: the ConnectDenied payload that rides the
// connection-layer reject. Its format is FROZEN (append-only) — it is the one
// packet that must be readable by a build the server otherwise won't talk to.
public class ConnectProtocolTests
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
    public void Connect_denied_round_trips_the_server_key_and_message()
    {
        var back = RoundTrip(new ConnectDeniedPacket
        {
            ServerKey = NetConfig.ConnectionKey,
            Message = $"This server runs {NetConfig.ConnectionKey} — get the latest at {NetConfig.DownloadUrl}",
        });

        Assert.Equal(NetConfig.ConnectionKey, back.ServerKey);
        Assert.Contains(NetConfig.DownloadUrl, back.Message);
    }

    // The frozen wire layout, spelled out byte-for-byte: two length-prefixed
    // strings, server key then message. If this test breaks, an old client can
    // no longer read a new server's refusal — that is a bug in the change, not
    // in the test. Append new fields after Message instead.
    [Fact]
    public void Connect_denied_wire_layout_is_two_strings_key_then_message()
    {
        var writer = new NetDataWriter();
        new ConnectDeniedPacket { ServerKey = "WoadRaiders.v99", Message = "why" }.Serialize(writer);

        var reader = new NetDataReader();
        reader.SetSource(writer);
        Assert.Equal("WoadRaiders.v99", reader.GetString());
        Assert.Equal("why", reader.GetString());
        Assert.Equal(0, reader.AvailableBytes);
    }
}

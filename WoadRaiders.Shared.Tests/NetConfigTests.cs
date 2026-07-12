using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

public class NetConfigTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_falls_back_to_the_default_endpoint(string? text)
    {
        Assert.Equal((NetConfig.DefaultHost, NetConfig.DefaultPort), NetConfig.ParseEndpoint(text));
    }

    [Fact]
    public void A_bare_host_uses_the_default_port()
    {
        Assert.Equal(("play.example.com", NetConfig.DefaultPort), NetConfig.ParseEndpoint("play.example.com"));
    }

    [Fact]
    public void Host_and_port_both_parse()
    {
        Assert.Equal(("10.0.0.5", 9051), NetConfig.ParseEndpoint(" 10.0.0.5:9051 "));
    }

    [Fact]
    public void A_bare_port_uses_the_default_host()
    {
        Assert.Equal((NetConfig.DefaultHost, 9051), NetConfig.ParseEndpoint(":9051"));
    }

    [Theory]
    [InlineData("myhost:banana")]
    [InlineData("myhost:")]
    [InlineData("myhost:0")]
    [InlineData("myhost:70000")]
    [InlineData("myhost:-1")]
    public void A_malformed_port_falls_back_to_the_default_port(string text)
    {
        Assert.Equal(("myhost", NetConfig.DefaultPort), NetConfig.ParseEndpoint(text));
    }
}

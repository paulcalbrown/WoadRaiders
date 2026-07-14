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

    [Theory]
    [InlineData("")]
    [InlineData(":9051")]
    public void A_missing_host_takes_the_caller_supplied_default(string text)
    {
        var (host, _) = NetConfig.ParseEndpoint(text, NetConfig.PublicHost);
        Assert.Equal(NetConfig.PublicHost, host);
    }

    [Fact]
    public void An_explicit_host_beats_the_caller_supplied_default()
    {
        Assert.Equal(("myhost", 9051), NetConfig.ParseEndpoint("myhost:9051", NetConfig.PublicHost));
    }

    [Fact]
    public void The_current_connection_key_parses_to_its_version()
    {
        Assert.True(NetConfig.TryParseVersion(NetConfig.ConnectionKey, out var version));
        Assert.True(version >= 13); // the key this feature shipped with; only ever bumped
    }

    [Theory]
    [InlineData("WoadRaiders.v13", 13)]
    [InlineData("WoadRaiders.v999", 999)]
    public void A_well_formed_key_parses(string key, int expected)
    {
        Assert.True(NetConfig.TryParseVersion(key, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("WoadRaiders.v")]
    [InlineData("WoadRaiders.vbanana")]
    [InlineData("WoadRaiders.v0")]
    [InlineData("WoadRaiders.v-3")]
    [InlineData("SomeOtherGame.v13")]
    [InlineData("woadraiders.v13")]
    public void A_foreign_or_mangled_key_does_not_parse(string? key)
    {
        Assert.False(NetConfig.TryParseVersion(key, out _));
    }
}

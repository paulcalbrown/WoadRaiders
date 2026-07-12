using WoadRaiders.Server;

namespace WoadRaiders.Server.Tests;

// SanitizeName is the server's guard on untrusted client-supplied names. It is
// internal to the server and exposed here via InternalsVisibleTo.
public class SanitizeNameTests
{
    [Fact]
    public void Passes_a_normal_name_unchanged() =>
        Assert.Equal("Bran", GameServer.SanitizeName("Bran", 1));

    [Fact]
    public void Trims_surrounding_whitespace() =>
        Assert.Equal("Bran", GameServer.SanitizeName("  Bran  ", 1));

    [Fact]
    public void Strips_control_characters() =>
        Assert.Equal("abc", GameServer.SanitizeName("a\0b\tc\n", 1)); // NUL, tab, newline removed

    [Fact]
    public void Caps_the_length()
    {
        var flood = new string('A', 10_000);
        Assert.Equal(24, GameServer.SanitizeName(flood, 1).Length);
    }

    [Theory]
    [InlineData("")]         // empty
    [InlineData("   ")]      // only whitespace
    [InlineData("\t\r\n\0")] // only control characters
    public void Empty_after_cleaning_falls_back_to_a_default(string hostile) =>
        Assert.Equal("Raider-7", GameServer.SanitizeName(hostile, 7));
}

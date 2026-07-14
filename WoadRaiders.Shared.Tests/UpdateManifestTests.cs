using WoadRaiders.Shared;

namespace WoadRaiders.Shared.Tests;

// The latest.json release manifest: the HTTPS half of the version story.
// It is fetched by every client at startup and written by tools/release.ps1,
// so parsing must be lenient and the newer-than compare must never nag a dev
// build running ahead of the published release.
public class UpdateManifestTests
{
    private const string FullManifest = """
        {
          "key": "WoadRaiders.v14",
          "page": "https://github.com/paulcalbrown/WoadRaiders/releases/tag/v14",
          "published": "2026-07-14T12:00:00Z",
          "downloads": {
            "windows": { "url": "https://example.test/WoadRaiders.exe", "sha256": "aa11" },
            "macos": { "url": "https://example.test/WoadRaiders-macOS.zip", "sha256": "bb22" }
          },
          "server": {
            "windows": { "url": "https://example.test/WoadRaiders-Server-win-x64.zip", "sha256": "cc33" },
            "linux": { "url": "https://example.test/WoadRaiders-Server-linux-x64.zip", "sha256": "dd44" },
            "image": {
              "ref": "ghcr.io/paulcalbrown/woadraiders-server:v14",
              "url": "https://example.test/WoadRaiders-Server-image.tar.gz",
              "sha256": "ee55"
            }
          }
        }
        """;

    [Fact]
    public void A_full_manifest_parses_every_field()
    {
        Assert.True(UpdateManifest.TryParse(FullManifest, out var manifest));

        Assert.Equal("WoadRaiders.v14", manifest.Key);
        Assert.Equal("https://github.com/paulcalbrown/WoadRaiders/releases/tag/v14", manifest.Page);
        Assert.Equal("https://example.test/WoadRaiders.exe", manifest.Windows?.DownloadUrl);
        Assert.Equal("aa11", manifest.Windows?.Sha256);
        Assert.Equal("https://example.test/WoadRaiders-macOS.zip", manifest.MacOS?.DownloadUrl);
        Assert.Equal("bb22", manifest.MacOS?.Sha256);
        Assert.Equal("https://example.test/WoadRaiders-Server-win-x64.zip", manifest.ServerWindows?.DownloadUrl);
        Assert.Equal("cc33", manifest.ServerWindows?.Sha256);
        Assert.Equal("https://example.test/WoadRaiders-Server-linux-x64.zip", manifest.ServerLinux?.DownloadUrl);
        Assert.Equal("dd44", manifest.ServerLinux?.Sha256);
        Assert.Equal("ghcr.io/paulcalbrown/woadraiders-server:v14", manifest.ServerImage);
        Assert.Equal("https://example.test/WoadRaiders-Server-image.tar.gz", manifest.ServerImageArchive?.DownloadUrl);
        Assert.Equal("ee55", manifest.ServerImageArchive?.Sha256);
    }

    [Fact]
    public void A_minimal_manifest_needs_only_the_key()
    {
        Assert.True(UpdateManifest.TryParse("""{ "key": "WoadRaiders.v14" }""", out var manifest));

        Assert.Equal("WoadRaiders.v14", manifest.Key);
        Assert.Equal("", manifest.Page);
        Assert.Null(manifest.Windows);
        Assert.Null(manifest.MacOS);
        Assert.Null(manifest.ServerWindows);
        Assert.Null(manifest.ServerLinux);
        Assert.Equal("", manifest.ServerImage);
        Assert.Null(manifest.ServerImageArchive);
    }

    [Fact]
    public void Unknown_fields_are_ignored_not_fatal()
    {
        // A future release may say more than this build understands.
        Assert.True(UpdateManifest.TryParse(
            """{ "key": "WoadRaiders.v14", "mandatory": true, "channels": ["beta"] }""", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{}")]
    [InlineData("""{ "key": 13 }""")]
    public void Garbage_is_a_false_never_a_throw(string json)
    {
        Assert.False(UpdateManifest.TryParse(json, out _));
    }

    [Theory]
    [InlineData("WoadRaiders.v14", "WoadRaiders.v13", true)]  // player is behind
    [InlineData("WoadRaiders.v13", "WoadRaiders.v13", false)] // up to date
    [InlineData("WoadRaiders.v13", "WoadRaiders.v14", false)] // dev build ahead of the release
    [InlineData("garbage", "WoadRaiders.v13", false)]         // mangled manifest never nags
    [InlineData("WoadRaiders.v14", "garbage", false)]         // mangled local key never nags
    public void Newer_than_compares_parsed_versions_strictly(string released, string current, bool newer)
    {
        UpdateManifest.TryParse($$"""{ "key": "{{released}}" }""", out var manifest);
        manifest.Key = released; // garbage keys fail TryParse; compare still must behave

        Assert.Equal(newer, manifest.IsNewerThan(current));
    }
}

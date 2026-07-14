using System.Text.Json;

namespace WoadRaiders.Shared;

/// <summary>
/// The release manifest (<c>latest.json</c>) published beside every client build
/// on GitHub Releases — the HTTPS half of the version story that
/// <see cref="NetConfig.ConnectionKey"/> gates on the UDP side. The client
/// fetches it at startup (never blocking play) and tells the player when a
/// newer build exists; <c>tools/release.ps1</c> writes it. The SHA-256 digests
/// aren't consumed yet — they're published now so a future self-updater can
/// verify downloads without a format change.
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>The stable manifest address: GitHub's "latest release" asset
    /// redirect, so every release automatically becomes the answer.</summary>
    public const string Url =
        "https://github.com/paulcalbrown/WoadRaiders/releases/latest/download/latest.json";

    /// <summary>One downloadable build inside the manifest.</summary>
    public sealed class Artifact
    {
        public string DownloadUrl = "";
        public string Sha256 = "";
    }

    /// <summary>The released build's <see cref="NetConfig.ConnectionKey"/>.</summary>
    public string Key = "";

    /// <summary>The human-facing releases page to send the player to.</summary>
    public string Page = "";

    public Artifact? Windows;
    public Artifact? MacOS;

    /// <summary>Dedicated-server builds. Not consumed by the client's update
    /// check — published for self-hosters and future hosting tooling.</summary>
    public Artifact? ServerWindows;
    public Artifact? ServerLinux;

    /// <summary>True when this manifest describes a build strictly newer than
    /// <paramref name="currentKey"/>. False whenever either key doesn't parse —
    /// and for a dev build running ahead of the published release.</summary>
    public bool IsNewerThan(string currentKey) =>
        NetConfig.TryParseVersion(Key, out var released)
        && NetConfig.TryParseVersion(currentKey, out var current)
        && released > current;

    /// <summary>Parse manifest JSON. Defensive — the manifest crosses version
    /// gates by design, so unknown fields are ignored and anything malformed
    /// is a false, never a throw. Requires only <c>key</c>.</summary>
    public static bool TryParse(string json, out UpdateManifest manifest)
    {
        manifest = new UpdateManifest();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            manifest.Key = ReadString(root, "key");
            manifest.Page = ReadString(root, "page");
            if (root.TryGetProperty("downloads", out var downloads)
                && downloads.ValueKind == JsonValueKind.Object)
            {
                manifest.Windows = ReadArtifact(downloads, "windows");
                manifest.MacOS = ReadArtifact(downloads, "macos");
            }
            if (root.TryGetProperty("server", out var server)
                && server.ValueKind == JsonValueKind.Object)
            {
                manifest.ServerWindows = ReadArtifact(server, "windows");
                manifest.ServerLinux = ReadArtifact(server, "linux");
            }
            return manifest.Key.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Fetch and parse the manifest, or null — offline, a missing
    /// release (no release published yet is a 404), a slow network, and bad
    /// JSON all just mean "no update news today", never an error.</summary>
    public static async Task<UpdateManifest?> FetchAsync(string url = Url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.TryParseAdd("WoadRaiders");
            var json = await http.GetStringAsync(url).ConfigureAwait(false);
            return TryParse(json, out var manifest) ? manifest : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static Artifact? ReadArtifact(JsonElement downloads, string platform) =>
        downloads.TryGetProperty(platform, out var entry) && entry.ValueKind == JsonValueKind.Object
            ? new Artifact { DownloadUrl = ReadString(entry, "url"), Sha256 = ReadString(entry, "sha256") }
            : null;
}

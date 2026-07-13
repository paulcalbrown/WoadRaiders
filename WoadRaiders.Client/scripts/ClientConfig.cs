using Godot;
using WoadRaiders.Shared;

namespace WoadRaiders.Client;

/// <summary>
/// App-level client configuration: where to connect and who we are. Seeded once
/// from the command line (user args after <c>--</c>) and edited by the title
/// screen before each match. A static on purpose — screen changes replace the
/// whole scene tree, and this must survive them. Matchmaking (PlayFab) will
/// eventually hand out the endpoint; this stays as the dev/manual override.
///
///   --server=host[:port]   target server (default 127.0.0.1)
///   --name=RaiderName      player name sent in the JoinRequest
///   --play                 skip the title screen and connect immediately
///   --screenshot=path.png  save title-screen stills there and exit (dev)
/// </summary>
public static class ClientConfig
{
    public static string Host { get; private set; } = NetConfig.DefaultHost;
    public static int Port { get; private set; } = NetConfig.DefaultPort;
    public static string PlayerName { get; set; } = "Woad Raider";

    /// <summary>Skip the title screen and connect immediately (dev/headless convenience).</summary>
    public static bool AutoPlay { get; private set; }

    /// <summary>Render the title screen, save PNG stills at this path, then quit (dev convenience).</summary>
    public static string? ScreenshotPath { get; private set; }

    private static bool _loaded;

    /// <summary>Parse the user args once. Idempotent — every screen calls it in _Ready,
    /// so a scene run directly from the editor (F6) is configured too.</summary>
    public static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg == "--play")
                AutoPlay = true;
            else if (arg.StartsWith("--server="))
                SetServer(arg["--server=".Length..]);
            else if (arg.StartsWith("--name="))
                PlayerName = arg["--name=".Length..];
            else if (arg.StartsWith("--screenshot="))
                ScreenshotPath = arg["--screenshot=".Length..];
        }
    }

    /// <summary>The endpoint as the title screen displays and edits it.</summary>
    public static string ServerText => Port == NetConfig.DefaultPort ? Host : $"{Host}:{Port}";

    /// <summary>Set the endpoint from user text ("host[:port]"); malformed input falls back to defaults.</summary>
    public static void SetServer(string text) => (Host, Port) = NetConfig.ParseEndpoint(text);
}

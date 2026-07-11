namespace WoadRaiders.Shared;

/// <summary>Connection-level constants shared by client and server.</summary>
public static class NetConfig
{
    public const int DefaultPort = 9050;

    /// <summary>
    /// A trivial handshake key. It stops random UDP traffic from being treated as
    /// a client; it is NOT security. Real auth comes from the accounts/matchmaking
    /// layer (e.g. PlayFab) once that is wired in.
    /// </summary>
    public const string ConnectionKey = "WoadRaiders.v0";

    /// <summary>Co-op party size cap for a single dedicated-server instance.</summary>
    public const int MaxPlayers = 8;

    /// <summary>How many world snapshots the server broadcasts per second.</summary>
    public const int SnapshotsPerSecond = 20;
}

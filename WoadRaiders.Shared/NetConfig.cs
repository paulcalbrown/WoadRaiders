namespace WoadRaiders.Shared;

/// <summary>Connection-level constants shared by client and server.</summary>
public static class NetConfig
{
    public const int DefaultPort = 9050;

    /// <summary>Default server endpoint for a client with no override (the local dev loop).</summary>
    public const string DefaultHost = "127.0.0.1";

    /// <summary>
    /// Parse a user-supplied "host[:port]" endpoint (IPv4 or hostname; IPv6 is not
    /// supported — the last ':' is taken as the port separator). This parses
    /// title-screen and CLI input, so anything missing or malformed falls back to
    /// the defaults instead of failing: best effort, never crash.
    /// </summary>
    public static (string Host, int Port) ParseEndpoint(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return (DefaultHost, DefaultPort);

        var colon = value.LastIndexOf(':');
        if (colon < 0)
            return (value, DefaultPort);

        var host = colon == 0 ? DefaultHost : value[..colon];
        var port = int.TryParse(value[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535
            ? parsed
            : DefaultPort;
        return (host, port);
    }

    /// <summary>
    /// A trivial handshake key. It stops random UDP traffic from being treated as
    /// a client; it is NOT security. Real auth comes from the accounts/matchmaking
    /// layer (e.g. PlayFab) once that is wired in.
    /// Bump the version suffix whenever the wire format changes (it is the only
    /// build-compatibility gate at connect time): v1 = EnemySnapshot.Type byte,
    /// v2 = ProjectileSnapshot array in WorldSnapshotPacket,
    /// v3 = loot kinds (GroundItemSnapshot.Kind, ItemPickedUpPacket.Kind/Amount),
    /// v4 = weapon-kit item types + GroundItemSnapshot.Type byte,
    /// v5 = InputPacket aim (AimX/AimZ) for cursor-aimed attacks,
    /// v6 = PlayerSnapshot attack timers (AttackAnim/AttackCooldown) for reconcile,
    /// v7 = WorldSnapshot chunk framing (tick/index/count header, rides Unreliable),
    /// v8 = character classes (JoinRequest.Class, PlayerSnapshot.Class,
    ///      ProjectileSnapshot.Kind).
    /// </summary>
    public const string ConnectionKey = "WoadRaiders.v8";

    /// <summary>Co-op party size cap for a single dedicated-server instance.</summary>
    public const int MaxPlayers = 8;

    /// <summary>How many world snapshots the server broadcasts per second.</summary>
    public const int SnapshotsPerSecond = 20;
}

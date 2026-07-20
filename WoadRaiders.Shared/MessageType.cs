namespace WoadRaiders.Shared;

/// <summary>
/// The first byte of every packet identifies its type. We frame packets manually
/// (a leading byte + the payload) rather than leaning on reflection helpers, so
/// the protocol is explicit and easy to reason about.
/// </summary>
public enum MessageType : byte
{
    /// <summary>Client → server: request to join, carries the chosen name.</summary>
    JoinRequest = 1,

    /// <summary>Server → client: you are in; here is your assigned player id.</summary>
    Welcome = 2,

    /// <summary>Client → server: one frame of input (sent every client tick).</summary>
    Input = 3,

    /// <summary>
    /// Server → clients: one chunk of the authoritative world snapshot (framed by
    /// <see cref="SnapshotChunks"/>; most snapshots fit in a single chunk).
    /// </summary>
    WorldSnapshot = 4,

    /// <summary>Server → one client: you just collected this item.</summary>
    ItemPickedUp = 5,

    /// <summary>Client → server: equip the item with this id from my inventory.</summary>
    EquipRequest = 6,

    /// <summary>Server → one client: your current equipped item ids per slot.</summary>
    EquipmentUpdate = 7,

    /// <summary>Server → client: the realm's 3D collision geometry (sent once on join).</summary>
    RealmGeometry = 8,

    /// <summary>Client → server: send me the current list of live dungeon instances.</summary>
    InstanceListRequest = 9,

    /// <summary>Server → one client: every live instance (reply to a list request).</summary>
    InstanceList = 10,

    /// <summary>Server → one client: your join was refused (gone/full); pick again.</summary>
    JoinDenied = 11,

    /// <summary>Server → one client: you stepped through the portal — run over, here is your summary.</summary>
    RunComplete = 12,
}

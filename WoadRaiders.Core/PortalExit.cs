namespace WoadRaiders.Core;

/// <summary>
/// A player stepping out of the dungeon through the boss portal, with the haul
/// they carried: recorded by <see cref="GameWorld"/> the tick they exit (the
/// player is removed from the world in the same tick) and drained by the host
/// to send each raider their run summary.
/// </summary>
public readonly record struct PortalExit(
    int PlayerId,
    string PlayerName,
    int Gold,
    int ItemsLooted,
    int DurationTicks); // ticks between this player's join and their exit

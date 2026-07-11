namespace WoadRaiders.Core;

/// <summary>
/// One frame of intent from a client. This is the ONLY thing a client is
/// allowed to tell the server about its player. The server decides what
/// actually happens — never trust a client for position, damage, or loot.
/// </summary>
public struct PlayerInput
{
    /// <summary>Horizontal move axis, expected in the range [-1, 1].</summary>
    public float MoveX;

    /// <summary>Vertical move axis, expected in the range [-1, 1].</summary>
    public float MoveY;

    /// <summary>Whether the attack button is held this frame.</summary>
    public bool Attack;

    /// <summary>
    /// Client-assigned, monotonically increasing input number. Client-side
    /// prediction and server reconciliation key on this.
    /// </summary>
    public uint Sequence;
}

namespace WoadRaiders.Core;

/// <summary>
/// One frame of intent from a client. This is the ONLY thing a client is
/// allowed to tell the server about its player. The server decides what
/// actually happens — never trust a client for position, damage, or loot.
///
/// Movement intent is 2D (the ground plane): even in a fully 3D world you steer
/// on XZ, and the simulation decides your height from the geometry.
/// </summary>
public struct PlayerInput
{
    /// <summary>Move axis along world X, expected in the range [-1, 1].</summary>
    public float MoveX;

    /// <summary>Move axis along world Z, expected in the range [-1, 1].</summary>
    public float MoveZ;

    /// <summary>Whether the attack button is held this frame.</summary>
    public bool Attack;

    /// <summary>
    /// Client-assigned, monotonically increasing input number. Client-side
    /// prediction and server reconciliation key on this.
    /// </summary>
    public uint Sequence;
}

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

    /// <summary>
    /// Aim direction on the ground plane (world X), toward the cursor. When an
    /// attack fires the server faces the player this way, so the swing goes where
    /// the player aimed rather than where they last moved. (0,0) means "no aim" —
    /// the server falls back to the movement facing.
    /// </summary>
    public float AimX;

    /// <summary>Aim direction on the ground plane (world Z), toward the cursor.</summary>
    public float AimZ;

    /// <summary>Whether the attack button is held this frame.</summary>
    public bool Attack;

    /// <summary>
    /// Client-assigned, monotonically increasing input number. Client-side
    /// prediction and server reconciliation key on this.
    /// </summary>
    public uint Sequence;
}

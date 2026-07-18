using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// What a decorative map prop is. Serialized as a byte (in the map JSON and the
/// geometry packet), so keep the numbering stable within a wire version.
/// </summary>
public enum PropType : byte
{
    /// <summary>A standing fire bowl: warm light + flame, the realm's waymarkers.</summary>
    Brazier = 0,
}

/// <summary>
/// A purely cosmetic map feature the client renders (braziers and the like).
/// Props never collide and the simulation never reads them — they ride the
/// geometry only so every client can dress the realm identically.
/// </summary>
public readonly record struct DungeonProp(PropType Type, Vector3 Position);

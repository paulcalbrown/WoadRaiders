using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// A mover mid-crossing on a baked off-mesh link — the falling (or boarding)
/// state between leaving one walkable surface and landing on another. While a
/// crossing is active the mover is rooted: the arc owns its position until the
/// landing tick.
///
/// Pure data and pure functions: the duration and the position at any tick
/// derive only from the link's endpoints and <see cref="SimConstants"/>, so
/// the server and a predicting client compute identical arcs from the
/// (Code, Tick) pair that rides the wire in PlayerSnapshot. The endpoints
/// themselves never ride the wire — both peers re-derive them from the same
/// baked bytes via <see cref="IRealmGeometry.TryGetLink"/>.
/// </summary>
public struct LinkTraversal
{
    /// <summary>Oriented link code from <see cref="IRealmGeometry.FindLink"/>; -1 = not crossing.</summary>
    public int Code;

    /// <summary>Fixed ticks completed since boarding the link.</summary>
    public ushort Tick;

    /// <summary>Ticks the whole crossing takes — <see cref="Duration"/> of the endpoints.</summary>
    public ushort TotalTicks;

    /// <summary>The boarded endpoint (the lip stepped off).</summary>
    public Vector3 From;

    /// <summary>The landing — the bake scout's rest position, already on exact ground.</summary>
    public Vector3 To;

    public readonly bool Active => Code >= 0;

    public static readonly LinkTraversal None = new() { Code = -1 };

    /// <summary>Board a link at its lip.</summary>
    public static LinkTraversal Begin(int code, Vector3 from, Vector3 to) => new()
    {
        Code = code,
        Tick = 0,
        TotalTicks = Duration(from, to),
        From = from,
        To = to,
    };

    /// <summary>
    /// Rebuild mid-crossing state from the wire pair (reconciliation). A code
    /// the geometry does not know — or no geometry at all — restores to
    /// <see cref="None"/> rather than trusting the wire.
    /// </summary>
    public static LinkTraversal Restore(int code, ushort tick, IRealmGeometry geometry,
                                        float radius = SimConstants.CharacterRadius)
    {
        if (code < 0 || !geometry.TryGetLink(code, out var from, out var to, radius))
            return None;
        var crossing = Begin(code, from, to);
        // The server clears a finished crossing in the same tick it lands, so a
        // live wire tick is always short of the total; clamp anything else.
        crossing.Tick = (ushort)Math.Min(tick, crossing.TotalTicks - 1);
        return crossing;
    }

    /// <summary>
    /// Where the crossing stands after <paramref name="tick"/> of TotalTicks:
    /// linear on the ground plane, eased on Y — falls accelerate into the drop,
    /// boardings hop up fast and settle.
    /// </summary>
    public readonly Vector3 PositionAt(int tick)
    {
        var t = Math.Clamp(tick / (float)TotalTicks, 0f, 1f);
        var ease = To.Y <= From.Y ? t * t : 1f - (1f - t) * (1f - t);
        return new Vector3(
            From.X + (To.X - From.X) * t,
            From.Y + (To.Y - From.Y) * ease,
            From.Z + (To.Z - From.Z) * t);
    }

    /// <summary>
    /// How many fixed ticks a crossing takes: the vertical at
    /// <see cref="SimConstants.LinkFallSpeed"/> or the horizontal at twice run
    /// speed, whichever is longer — a long shallow link must not fling the
    /// mover sideways faster than any fall could. Never fewer than 2, so even
    /// a step-sized boarding is a visible motion, not a teleport.
    /// </summary>
    public static ushort Duration(Vector3 from, Vector3 to)
    {
        var vertical = MathF.Abs(to.Y - from.Y) / (SimConstants.LinkFallSpeed * SimConstants.TickDelta);
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        var horizontal = MathF.Sqrt(dx * dx + dz * dz) / (2f * SimConstants.PlayerMoveSpeed * SimConstants.TickDelta);
        return (ushort)Math.Max(2, (int)MathF.Ceiling(MathF.Max(vertical, horizontal)));
    }
}

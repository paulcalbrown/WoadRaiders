using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Client-side prediction + server reconciliation for the local player.
///
/// It runs a one-player <see cref="GameWorld"/> using the exact same movement
/// rules (and the same <see cref="IDungeonGeometry"/>) as the server, so
/// predicting locally can never diverge from the server's physics. The flow:
///   1. Every client tick, call <see cref="Predict"/> with the local input. The
///      player moves immediately and the input is buffered as "unacknowledged".
///   2. When an authoritative snapshot arrives, call <see cref="Reconcile"/> with
///      the server's position and the last input sequence it processed. Acked
///      inputs are dropped, the player is snapped to the server position, and the
///      still-in-flight inputs are replayed on top — correcting any drift.
///
/// Engine-free and deterministic, so it is fully unit-testable.
/// </summary>
public sealed class ClientPrediction
{
    private readonly GameWorld _world = new();
    private readonly List<PlayerInput> _pending = new();
    private readonly int _localPlayerId;

    public ClientPrediction(int localPlayerId, Vector3 startPosition, IDungeonGeometry? geometry = null)
    {
        _world.Geometry = geometry; // predict against the same geometry the server uses
        _localPlayerId = localPlayerId;
        var player = _world.AddPlayer(localPlayerId, "local");
        player.Position = startPosition;
    }

    /// <summary>The current predicted position of the local player.</summary>
    public Vector3 Position => _world.Players[_localPlayerId].Position;

    /// <summary>Inputs sent but not yet acknowledged by the server.</summary>
    public int PendingInputCount => _pending.Count;

    /// <summary>
    /// Apply one locally-generated input immediately and remember it until the
    /// server acknowledges it. Returns the new predicted position.
    /// </summary>
    public Vector3 Predict(PlayerInput input)
    {
        _pending.Add(input);
        _world.SetInput(_localPlayerId, input);
        _world.Step();
        return Position;
    }

    /// <summary>
    /// Fold in an authoritative snapshot: drop inputs the server has already
    /// processed, snap to the server position and attack timers, then replay the
    /// inputs still in flight. Returns the corrected predicted position.
    ///
    /// The attack timers must be restored too, not just position: a swing roots the
    /// player, so replaying the still-pending inputs against a stale attack window
    /// would wrongly root (or free) the pre-swing moving ticks and leave the
    /// position short of the server's — a correction that then eases out as a
    /// visible glide. Restoring the server's authoritative <paramref name="attackAnimRemaining"/>
    /// and <paramref name="attackCooldown"/> makes the replay reproduce the root exactly.
    /// </summary>
    public Vector3 Reconcile(Vector3 authoritativePosition, float attackAnimRemaining, float attackCooldown,
                             uint lastProcessedInput)
    {
        _pending.RemoveAll(i => i.Sequence <= lastProcessedInput);

        var player = _world.Players[_localPlayerId];
        player.Position = authoritativePosition;
        player.Velocity = Vector3.Zero;
        player.AttackAnimRemaining = attackAnimRemaining;
        player.AttackCooldown = attackCooldown;

        foreach (var input in _pending)
        {
            _world.SetInput(_localPlayerId, input);
            _world.Step();
        }

        return Position;
    }
}

using Godot;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;
using SysVec3 = System.Numerics.Vector3;

namespace WoadRaiders.Client;

/// <summary>
/// Drives the local player's predicted simulation: the fixed-tick accumulator,
/// input sampling and sending, the predicted attack cadence, server
/// reconciliation, and the smoothed render position (interpolated between fixed
/// ticks, with reconciliation corrections eased out as a decaying render error).
/// Movement is predicted; damage, loot, and equipment stay server-authoritative.
/// </summary>
public sealed class LocalPlayer
{
    private const float ErrorDecayRate = 10f; // how fast reconciliation corrections ease out
    private const float MaxSnapError = 120f;  // above this, snap (respawn/teleport) not smooth
    private const int MaxCatchUpTicks = 5;    // per frame; a longer stall drops the lost time
    private const float ArriveRadius = 10f;   // click-to-move stops once this close to the target
    private const float SpawnWalkDistance = 105f; // render starts this far behind the spawn (behind the set-back portal)
    private const float SpawnWalkSeconds = 1.1f; // how long the cosmetic walk-out of the portal takes

    private readonly ClientConnection _connection;
    private readonly CameraRig _camera;
    private ClientPrediction? _prediction;
    private AttackPrediction _attack;
    private uint _inputSequence; // monotonic across reconnects — the server buffer only needs increasing
    private double _tickAccumulator;
    private SysVec3 _prevTickPos;  // predicted position at the previous fixed tick
    private SysVec3 _renderPos;    // interpolated position actually drawn this frame
    private SysVec3 _renderError;  // reconciliation correction being smoothed out of the render
    private Vector3 _aim;          // live cursor aim on the ground plane (XZ, unit), sent every tick
    private Vector3 _attackFacing; // _aim captured when the current swing fired — held for the whole swing
    private Vector3? _moveTarget;  // ground point the player is pathing toward (right-click), if any
    private bool _moveClickHeld;   // right button down last tick, to detect the press edge
    private SysVec3 _spawnWalkDir; // unit ground heading the emerging character walks (out of the portal)
    private float _spawnWalkRemaining; // seconds left in the cosmetic spawn walk-out; 0 when done

    /// <summary>Fired on a right-click, with the ground point clicked — the game drops a marker there.</summary>
    public event Action<Vector3>? MoveClicked;

    public LocalPlayer(ClientConnection connection, CameraRig camera)
    {
        _connection = connection;
        _camera = camera;
    }

    public int PlayerId { get; private set; } = -1;

    /// <summary>False until the first Welcome arrives; nothing is predicted before that.</summary>
    public bool Active => _prediction is not null;

    /// <summary>Predicted attack-anim window, so the local swing is instant.</summary>
    public bool Swinging => _attack.Swinging;

    /// <summary>The smoothed feet position to draw (and follow) this frame.</summary>
    public Vector3 RenderPosition => _renderPos.ToGodot();

    /// <summary>
    /// The aim locked in when the current swing fired (the click direction). The
    /// model holds this through the swing, so it doesn't chase the mouse mid-attack.
    /// </summary>
    public Vector3 AttackFacing => _attackFacing;

    /// <summary>Start (or on reconnect, restart) predicting as the given player and class.</summary>
    public void BeginSession(int playerId, SysVec3 spawn, IDungeonGeometry? geometry, CharacterClass cls)
    {
        PlayerId = playerId;
        // The class shapes prediction itself: move speed and the attack root in the
        // predicted world, and the swing cadence — all must mirror the server's.
        _prediction = new ClientPrediction(playerId, spawn, geometry, cls);
        _attack = new AttackPrediction(ClassArchetypes.Of(cls).AttackCooldown);
        _prevTickPos = _renderPos = _prediction.Position;
        _renderError = SysVec3.Zero;
        _tickAccumulator = 0;
        _moveTarget = null;
        _moveClickHeld = false;

        // Cosmetic spawn intro: pull the RENDER position back behind the entrance
        // portal and ease it forward to the spawn, so the character walks out of the
        // portal. Prediction and reconciliation are untouched — only what's drawn
        // shifts. "Out of the portal" is the iso camera's ground heading (toward the
        // viewer), matching the portal's camera-facing angle.
        var camGround = new SysVec3(CameraRig.Offset.X, 0f, CameraRig.Offset.Z);
        _spawnWalkDir = camGround.LengthSquared() > 0.0001f ? SysVec3.Normalize(camGround) : SysVec3.UnitX;
        _spawnWalkRemaining = SpawnWalkSeconds;
    }

    /// <summary>Run the fixed-tick loop for this frame: sample, predict, send.</summary>
    public void Advance(double delta)
    {
        if (_prediction is null)
            return;

        _tickAccumulator += delta;
        var catchUp = MaxCatchUpTicks;
        while (_tickAccumulator >= SimConstants.TickDelta && catchUp-- > 0)
        {
            _prevTickPos = _prediction.Position; // remember where we were before stepping
            Tick();
            _tickAccumulator -= SimConstants.TickDelta;
        }
    }

    /// <summary>
    /// Fold in the local player's snapshot entry: reconcile the prediction and
    /// absorb the correction into a decaying render error so the authoritative
    /// snap eases in over a few frames instead of popping.
    /// </summary>
    public void Reconcile(in PlayerSnapshot snapshot)
    {
        if (_prediction is null)
            return;
        var before = _prediction.Position;
        _prediction.Reconcile(new SysVec3(snapshot.X, snapshot.Y, snapshot.Z),
                              snapshot.AttackAnim, snapshot.AttackCooldown, snapshot.LastProcessedInput);
        _renderError += before - _prediction.Position;
    }

    /// <summary>Per-frame: interpolate between the last two fixed ticks and decay the render error.</summary>
    public void UpdateRenderPosition(double delta)
    {
        if (_prediction is null)
            return;

        // Interpolate between the last two fixed ticks so 30 Hz motion renders smoothly.
        var alpha = Mathf.Clamp((float)(_tickAccumulator / SimConstants.TickDelta), 0f, 1f);
        // Ease reconciliation corrections out instead of popping — but snap a large jump
        // (respawn/teleport), which isn't a correction and shouldn't be smoothed.
        if (_renderError.LengthSquared() > MaxSnapError * MaxSnapError)
            _renderError = SysVec3.Zero;
        _renderError *= Mathf.Exp(-ErrorDecayRate * (float)delta);
        _renderPos = SysVec3.Lerp(_prevTickPos, _prediction.Position, alpha) + _renderError;

        // Spawn walk-out: subtract a decaying offset so the render emerges from
        // behind the portal and settles at the spawn. f² eases it — quick out of
        // the gate, slowing to a stop — and the CharacterView reads the forward
        // motion as a walk (run clip + facing) on its own.
        if (_spawnWalkRemaining > 0f)
        {
            _spawnWalkRemaining = Mathf.Max(0f, _spawnWalkRemaining - (float)delta);
            var f = _spawnWalkRemaining / SpawnWalkSeconds;
            _renderPos -= _spawnWalkDir * (SpawnWalkDistance * f * f);
        }
    }

    private void Tick()
    {
        _aim = ComputeAim();

        // Left mouse button swings toward the cursor; Space swings in the character's
        // current facing. A zero aim tells the sim to keep the pre-attack facing — so
        // the Space swing lands where the character already faces, not at the mouse.
        var mouseAttack = Input.IsActionPressed(ClientActions.Attack);
        var forwardAttack = Input.IsActionPressed(ClientActions.AttackForward);
        var attack = mouseAttack || forwardAttack;
        var attackAim = mouseAttack ? _aim : Vector3.Zero; // mouse wins if both are held

        // Attacking takes priority over moving: a swing cancels the click-to-move order
        // (and the sim roots the player for the swing). A held movement key resumes on
        // its own once the swing ends; a cancelled click-to-move order does not.
        if (attack)
            _moveTarget = null;
        else
            ReadMoveClick();
        var move = MovementIntent();

        var input = new PlayerInput
        {
            MoveX = move.X, MoveZ = move.Y, AimX = attackAim.X, AimZ = attackAim.Z,
            Attack = attack, Sequence = ++_inputSequence,
        };

        // Predict our own attack animation so the swing is instant (everyone else plays
        // theirs from the authoritative snapshot flag). Lock the facing at the moment the
        // swing fires: the cursor for a mouse attack, or zero for a Space attack (no
        // visual override → the model keeps facing where it already is).
        if (_attack.Tick(attack))
            _attackFacing = attackAim;

        _prediction!.Predict(input);

        // ReliableOrdered so the server's per-player input buffer receives every input
        // exactly once, in order — that 1:1 replay is what keeps reconciliation drift-free.
        _connection.Send(MessageType.Input, new InputPacket
        {
            MoveX = move.X, MoveZ = move.Y, AimX = attackAim.X, AimZ = attackAim.Z,
            Attack = attack, Sequence = input.Sequence,
        }, DeliveryMethod.ReliableOrdered);
    }

    // Right mouse button: click or hold to path toward the cursor. On the press
    // edge, announce the click so the game drops a marker there.
    private void ReadMoveClick()
    {
        if (Input.IsActionPressed(ClientActions.MoveTo) && TryProjectMouseToGround(out var clicked))
        {
            _moveTarget = clicked;
            if (!_moveClickHeld)
                MoveClicked?.Invoke(clicked);
            _moveClickHeld = true;
        }
        else
        {
            _moveClickHeld = false;
        }
    }

    // Keyboard steering wins and cancels any click-to-move; otherwise head toward
    // the move target (full speed) until we arrive within ArriveRadius.
    private Vector2 MovementIntent()
    {
        var keys = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        if (keys != Vector2.Zero)
        {
            _moveTarget = null;
            return keys;
        }

        if (_moveTarget is { } target)
        {
            var pos = _prediction!.Position.ToGodot();
            var to = new Vector3(target.X - pos.X, 0f, target.Z - pos.Z);
            if (to.LengthSquared() > ArriveRadius * ArriveRadius)
            {
                var dir = to.Normalized();
                return new Vector2(dir.X, dir.Z);
            }
            _moveTarget = null; // arrived
        }
        return Vector2.Zero;
    }

    // Unit direction from the player toward the cursor. The cursor is projected
    // onto the plane at the projectile's FLIGHT height (feet + eye height), not
    // the floor: a bolt leaves the caster at eye height and flies level, so
    // aiming at the floor point under the cursor would launch it out raised-but-
    // parallel and — under the angled iso camera — it would visibly track a line
    // above where you clicked. Aiming at the flight plane makes the shot pass
    // through the cursor. Melee shares the aim; its wide arc shrugs off the few-
    // degrees difference. Keeps the last aim if the cursor sits on the player or
    // the ray runs parallel to the plane.
    private Vector3 ComputeAim()
    {
        var playerPos = _prediction!.Position.ToGodot();
        if (!TryProjectMouseToPlane(playerPos.Y + SimConstants.EyeHeight, out var hit))
            return _aim;
        var flat = new Vector3(hit.X - playerPos.X, 0f, hit.Z - playerPos.Z);
        return flat.LengthSquared() > 0.01f ? flat.Normalized() : _aim;
    }

    // Intersect the cursor ray with the ground plane at the player's feet height
    // (where a click-to-move order lands).
    private bool TryProjectMouseToGround(out Vector3 point) =>
        TryProjectMouseToPlane(_prediction!.Position.Y, out point);

    // Intersect the cursor ray with the horizontal plane at world height planeY.
    private bool TryProjectMouseToPlane(float planeY, out Vector3 point)
    {
        point = default;
        var mouse = _camera.GetViewport().GetMousePosition();
        var from = _camera.ProjectRayOrigin(mouse);
        var dir = _camera.ProjectRayNormal(mouse);
        if (Mathf.Abs(dir.Y) < 1e-5f)
            return false;

        var t = (planeY - from.Y) / dir.Y;
        point = from + dir * t;
        return true;
    }
}

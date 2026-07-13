using WoadRaiders.Core;
using WoadRaiders.Shared;

namespace WoadRaiders.Server;

/// <summary>
/// One authoritative match: the world, its spawn policy, and the per-player input
/// buffers. It owns everything the simulation needs and nothing about the network —
/// the server feeds it input and asks it for the state to broadcast. Engine-free and,
/// given a seeded RNG, deterministic, so it can be driven in tests without a socket.
/// </summary>
internal sealed class GameSession
{
    private readonly DungeonGeometry _dungeon;
    private readonly GameWorld _world;
    private readonly SpawnDirector _director;
    private readonly Dictionary<int, ServerInputBuffer> _inputBuffers = new();

    public GameSession(DungeonGeometry dungeon, Random rng)
    {
        _dungeon = dungeon;
        _world = new GameWorld { Geometry = dungeon };
        _director = new SpawnDirector(_world, dungeon, rng);

        // The session owns the wording of match events so the transport layer never has
        // to know about the boss (its name, its respawn timing) — it just relays Notice.
        // The boss falling opens the exit portal where he stood; walking into it ends a
        // player's run (GameWorld detects the step-through; ConsumePortalExits reports it).
        _director.BossFell += () =>
        {
            if (_dungeon.BossSpawn is { } bossPos)
                _world.OpenPortal(bossPos);
            Raise(SessionEventKind.BossFell,
                $"The Barrow King has fallen! A portal tears open in his chamber — " +
                $"he returns in {SpawnDirector.BossRespawnDelayTicks / SimConstants.TickRate}s.");
        };
        _director.BossRose += () => Raise(SessionEventKind.BossRose, "The Barrow King rises again.");
    }

    /// <summary>
    /// Notable match events (boss lifecycle now; waves, exits, downed players later) for
    /// the host to log or relay. The session owns the wording, so the transport layer
    /// stays free of any game-domain knowledge.
    /// </summary>
    public event Action<SessionEvent>? Notice;

    /// <summary>The current simulation tick.</summary>
    public int Tick => _world.Tick;

    /// <summary>Populate the map (one enemy per marker, plus the boss). Returns regulars spawned.</summary>
    public int SpawnInitial()
    {
        var spawned = _director.SpawnInitial();
        if (_dungeon.BossSpawn is not null)
            Raise(SessionEventKind.BossAwaits, "The Barrow King waits in his chamber.");
        return spawned;
    }

    /// <summary>
    /// Spawn a joining player with their chosen class. Players are created on the
    /// JoinRequest, not the connect, so the class is known from the first tick
    /// they exist — no snapshot ever carries an unclassed placeholder. A repeated
    /// JoinRequest mid-match renames at most — never a fresh body, so it can't be
    /// abused as a free heal or teleport to spawn.
    /// </summary>
    public void AddPlayer(int id, string name, CharacterClass cls = CharacterClass.Knight)
    {
        if (_world.Players.TryGetValue(id, out var existing))
        {
            existing.Name = name;
            return;
        }

        var player = _world.AddPlayer(id, name, cls);
        player.Position = _dungeon.SpawnPoint;
        _inputBuffers[id] = new ServerInputBuffer();
    }

    /// <summary>Tolerates a peer that connected but never joined (no player exists yet).</summary>
    public void RemovePlayer(int id)
    {
        _world.RemovePlayer(id);
        _inputBuffers.Remove(id);
    }

    /// <summary>Buffer a player's input intent; it is applied one-per-tick by <see cref="Step"/>.</summary>
    public void EnqueueInput(int id, in PlayerInput input)
    {
        if (_inputBuffers.TryGetValue(id, out var buffer))
            buffer.Enqueue(input);
    }

    /// <summary>
    /// Advance the sim one tick: hand each player exactly one buffered input in sequence
    /// order, so the server replays each client's stream 1:1 and reconciliation stays
    /// drift-free. A priming or starved buffer <em>holds</em> the player — a zero-move
    /// input tagged with their last processed sequence — so <c>LastProcessedInput</c>
    /// never regresses and the client reconciles the freeze exactly rather than fighting
    /// a re-applied stale input. Then step the world and advance spawn policy.
    /// </summary>
    public void Step()
    {
        foreach (var player in _world.Players.Values)
        {
            if (_inputBuffers.TryGetValue(player.Id, out var buffer) && buffer.TryDequeue(out var input))
                _world.SetInput(player.Id, input);
            else
                _world.SetInput(player.Id, new PlayerInput { Sequence = player.LastProcessedInput });
        }

        _world.Step();
        _director.Update();
    }

    /// <summary>Project the current world into a broadcastable snapshot packet.</summary>
    public WorldSnapshotPacket Snapshot() => WorldSnapshot.From(_world);

    /// <summary>Loot collected since the last call, for the server to deliver to each player.</summary>
    public IReadOnlyList<LootPickup> ConsumePickups() => _world.ConsumePickups();

    /// <summary>
    /// Players who stepped through the boss portal since the last call, each with
    /// their run summary. The world already removed them the tick they exited;
    /// this also clears their input buffers, finishing the session-side removal.
    /// </summary>
    public IReadOnlyList<RunReport> ConsumePortalExits()
    {
        var exits = _world.ConsumePortalExits();
        if (exits.Count == 0)
            return Array.Empty<RunReport>();

        var reports = new RunReport[exits.Count];
        for (var i = 0; i < exits.Count; i++)
        {
            var exit = exits[i];
            reports[i] = new RunReport(exit.PlayerId, exit.PlayerName, exit.Gold, exit.ItemsLooted,
                exit.DurationTicks / SimConstants.TickRate, _world.EnemiesSlain);
            RemovePlayer(exit.PlayerId); // world part is a no-op; drops the input buffer
        }
        return reports;
    }

    /// <summary>Try to equip an item; returns the resulting loadout to send back, or null on no-op.</summary>
    public EquipOutcome? TryEquip(int playerId, int itemId)
    {
        if (!_world.Players.TryGetValue(playerId, out var player) || !player.TryEquip(itemId))
            return null;

        return new EquipOutcome(
            EquippedId(player, EquipSlot.Weapon),
            EquippedId(player, EquipSlot.Armor),
            EquippedId(player, EquipSlot.Trinket),
            player.AttackDamage,
            player.DamageReduction);
    }

    private static int EquippedId(PlayerState player, EquipSlot slot) =>
        player.Equipped.TryGetValue(slot, out var item) ? item.Id : 0;

    private void Raise(SessionEventKind kind, string message) => Notice?.Invoke(new SessionEvent(kind, message));
}

/// <summary>A player's equipped item ids and derived combat stats after an equip.</summary>
internal readonly record struct EquipOutcome(
    int WeaponId, int ArmorId, int TrinketId, float AttackDamage, float DamageReduction);

/// <summary>One finished run: what a player carried out through the portal.
/// FoesSlain is the warband's shared tally, not this player's alone.</summary>
internal readonly record struct RunReport(
    int PlayerId, string PlayerName, int Gold, int ItemsLooted, int DurationSeconds, int FoesSlain);

/// <summary>A notable match event — for host logging now, telemetry or client notices later.</summary>
internal readonly record struct SessionEvent(SessionEventKind Kind, string Message);

internal enum SessionEventKind
{
    BossAwaits,
    BossFell,
    BossRose,
}

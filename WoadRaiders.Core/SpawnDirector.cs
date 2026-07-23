using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// Owns the map's enemy-population policy: the initial spawn, keeping the regular
/// enemy count topped up, and the boss's death/respawn cycle. This is simulation
/// policy, so it lives in <c>Core</c> — engine-free, deterministic, and driven by
/// the sim tick (not wall-clock time), so a server stall can never bank a burst of
/// overdue spawns and the whole thing is unit-testable without a server or a clock.
///
/// Drive it by calling <see cref="SpawnInitial"/> once at startup and
/// <see cref="Update"/> exactly once per <see cref="GameWorld.Step"/>.
/// </summary>
public sealed class SpawnDirector
{
    /// <summary>
    /// Whether the regular population is topped back up as it is killed.
    ///
    /// OFF. A realm you cannot finish clearing is a realm you cannot finish: at
    /// one enemy every six seconds a lone raider was refilling rooms behind
    /// themselves faster than they could advance, so a cleared chamber stopped
    /// meaning anything and the descent had no sense of progress in it at all.
    ///
    /// This is a stopgap, not a design decision — it is the endless-dungeon
    /// behaviour switched off while the realm is being tuned for a single
    /// player, and it belongs behind a per-realm or per-mode setting rather than
    /// a constant. The boss is unaffected: it still returns, because the portal
    /// and the end of a run hang off that cycle.
    /// </summary>
    public const bool RepopulateRegularsByDefault = false;

    /// <summary>Top the regular population up by one enemy at most this often.</summary>
    public const int RepopIntervalTicks = 6 * SimConstants.TickRate;

    /// <summary>
    /// The most regular enemies a realm may hold at once. A CEILING, not a
    /// target: the population a realm actually keeps is its own marker count
    /// (see the constructor), so raising this changes nothing for a realm that
    /// does not place more markers.
    ///
    /// It stands at 300 because interest management removed what used to bound
    /// it. Snapshots ride Unreliable, split across MTU-sized chunks, and losing
    /// any one chunk discards the whole update — so before each raider was sent
    /// only what is within sight of them, every extra enemy was paid for by
    /// every player at 20 Hz, and a few hundred of them turned a lossy link into
    /// a stutter. Filtered, the wire cost tracks what a raider can SEE rather
    /// than what the realm holds, and eight players fighting in a crypt is the
    /// same handful of chunks whether the realm holds forty foes or four
    /// hundred. What binds now is the client's concurrent character count, which
    /// the same filter also bounds.
    /// </summary>
    public const int MaxLiveEnemies = 300;

    /// <summary>Below this a map is not a raid; a realm with too few markers is topped up to it.</summary>
    public const int MinLiveEnemies = 4;

    /// <summary>The boss returns this long after it is slain, so the fight repeats.</summary>
    public const int BossRespawnDelayTicks = 120 * SimConstants.TickRate;

    /// <summary>Regular respawns keep at least this far from the player spawn.</summary>
    public const float MinRespawnDistanceFromPlayerSpawn = 200f;

    private readonly bool _repopulateRegulars;

    private static readonly float MinRespawnDistanceSq =
        MinRespawnDistanceFromPlayerSpawn * MinRespawnDistanceFromPlayerSpawn;

    private readonly GameWorld _world;
    private readonly RealmDefinition _realm;
    private readonly Random _rng;

    private int _nextRepopTick;
    private int? _bossReturnTick; // sim tick the boss returns; null while it is alive or not yet fallen this cycle

    /// <summary>Raised the tick the boss is first found dead (fires once per death).</summary>
    public event Action? BossFell;

    /// <summary>Raised the tick the boss respawns.</summary>
    public event Action? BossRose;

    public SpawnDirector(GameWorld world, RealmDefinition realm, Random rng,
                         bool repopulateRegulars = RepopulateRegularsByDefault)
    {
        _world = world;
        _realm = realm;
        _rng = rng;
        _repopulateRegulars = repopulateRegulars;
        // Map-driven density: target one enemy per typed marker, clamped to a sane band.
        TargetEnemyCount = Math.Clamp(realm.EnemySpawns.Count, MinLiveEnemies, MaxLiveEnemies);
    }

    /// <summary>The regular (non-boss) enemy population the director maintains.</summary>
    public int TargetEnemyCount { get; }

    /// <summary>True once the boss has fallen and is waiting to return.</summary>
    public bool BossIsDown => _bossReturnTick is not null;

    /// <summary>
    /// Populate the map: one enemy per typed marker up to the target (so the authored
    /// mix is exact), random top-ups if the target exceeds the marker count, plus the
    /// boss. Returns how many regular enemies were spawned.
    /// </summary>
    public int SpawnInitial()
    {
        var markers = _realm.EnemySpawns;
        for (var i = 0; i < TargetEnemyCount; i++)
        {
            var spawn = i < markers.Count ? markers[i] : RandomSpawnAwayFromPlayers();
            _world.SpawnEnemy(spawn.Position, spawn.Type);
        }

        if (_realm.BossSpawn is { } bossPos)
            _world.SpawnEnemy(bossPos, EnemyType.Boss);

        _nextRepopTick = _world.Tick + RepopIntervalTicks;
        return TargetEnemyCount;
    }

    /// <summary>Advance spawn policy. Call exactly once per <see cref="GameWorld.Step"/>.</summary>
    public void Update()
    {
        if (_repopulateRegulars && _world.Tick >= _nextRepopTick)
        {
            _nextRepopTick = _world.Tick + RepopIntervalTicks;
            if (RegularEnemyCount() < TargetEnemyCount)
            {
                var spawn = RandomSpawnAwayFromPlayers();
                _world.SpawnEnemy(spawn.Position, spawn.Type);
            }
        }

        UpdateBoss();
    }

    private void UpdateBoss()
    {
        if (_realm.BossSpawn is not { } bossPos)
            return;

        if (BossIsAlive())
            return;

        if (_bossReturnTick is null)
        {
            _bossReturnTick = _world.Tick + BossRespawnDelayTicks;
            BossFell?.Invoke();
        }
        else if (_world.Tick >= _bossReturnTick)
        {
            _bossReturnTick = null;
            _world.SpawnEnemy(bossPos, EnemyType.Boss);
            BossRose?.Invoke();
        }
    }

    private int RegularEnemyCount()
    {
        var count = 0;
        foreach (var enemy in _world.Enemies.Values)
            if (enemy.Type != EnemyType.Boss)
                count++;
        return count;
    }

    private bool BossIsAlive()
    {
        foreach (var enemy in _world.Enemies.Values)
            if (enemy.Type == EnemyType.Boss)
                return true;
        return false;
    }

    private EnemySpawnPoint RandomSpawnAwayFromPlayers()
    {
        // A random typed marker, but not right on top of the player spawn.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var spawn = _realm.RandomEnemySpawn(_rng);
            if (Vector3.DistanceSquared(spawn.Position, _realm.SpawnPoint) > MinRespawnDistanceSq)
                return spawn;
        }
        return _realm.RandomEnemySpawn(_rng);
    }
}

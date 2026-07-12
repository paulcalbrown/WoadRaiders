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
    /// <summary>Top the regular population up by one enemy at most this often.</summary>
    public const int RepopIntervalTicks = 6 * SimConstants.TickRate;

    /// <summary>The boss returns this long after it is slain, so the fight repeats.</summary>
    public const int BossRespawnDelayTicks = 120 * SimConstants.TickRate;

    /// <summary>Regular respawns keep at least this far from the player spawn.</summary>
    public const float MinRespawnDistanceFromPlayerSpawn = 200f;

    private static readonly float MinRespawnDistanceSq =
        MinRespawnDistanceFromPlayerSpawn * MinRespawnDistanceFromPlayerSpawn;

    private readonly GameWorld _world;
    private readonly DungeonGeometry _dungeon;
    private readonly Random _rng;

    private int _nextRepopTick;
    private int? _bossReturnTick; // sim tick the boss returns; null while it is alive or not yet fallen this cycle

    /// <summary>Raised the tick the boss is first found dead (fires once per death).</summary>
    public event Action? BossFell;

    /// <summary>Raised the tick the boss respawns.</summary>
    public event Action? BossRose;

    public SpawnDirector(GameWorld world, DungeonGeometry dungeon, Random rng)
    {
        _world = world;
        _dungeon = dungeon;
        _rng = rng;
        // Map-driven density: target one enemy per typed marker, clamped to a sane band.
        TargetEnemyCount = Math.Clamp(dungeon.TypedEnemySpawns.Count, 4, 40);
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
        var markers = _dungeon.TypedEnemySpawns;
        for (var i = 0; i < TargetEnemyCount; i++)
        {
            var spawn = i < markers.Count ? markers[i] : RandomSpawnAwayFromPlayers();
            _world.SpawnEnemy(spawn.Position, spawn.Type);
        }

        if (_dungeon.BossSpawn is { } bossPos)
            _world.SpawnEnemy(bossPos, EnemyType.Boss);

        _nextRepopTick = _world.Tick + RepopIntervalTicks;
        return TargetEnemyCount;
    }

    /// <summary>Advance spawn policy. Call exactly once per <see cref="GameWorld.Step"/>.</summary>
    public void Update()
    {
        if (_world.Tick >= _nextRepopTick)
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
        if (_dungeon.BossSpawn is not { } bossPos)
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
            var spawn = _dungeon.RandomEnemySpawn(_rng);
            if (Vector3.DistanceSquared(spawn.Position, _dungeon.SpawnPoint) > MinRespawnDistanceSq)
                return spawn;
        }
        return _dungeon.RandomEnemySpawn(_rng);
    }
}

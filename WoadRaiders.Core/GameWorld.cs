using System.Numerics;

namespace WoadRaiders.Core;

/// <summary>
/// The authoritative game simulation — pure C#, no engine, no networking.
/// Simulates in full 3D world space (Y-up); dungeon shape is abstracted behind
/// <see cref="IDungeonGeometry"/>. The dedicated server owns one instance and
/// advances it in fixed steps via <see cref="Step"/>. The client keeps its own
/// single-player copy for prediction.
/// </summary>
public sealed class GameWorld
{
    private readonly Dictionary<int, PlayerState> _players = new();
    private readonly Dictionary<int, PlayerInput> _inputs = new();
    private readonly Dictionary<int, EnemyState> _enemies = new();
    private readonly Dictionary<int, GroundItem> _groundItems = new();
    private readonly List<LootPickup> _pickups = new();
    private readonly Random _rng;
    private int _nextEnemyId = 1;
    private int _nextItemId = 1;

    public GameWorld() : this(new Random())
    {
    }

    /// <summary>Construct with a supplied RNG (seed it in tests for reproducible loot).</summary>
    public GameWorld(Random rng) => _rng = rng;

    public IReadOnlyDictionary<int, PlayerState> Players => _players;
    public IReadOnlyDictionary<int, EnemyState> Enemies => _enemies;
    public IReadOnlyDictionary<int, GroundItem> GroundItems => _groundItems;

    /// <summary>Dungeon geometry for collision and spawns. Null → open flat arena.</summary>
    public IDungeonGeometry? Geometry { get; set; }

    /// <summary>How many fixed steps have been simulated since the world began.</summary>
    public int Tick { get; private set; }

    // --- players ---

    public PlayerState AddPlayer(int id, string name)
    {
        var player = new PlayerState(id, name);
        _players[id] = player;
        _inputs[id] = default;
        return player;
    }

    public void RemovePlayer(int id)
    {
        _players.Remove(id);
        _inputs.Remove(id);
    }

    /// <summary>Record the latest intent for a player; applied on the next <see cref="Step"/>.</summary>
    public void SetInput(int id, PlayerInput input)
    {
        if (_players.ContainsKey(id))
            _inputs[id] = input;
    }

    // --- enemies ---

    public EnemyState SpawnEnemy(Vector3 position)
    {
        var enemy = new EnemyState(_nextEnemyId++) { Position = position };
        _enemies[enemy.Id] = enemy;
        return enemy;
    }

    // --- loot ---

    /// <summary>Place an item in the world (assigns it a fresh id).</summary>
    public GroundItem DropItem(Item item, Vector3 position)
    {
        var grounded = new GroundItem(item with { Id = _nextItemId++ }, position);
        _groundItems[grounded.Id] = grounded;
        return grounded;
    }

    /// <summary>
    /// Returns the pickups that happened since the last call, then clears the
    /// buffer. The server drains this each tick to notify the collecting players.
    /// </summary>
    public IReadOnlyList<LootPickup> ConsumePickups()
    {
        if (_pickups.Count == 0)
            return Array.Empty<LootPickup>();

        var drained = _pickups.ToArray();
        _pickups.Clear();
        return drained;
    }

    // --- simulation ---

    /// <summary>Advance the simulation by exactly one fixed tick.</summary>
    public void Step()
    {
        var dt = SimConstants.TickDelta;

        TickCooldowns(dt);
        MovePlayers(dt);
        ResolvePlayerAttacks();
        UpdateEnemies(dt);
        RemoveDeadEnemies(); // may drop loot
        ResolvePickups();    // players auto-collect nearby loot
        RespawnDeadPlayers();

        Tick++;
    }

    private void TickCooldowns(float dt)
    {
        foreach (var p in _players.Values)
            p.AttackCooldown = Math.Max(0f, p.AttackCooldown - dt);
        foreach (var e in _enemies.Values)
            e.AttackCooldown = Math.Max(0f, e.AttackCooldown - dt);
    }

    private void MovePlayers(float dt)
    {
        foreach (var player in _players.Values)
        {
            var input = _inputs.TryGetValue(player.Id, out var i) ? i : default;
            player.LastProcessedInput = input.Sequence;

            // Intent is on the ground plane; the geometry decides the resulting height.
            var move = new Vector3(input.MoveX, 0f, input.MoveZ);
            // Guard the zero vector (Normalize → NaN) and cap diagonal speed.
            if (move.LengthSquared() > 1f)
                move = Vector3.Normalize(move);

            player.Velocity = move * SimConstants.PlayerMoveSpeed;
            player.Position = ResolveMove(player.Position, player.Velocity * dt);
        }
    }

    private void ResolvePlayerAttacks()
    {
        var rangeSq = SimConstants.PlayerAttackRange * SimConstants.PlayerAttackRange;

        foreach (var player in _players.Values)
        {
            if (!player.IsAlive)
                continue;

            var input = _inputs.TryGetValue(player.Id, out var i) ? i : default;
            if (!input.Attack || player.AttackCooldown > 0f)
                continue;

            player.AttackCooldown = SimConstants.PlayerAttackCooldown;

            // Cleave: every enemy within range takes the hit (scaled by gear).
            foreach (var enemy in _enemies.Values)
            {
                if (enemy.IsAlive &&
                    Vector3.DistanceSquared(player.Position, enemy.Position) <= rangeSq)
                    enemy.TakeDamage(player.AttackDamage);
            }
        }
    }

    private void UpdateEnemies(float dt)
    {
        foreach (var enemy in _enemies.Values)
        {
            if (!enemy.IsAlive)
                continue;

            var target = NearestAlivePlayer(enemy.Position);
            if (target is null)
            {
                enemy.Velocity = Vector3.Zero;
                continue;
            }

            var toTarget = target.Position - enemy.Position;
            var distance = toTarget.Length();

            if (distance > SimConstants.EnemyAttackRange)
            {
                var dir = distance > 0.0001f ? toTarget / distance : Vector3.Zero;
                enemy.Velocity = dir * SimConstants.EnemyMoveSpeed;
                enemy.Position = ResolveMove(enemy.Position, enemy.Velocity * dt);
            }
            else
            {
                enemy.Velocity = Vector3.Zero;
                if (enemy.AttackCooldown <= 0f)
                {
                    // Armor soaks part of the hit, but every hit lands for at least 1.
                    var damage = Math.Max(1f, SimConstants.EnemyAttackDamage - target.DamageReduction);
                    target.TakeDamage(damage);
                    enemy.AttackCooldown = SimConstants.EnemyAttackCooldown;
                }
            }
        }
    }

    private void RemoveDeadEnemies()
    {
        List<int>? dead = null;
        foreach (var enemy in _enemies.Values)
            if (!enemy.IsAlive)
                (dead ??= new List<int>()).Add(enemy.Id);

        if (dead is null)
            return;

        foreach (var id in dead)
        {
            var enemy = _enemies[id];

            var drop = LootGenerator.TryRollDrop(_rng);
            if (drop is not null)
                DropItem(drop, enemy.Position);

            _enemies.Remove(id);
        }
    }

    private void ResolvePickups()
    {
        if (_groundItems.Count == 0)
            return;

        var radiusSq = SimConstants.ItemPickupRadius * SimConstants.ItemPickupRadius;

        List<int>? collected = null;
        foreach (var ground in _groundItems.Values)
        {
            var picker = NearestAlivePlayerWithin(ground.Position, radiusSq);
            if (picker is null)
                continue;

            picker.Inventory.Add(ground.Item);
            _pickups.Add(new LootPickup(picker.Id, ground.Item));
            (collected ??= new List<int>()).Add(ground.Id);
        }

        if (collected is null)
            return;

        foreach (var id in collected)
            _groundItems.Remove(id);
    }

    private PlayerState? NearestAlivePlayer(Vector3 from)
    {
        PlayerState? nearest = null;
        var bestSq = float.MaxValue;
        foreach (var player in _players.Values)
        {
            if (!player.IsAlive)
                continue;
            var d = Vector3.DistanceSquared(from, player.Position);
            if (d < bestSq)
            {
                bestSq = d;
                nearest = player;
            }
        }
        return nearest;
    }

    private PlayerState? NearestAlivePlayerWithin(Vector3 pos, float radiusSq)
    {
        PlayerState? nearest = null;
        var bestSq = radiusSq;
        foreach (var player in _players.Values)
        {
            if (!player.IsAlive)
                continue;
            var d = Vector3.DistanceSquared(pos, player.Position);
            if (d <= bestSq)
            {
                bestSq = d;
                nearest = player;
            }
        }
        return nearest;
    }

    private void RespawnDeadPlayers()
    {
        foreach (var player in _players.Values)
        {
            if (player.IsAlive)
                continue;

            // Simple immediate respawn at the dungeon start. TODO: respawn delay.
            player.Position = Geometry?.SpawnPoint ?? Vector3.Zero;
            player.Velocity = Vector3.Zero;
            player.Health = player.MaxHealth;
            player.AttackCooldown = 0f;
        }
    }

    private Vector3 ResolveMove(Vector3 pos, Vector3 delta) =>
        Geometry?.Move(pos, delta) ?? ClampToArena(pos + delta);

    private static Vector3 ClampToArena(Vector3 p) => new(
        Math.Clamp(p.X, -SimConstants.WorldHalfWidth, SimConstants.WorldHalfWidth),
        0f,
        Math.Clamp(p.Z, -SimConstants.WorldHalfHeight, SimConstants.WorldHalfHeight));
}

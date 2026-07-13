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
    private readonly Dictionary<int, ProjectileState> _projectiles = new();
    private readonly List<LootPickup> _pickups = new();
    private readonly Random _rng;
    private int _nextEnemyId = 1;
    private int _nextItemId = 1;
    private int _nextProjectileId = 1;

    public GameWorld() : this(new Random())
    {
    }

    /// <summary>Construct with a supplied RNG (seed it in tests for reproducible loot).</summary>
    public GameWorld(Random rng) => _rng = rng;

    public IReadOnlyDictionary<int, PlayerState> Players => _players;
    public IReadOnlyDictionary<int, EnemyState> Enemies => _enemies;
    public IReadOnlyDictionary<int, GroundItem> GroundItems => _groundItems;
    public IReadOnlyDictionary<int, ProjectileState> Projectiles => _projectiles;

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
        if (!_players.ContainsKey(id))
            return;

        // The transport hands us raw client floats. Non-finite intent (NaN/∞) would
        // sail through the magnitude cap (NaN > 1 is false) and poison the player's
        // position — which then broadcasts to every client. Neutralize it here, at
        // the simulation boundary, so no transport has to remember to.
        if (!float.IsFinite(input.MoveX)) input.MoveX = 0f;
        if (!float.IsFinite(input.MoveZ)) input.MoveZ = 0f;
        if (!float.IsFinite(input.AimX)) input.AimX = 0f;
        if (!float.IsFinite(input.AimZ)) input.AimZ = 0f;

        _inputs[id] = input;
    }

    // --- enemies ---

    public EnemyState SpawnEnemy(Vector3 position, EnemyType type = EnemyType.Minion)
    {
        var enemy = new EnemyState(_nextEnemyId++, type) { Position = position, HomePosition = position };
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

    /// <summary>Place a pile of gold coins in the world.</summary>
    public GroundItem DropGold(int amount, Vector3 position)
    {
        var pile = new GroundItem(_nextItemId++, LootKind.Gold, amount, position);
        _groundItems[pile.Id] = pile;
        return pile;
    }

    /// <summary>Place a health potion in the world (heals on walk-over).</summary>
    public GroundItem DropPotion(Vector3 position)
    {
        var potion = new GroundItem(_nextItemId++, LootKind.HealthPotion, (int)SimConstants.PotionHealAmount, position);
        _groundItems[potion.Id] = potion;
        return potion;
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
        UpdateEnemies(dt);       // mages fire bolts
        UpdateProjectiles(dt);   // bolts travel and strike
        ResolveEnemyDeaths();    // roll drops, then remove the slain
        ResolvePickups();        // players auto-collect nearby loot
        RespawnDeadPlayers();

        Tick++;
    }

    private void TickCooldowns(float dt)
    {
        foreach (var p in _players.Values)
        {
            p.AttackCooldown = Math.Max(0f, p.AttackCooldown - dt);
            p.AttackAnimRemaining = Math.Max(0f, p.AttackAnimRemaining - dt);
        }
        foreach (var e in _enemies.Values)
        {
            e.AttackCooldown = Math.Max(0f, e.AttackCooldown - dt);
            e.AttackAnimRemaining = Math.Max(0f, e.AttackAnimRemaining - dt);
        }
    }

    private void MovePlayers(float dt)
    {
        foreach (var player in _players.Values)
        {
            var input = _inputs.TryGetValue(player.Id, out var i) ? i : default;
            player.LastProcessedInput = input.Sequence;

            // A melee swing roots you: no movement while the swing plays, nor on the
            // tick it fires. Facing still comes from the swing's aim (ResolvePlayerAttacks).
            if (player.IsAttacking || (input.Attack && player.AttackReady))
            {
                player.Velocity = Vector3.Zero;
                continue;
            }

            // Intent is on the ground plane; the geometry decides the resulting height.
            var move = new Vector3(input.MoveX, 0f, input.MoveZ);
            var moveLenSq = move.LengthSquared();
            // Guard the zero vector (Normalize → NaN) and cap diagonal speed.
            if (moveLenSq > 1f)
                move = Vector3.Normalize(move);

            // Face where we're steering; hold the last facing while standing still,
            // so a stationary attack still strikes in a sensible direction.
            if (moveLenSq > 0.0001f)
                player.Facing = Vector3.Normalize(move);

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
            if (!input.Attack || !player.AttackReady)
                continue;

            player.AttackCooldown = SimConstants.PlayerAttackCooldown;
            player.AttackAnimRemaining = SimConstants.AttackAnimDuration;

            // Aim the swing where the player pointed (cursor), not where they last
            // moved. A zero aim (no cursor sent) leaves the movement facing intact.
            var aim = new Vector3(input.AimX, 0f, input.AimZ);
            if (aim.LengthSquared() > 0.0001f)
                player.Facing = Vector3.Normalize(aim);

            // Frontal cleave: every enemy within reach AND in front of you (the damage
            // arc) takes the hit — a swing through the arc, not a single target, but
            // still not a 360° sweep.
            foreach (var enemy in _enemies.Values)
            {
                if (!enemy.IsAlive)
                    continue;

                var toEnemy = enemy.Position - player.Position;
                var distSq = toEnemy.LengthSquared();
                if (distSq > rangeSq)
                    continue; // out of reach

                // Must be facing the enemy (the zero-distance case counts as "in contact").
                if (distSq > 0.0001f &&
                    Vector3.Dot(player.Facing, toEnemy / MathF.Sqrt(distSq)) < SimConstants.PlayerAttackArcDot)
                    continue; // behind or off to the side

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

            var arch = EnemyArchetypes.Of(enemy.Type);
            var target = NearestAlivePlayer(enemy.Position);
            if (target is null)
            {
                enemy.Aggroed = false;
                ReturnHome(enemy, arch, dt);
                continue;
            }

            var toTarget = target.Position - enemy.Position;
            var distance = toTarget.Length();

            // Aggro state: enemies guard their post until a player comes close enough
            // AND is visible — no spotting through walls. Once aggroed they pursue
            // until the target outruns the leash, then walk back to their post
            // (essential on large maps, or kited enemies pile up in travelled areas).
            if (!enemy.Aggroed)
            {
                if (distance <= arch.AggroRange && HasLineOfSight(enemy, target))
                    enemy.Aggroed = true;
            }
            else if (distance > arch.AggroRange * SimConstants.EnemyLeashFactor)
            {
                enemy.Aggroed = false;
            }

            if (!enemy.Aggroed)
            {
                ReturnHome(enemy, arch, dt);
                continue;
            }

            // In striking distance AND visible → attack; otherwise close in (an enemy
            // in range but behind a wall keeps advancing instead of zapping through it).
            if (distance <= arch.AttackRange && HasLineOfSight(enemy, target))
            {
                enemy.Velocity = Vector3.Zero;
                if (enemy.AttackCooldown <= 0f)
                {
                    if (arch.ProjectileSpeed > 0f)
                    {
                        // Ranged: launch a bolt at where the target is now (dodgeable);
                        // damage is dealt on impact by UpdateProjectiles, not here.
                        SpawnProjectile(enemy.Position, target.Position, arch);
                    }
                    else
                    {
                        // Armor soaks part of the hit, but every hit lands for at least 1.
                        var damage = Math.Max(1f, arch.AttackDamage - target.DamageReduction);
                        target.TakeDamage(damage);
                    }
                    enemy.AttackCooldown = arch.AttackCooldown;
                    enemy.AttackAnimRemaining = SimConstants.AttackAnimDuration;
                }
            }
            else
            {
                var dir = distance > 0.0001f ? toTarget / distance : Vector3.Zero;
                enemy.Velocity = dir * arch.MoveSpeed;
                enemy.Position = ResolveMove(enemy.Position, enemy.Velocity * dt, arch.Radius);
            }
        }
    }

    /// <summary>Walk an un-aggroed enemy back to its post, then stand guard.</summary>
    private void ReturnHome(EnemyState enemy, in EnemyArchetype arch, float dt)
    {
        var toHome = enemy.HomePosition - enemy.Position;
        var distance = toHome.Length();
        if (distance <= SimConstants.EnemyHomeEpsilon)
        {
            enemy.Velocity = Vector3.Zero;
            return;
        }

        var dir = toHome / distance;
        enemy.Velocity = dir * arch.MoveSpeed;
        enemy.Position = ResolveMove(enemy.Position, enemy.Velocity * dt, arch.Radius);
    }

    /// <summary>Sight line between two combatants, traced at eye height.</summary>
    private bool HasLineOfSight(Combatant from, Combatant to)
    {
        if (Geometry is null)
            return true; // open arena

        var eye = new Vector3(0f, SimConstants.EyeHeight, 0f);
        return Geometry.HasLineOfSight(from.Position + eye, to.Position + eye);
    }

    /// <summary>Launch a bolt from <paramref name="from"/> toward <paramref name="target"/> (both feet-space).</summary>
    private void SpawnProjectile(Vector3 from, Vector3 target, in EnemyArchetype arch)
    {
        var dir = target - from;
        dir.Y = 0f; // fly level across the ground plane
        if (dir.LengthSquared() < 0.0001f)
            return;
        dir = Vector3.Normalize(dir);

        var proj = new ProjectileState(_nextProjectileId++)
        {
            Position = from + new Vector3(0f, SimConstants.EyeHeight, 0f),
            Velocity = dir * arch.ProjectileSpeed,
            Damage = arch.AttackDamage,
            Life = SimConstants.ProjectileLifetime,
        };
        _projectiles[proj.Id] = proj;
    }

    private void UpdateProjectiles(float dt)
    {
        if (_projectiles.Count == 0)
            return;

        var hitRadius = SimConstants.CharacterRadius + SimConstants.ProjectileRadius;
        var hitRadiusSq = hitRadius * hitRadius;
        List<int>? dead = null;

        foreach (var proj in _projectiles.Values)
        {
            var next = proj.Position + proj.Velocity * dt;

            // A wall in the flight path this tick stops the bolt (no damage).
            if (Geometry is not null && !Geometry.HasLineOfSight(proj.Position, next))
            {
                (dead ??= new List<int>()).Add(proj.Id);
                continue;
            }

            proj.Position = next;
            proj.Life -= dt;

            // First alive player the bolt overlaps takes the hit.
            var struck = false;
            foreach (var player in _players.Values)
            {
                if (!player.IsAlive)
                    continue;

                var dx = player.Position.X - proj.Position.X;
                var dz = player.Position.Z - proj.Position.Z;
                var withinColumn = proj.Position.Y >= player.Position.Y &&
                                   proj.Position.Y <= player.Position.Y + SimConstants.CharacterHeight;
                if (withinColumn && dx * dx + dz * dz <= hitRadiusSq)
                {
                    player.TakeDamage(Math.Max(1f, proj.Damage - player.DamageReduction));
                    struck = true;
                    break;
                }
            }

            if (struck || proj.Life <= 0f)
                (dead ??= new List<int>()).Add(proj.Id);
        }

        if (dead is not null)
            foreach (var id in dead)
                _projectiles.Remove(id);
    }

    private void ResolveEnemyDeaths()
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
            var arch = EnemyArchetypes.Of(enemy.Type);

            if (arch.GuaranteedDrops > 0)
            {
                // A boss always pays out equipment — scatter the drops so they read as a pile.
                for (var i = 0; i < arch.GuaranteedDrops; i++)
                {
                    var offset = new Vector3(MathF.Cos(i * 2.1f), 0f, MathF.Sin(i * 2.1f)) * 26f;
                    DropItem(LootGenerator.RollDrop(_rng), enemy.Position + offset);
                }
            }
            else if (LootGenerator.TryRollDrop(_rng) is { } drop)
            {
                // Common enemies only rarely part with equipment (EquipmentDropChance).
                DropItem(drop, enemy.Position);
            }

            // The everyday drops roll for every enemy, boss included. Fixed offsets
            // keep them from stacking exactly on an equipment drop.
            if (_rng.NextDouble() < SimConstants.GoldDropChance)
                DropGold(_rng.Next(SimConstants.GoldDropMin, SimConstants.GoldDropMax + 1),
                         enemy.Position + new Vector3(14f, 0f, -10f));
            if (_rng.NextDouble() < SimConstants.PotionDropChance)
                DropPotion(enemy.Position + new Vector3(-14f, 0f, 10f));

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

            switch (ground.Kind)
            {
                case LootKind.Gold:
                    picker.Gold += ground.Amount;
                    _pickups.Add(new LootPickup(picker.Id, LootKind.Gold, null, ground.Amount));
                    break;

                case LootKind.HealthPotion:
                    // A potion is wasted on the unhurt: it stays on the ground
                    // until someone who needs it walks over.
                    if (picker.Health >= picker.MaxHealth)
                        continue;
                    var healed = picker.Heal(SimConstants.PotionHealAmount);
                    _pickups.Add(new LootPickup(picker.Id, LootKind.HealthPotion, null, (int)MathF.Round(healed)));
                    break;

                default:
                    picker.Inventory.Add(ground.Item!);
                    _pickups.Add(new LootPickup(picker.Id, LootKind.Equipment, ground.Item, 0));
                    break;
            }
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

    private Vector3 ResolveMove(Vector3 pos, Vector3 delta, float radius = SimConstants.CharacterRadius) =>
        Geometry?.Move(pos, delta, radius) ?? ClampToArena(pos + delta);

    private static Vector3 ClampToArena(Vector3 p) => new(
        Math.Clamp(p.X, -SimConstants.WorldHalfWidth, SimConstants.WorldHalfWidth),
        0f,
        Math.Clamp(p.Z, -SimConstants.WorldHalfHeight, SimConstants.WorldHalfHeight));
}

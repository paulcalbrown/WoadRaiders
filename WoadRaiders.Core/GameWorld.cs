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
    private readonly List<PortalExit> _portalExits = new();
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

    /// <summary>Where the exit portal stands, once the boss has fallen (null before).</summary>
    public Vector3? Portal { get; private set; }

    /// <summary>Every enemy slain in this world so far — the warband's shared tally.</summary>
    public int EnemiesSlain { get; private set; }

    /// <summary>Open the exit portal. The first opening sticks; later calls (a
    /// respawned boss falling again) leave the standing portal alone.</summary>
    public void OpenPortal(Vector3 position) => Portal ??= position;

    // --- players ---

    public PlayerState AddPlayer(int id, string name, CharacterClass cls = CharacterClass.Knight)
    {
        var player = new PlayerState(id, name, cls) { JoinTick = Tick };
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

    /// <summary>
    /// Returns the portal exits that happened since the last call, then clears the
    /// buffer. Each exited player is already out of the world (removed the tick
    /// they stepped through); the host drains this to send their run summaries.
    /// </summary>
    public IReadOnlyList<PortalExit> ConsumePortalExits()
    {
        if (_portalExits.Count == 0)
            return Array.Empty<PortalExit>();

        var drained = _portalExits.ToArray();
        _portalExits.Clear();
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
        ResolvePortalExits();    // after pickups, so loot grabbed at the threshold counts
        RespawnDeadPlayers();

        Tick++;
    }

    /// <summary>
    /// Walk any player standing in the open portal out of the dungeon: record
    /// their haul as a <see cref="PortalExit"/> and remove them from the world in
    /// the same tick, so they never appear in another snapshot (and can't exit
    /// twice under server catch-up stepping).
    /// </summary>
    private void ResolvePortalExits()
    {
        if (Portal is not { } portal)
            return;

        List<PlayerState>? exiting = null;
        var radiusSq = SimConstants.PortalRadius * SimConstants.PortalRadius;
        foreach (var player in _players.Values)
            if (player.IsAlive && Vector3.DistanceSquared(player.Position, portal) <= radiusSq)
                (exiting ??= new List<PlayerState>()).Add(player);

        if (exiting is null)
            return;

        foreach (var player in exiting)
        {
            _portalExits.Add(new PortalExit(
                player.Id, player.Name, player.Gold, player.Inventory.Count, Tick - player.JoinTick));
            RemovePlayer(player.Id);
        }
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

            player.Velocity = move * player.Archetype.MoveSpeed;
            player.Position = ResolveMove(player.Position, player.Velocity * dt);
        }
    }

    private void ResolvePlayerAttacks()
    {
        foreach (var player in _players.Values)
        {
            if (!player.IsAlive)
                continue;

            var input = _inputs.TryGetValue(player.Id, out var i) ? i : default;
            if (!input.Attack || !player.AttackReady)
                continue;

            var arch = player.Archetype;
            player.AttackCooldown = arch.AttackCooldown;
            player.AttackAnimRemaining = SimConstants.AttackAnimDuration;

            // Aim the strike where the player pointed (cursor), not where they last
            // moved. A zero aim (no cursor sent) leaves the movement facing intact.
            var aim = new Vector3(input.AimX, 0f, input.AimZ);
            if (aim.LengthSquared() > 0.0001f)
                player.Facing = Vector3.Normalize(aim);

            // Ranged classes loose a bolt along their facing; damage lands on impact
            // (UpdateProjectiles), so a shot is dodgeable like the enemy mages' are.
            if (arch.ProjectileSpeed > 0f)
            {
                // Player shots are cursor-aimed on the ground plane, so they fly
                // level and hug the terrain in flight (see UpdateProjectiles).
                SpawnProjectile(player.Position, player.Facing, arch.ProjectileSpeed, player.AttackDamage,
                    player.Class == CharacterClass.Mage ? ProjectileKind.MagicBolt : ProjectileKind.Arrow,
                    followTerrain: true);
                continue;
            }

            // Frontal cleave: every enemy within reach AND in front of you (the damage
            // arc) takes the hit — a swing through the arc, not a single target, but
            // still not a 360° sweep.
            var rangeSq = arch.AttackRange * arch.AttackRange;
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
                        // Aimed eye-to-eye in FULL 3D, so a mage on an overlook rains
                        // bolts down the slope instead of zapping level over heads.
                        var eye = new Vector3(0f, SimConstants.EyeHeight, 0f);
                        SpawnProjectile(enemy.Position, (target.Position + eye) - (enemy.Position + eye),
                            arch.ProjectileSpeed, arch.AttackDamage, ProjectileKind.EnemyBolt, followTerrain: false);
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

    /// <summary>Launch a bolt from <paramref name="from"/> (feet-space) along <paramref name="direction"/>.</summary>
    private void SpawnProjectile(Vector3 from, Vector3 direction, float speed, float damage, ProjectileKind kind,
                                 bool followTerrain)
    {
        if (followTerrain)
            direction.Y = 0f; // cursor-aimed: fly level and let the terrain-follow ride the slopes
        if (direction.LengthSquared() < 0.0001f)
            return;
        direction = Vector3.Normalize(direction);

        var proj = new ProjectileState(_nextProjectileId++)
        {
            Position = from + new Vector3(0f, SimConstants.EyeHeight, 0f),
            Velocity = direction * speed,
            Damage = damage,
            Life = SimConstants.ProjectileLifetime,
            Kind = kind,
            FollowsTerrain = followTerrain,
        };
        _projectiles[proj.Id] = proj;
    }

    private void UpdateProjectiles(float dt)
    {
        if (_projectiles.Count == 0)
            return;

        List<int>? dead = null;

        foreach (var proj in _projectiles.Values)
        {
            var next = proj.Position + proj.Velocity * dt;

            // Terrain-following (player) bolts hug the ground: on a gradual slope
            // the flight height tracks ground + eye height exactly; over a sharp
            // drop (a gorge, a ledge) they hold level and sail across, Gauntlet-
            // style; a sharp rise is a wall and stops the bolt.
            if (proj.FollowsTerrain && Geometry is not null)
            {
                var followY = Geometry.GroundHeight(next.X, next.Z) + SimConstants.EyeHeight;
                var dy = followY - next.Y;
                if (dy > SimConstants.StepHeight)
                {
                    (dead ??= new List<int>()).Add(proj.Id); // the ground leapt up — a cliff face
                    continue;
                }
                if (dy >= -SimConstants.StepHeight)
                    next.Y = followY; // walkable grade — follow it
            }

            // A wall in the flight path this tick stops the bolt (no damage).
            if (Geometry is not null && !Geometry.HasLineOfSight(proj.Position, next))
            {
                (dead ??= new List<int>()).Add(proj.Id);
                continue;
            }

            proj.Position = next;
            proj.Life -= dt;

            // First alive combatant on the opposing side the bolt overlaps takes the
            // hit — an enemy bolt flies through enemies, a player's through players.
            var struck = proj.HostileToPlayers ? StrikePlayer(proj) : StrikeEnemy(proj);
            if (struck || proj.Life <= 0f)
                (dead ??= new List<int>()).Add(proj.Id);
        }

        if (dead is not null)
            foreach (var id in dead)
                _projectiles.Remove(id);
    }

    private bool StrikePlayer(ProjectileState proj)
    {
        foreach (var player in _players.Values)
        {
            if (!player.IsAlive)
                continue;
            if (!ProjectileOverlaps(proj, player.Position, SimConstants.CharacterRadius))
                continue;
            // Armor soaks part of the hit, but every hit lands for at least 1.
            player.TakeDamage(Math.Max(1f, proj.Damage - player.DamageReduction));
            return true;
        }
        return false;
    }

    private bool StrikeEnemy(ProjectileState proj)
    {
        foreach (var enemy in _enemies.Values)
        {
            if (!enemy.IsAlive)
                continue;
            // Per-archetype radius: the boss is a wider target, as he is on foot.
            if (!ProjectileOverlaps(proj, enemy.Position, EnemyArchetypes.Of(enemy.Type).Radius))
                continue;
            enemy.TakeDamage(proj.Damage);
            return true;
        }
        return false;
    }

    /// <summary>Cylinder test: within the body column's height and combined radius on XZ.</summary>
    private static bool ProjectileOverlaps(ProjectileState proj, Vector3 feet, float bodyRadius)
    {
        var withinColumn = proj.Position.Y >= feet.Y &&
                           proj.Position.Y <= feet.Y + SimConstants.CharacterHeight;
        if (!withinColumn)
            return false;
        var dx = feet.X - proj.Position.X;
        var dz = feet.Z - proj.Position.Z;
        var hitRadius = bodyRadius + SimConstants.ProjectileRadius;
        return dx * dx + dz * dz <= hitRadius * hitRadius;
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
            EnemiesSlain++;
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

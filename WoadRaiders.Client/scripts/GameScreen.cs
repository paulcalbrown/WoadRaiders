using Godot;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;
using SysVec3 = System.Numerics.Vector3; // Core simulates in 3D (Y-up), same convention as Godot.

namespace WoadRaiders.Client;

/// <summary>
/// The in-match screen and the client's composition root. Entered from the
/// <see cref="TitleScreen"/> (Esc returns there); this node only wires the
/// pieces together and orchestrates the frame:
///
///   <see cref="ClientConnection"/> (transport + lifecycle) feeds
///   <see cref="ClientState"/> (inventory/equipment/health replica) and
///   <see cref="LocalPlayer"/> (prediction + reconciliation), which drive
///   <see cref="WorldView"/> (entity views), <see cref="HudController"/>,
///   <see cref="CameraRig"/>, and <see cref="OcclusionFader"/>;
///   <see cref="DungeonVisualBuilder"/> stands the map up under a dedicated
///   root node when geometry arrives (and again if a reconnect lands on a
///   different map).
///
/// The simulation is fully 3D (System.Numerics, Y-up) — the same convention as
/// Godot, so sim positions map to the scene 1:1.
///
/// Arrows move · Space attacks · walk over loot · I = inventory · 1-9 = equip · Esc = menu.
/// </summary>
public partial class GameScreen : Node3D
{
    public const string ScenePath = "res://screens/GameScreen.tscn";

    private const float BodyHeight = 22f; // approx body-centre height, for camera/fade reference

    private readonly ClientState _state = new();
    private readonly OcclusionFader _fader = new();
    private ClientConnection _connection = null!;
    private LocalPlayer _localPlayer = null!;
    private WorldView _worldView = null!;
    private HudController _hud = null!;
    private CameraRig _camera = null!;
    private DungeonGeometry? _geometry;
    private IDungeonGeometry? _movement; // the baked navmesh (or flat rules) everything MOVES on
    private Node3D? _mapRoot;            // all dungeon visuals live under here, so a map swap can rebuild them
    private int? _builtMapFingerprint;   // fingerprint of the map the visuals were built for
    private bool _portalAnnounced;       // the "way opens" banner fires once per session

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();
        ClientActions.EnsureRegistered();

        _worldView = new WorldView(this);
        _camera = new CameraRig();
        AddChild(_camera);
        _hud = new HudController();
        AddChild(_hud);

        _connection = new ClientConnection(ClientConfig.Host, ClientConfig.Port);
        _state.Class = ClientConfig.PlayerClass;
        _hud.SetPlayerClass(ClientConfig.PlayerClass);

        // Dev helper behind --play --screenshot: let the match come up and render,
        // save two stills of the live dungeon, and exit.
        if (ClientConfig.ScreenshotPath is { } path)
        {
            GetTree().CreateTimer(4.0).Timeout += () =>
            {
                SaveStill(path);
                GetTree().CreateTimer(2.0).Timeout += () =>
                {
                    SaveStill(System.IO.Path.ChangeExtension(path, null) + "-2.png");
                    GetTree().Quit();
                };
            };
        }
        _localPlayer = new LocalPlayer(_connection, _camera);
        _localPlayer.MoveClicked += point => MoveMarker.Spawn(this, point); // red X where the player clicks to move

        _connection.Connected += SendJoin;
        _connection.GeometryReceived += OnGeometry;
        _connection.Welcomed += OnWelcome;
        _connection.SnapshotReceived += OnSnapshot;
        _connection.ItemPickedUp += OnItemPickedUp;
        _connection.EquipmentUpdated += OnEquipmentUpdate;
        _connection.JoinDenied += OnJoinDenied;
        _connection.RunCompleted += OnRunComplete;
        _connection.Start();
    }

    public override void _Process(double delta)
    {
        // Frame order is load-bearing: Poll fires the reconcile/welcome handlers
        // before Advance ticks prediction, and the views must move before the
        // fader and camera read the render position.
        _connection.Poll(delta);

        // A handler fired during Poll (run complete, join denied) can swap the
        // scene out — Godot pulls this node from the tree immediately and frees
        // it at frame's end. The rest of the frame reads the tree (LookAt needs
        // a global transform), so stop here on the way out.
        if (!IsInsideTree())
            return;

        // Freeze prediction while the link is down: the world is frozen too, and
        // walking a ghost through it would only earn a big snap on rejoin.
        if (_connection.State == ConnectionState.Playing)
            _localPlayer.Advance(delta);
        _localPlayer.UpdateRenderPosition(delta);

        _worldView.Update(delta, _localPlayer.PlayerId, _localPlayer.RenderPosition, _localPlayer.Swinging,
                          _localPlayer.AttackFacing);

        if (_localPlayer.Active)
        {
            var bodyCentre = _localPlayer.RenderPosition + Vector3.Up * BodyHeight;
            _camera.Follow(bodyCentre, delta);
            // The chase camera swings around the raider, so the fade direction is
            // live — this frame's body-to-camera sight line.
            _fader.Update(bodyCentre, (_camera.GlobalPosition - bodyCentre).Normalized());
        }
        else
        {
            _camera.Follow(Vector3.Zero, delta);
        }

        _hud.Refresh(_state, _connection.State, delta, _connection.RefusalMessage);
    }

    public override void _Input(InputEvent @event)
    {
        if (@event.IsActionPressed("ui_cancel")) // Esc
        {
            if (_hud.InventoryOpen)
                _hud.ToggleInventory(); // first Esc closes the panel, second leaves
            else
                GetTree().ChangeSceneToFile(TitleScreen.ScenePath); // _ExitTree closes the connection
            return;
        }

        if (@event.IsActionPressed(ClientActions.InventoryToggle))
        {
            _hud.ToggleInventory();
            return;
        }

        if (!_hud.InventoryOpen)
            return;
        for (var i = 0; i < ClientActions.EquipSlots.Length; i++)
        {
            if (!@event.IsActionPressed(ClientActions.EquipSlots[i]))
                continue;
            if (i < _state.Inventory.Count)
                _connection.Send(MessageType.EquipRequest,
                    new EquipRequestPacket { ItemId = _state.Inventory[i].Id },
                    DeliveryMethod.ReliableOrdered);
            return;
        }
    }

    public override void _ExitTree() => _connection.Stop();

    /// <summary>Fires on every connect (including retries): forge the configured
    /// dungeon or enter the chosen instance. After the first Welcome the config is
    /// pinned to Join, so a reconnect lands back in the same run.</summary>
    private void SendJoin() => _connection.SendJoin(new JoinRequest
    {
        Name = ClientConfig.PlayerName,
        Class = (byte)ClientConfig.PlayerClass,
        Mode = (byte)ClientConfig.Mode,
        Dungeon = (byte)ClientConfig.Dungeon,
        InstanceName = ClientConfig.InstanceName,
        InstanceId = ClientConfig.InstanceId,
    });

    /// <summary>The instance we wanted is gone or full (or the server is) — back to
    /// the raid browser with the reason, so the player picks again.</summary>
    private void OnJoinDenied(JoinDeniedPacket denial)
    {
        RaidSelectScreen.Notice = (JoinDenyReason)denial.Reason switch
        {
            JoinDenyReason.InstanceFull => "That warband is full.",
            JoinDenyReason.ServerFull => "The server can host no more raids.",
            _ => "That raid has ended.",
        };
        // A rejected rejoin must not try the dead instance forever; browsing anew
        // starts from a clean forge-or-join choice.
        ClientConfig.Mode = JoinMode.Create;
        GD.Print($"Join denied ({(JoinDenyReason)denial.Reason}) — returning to the raid browser.");
        GetTree().ChangeSceneToFile(RaidSelectScreen.ScenePath);
    }

    private void OnGeometry(DungeonGeometryPacket packet)
    {
        _geometry = DungeonSnapshot.ToGeometry(packet);       // the realm's DATA: scene identity, visuals
        _movement = DungeonSnapshot.ToMovementGeometry(packet); // what prediction and cursor rays move on
        _camera.Geometry = _movement; // the boom keeps clear of this terrain

        // A reconnect to the same match re-sends the same map — leave the standing
        // visuals alone. But a client that outlives a server swap can land on a
        // DIFFERENT map (server restarted with a new arena); rendering the old
        // walls over the new collision geometry would mean invisible walls, so
        // tear the map root down and rebuild.
        var fingerprint = DungeonSnapshot.Fingerprint(packet);
        if (_builtMapFingerprint == fingerprint)
            return;
        if (_builtMapFingerprint is not null)
            GD.Print("Server map changed — rebuilding the dungeon visuals.");
        _builtMapFingerprint = fingerprint;

        _fader.Clear();          // before the teardown, so no frame fades freed meshes
        _mapRoot?.QueueFree();
        _mapRoot = new Node3D { Name = "Map" };
        AddChild(_mapRoot);
        if (!DungeonVisualBuilder.Build(_mapRoot, _geometry, _fader))
        {
            // The server is hosting a realm whose scene this build doesn't ship.
            // There is no honest way to draw it, and playing on collision we
            // cannot see would be worse than stopping — so refuse and say where
            // a build that CAN draw it lives.
            var realm = System.IO.Path.GetFileNameWithoutExtension(_geometry.ScenePath ?? "");
            GD.PrintErr($"No scene in this build for '{_geometry.ScenePath}' — refusing the raid.");
            _connection.RefuseLocally(
                $"This build has no map for {(realm.Length > 0 ? realm : "that realm")}. " +
                $"Get the latest at {NetConfig.DownloadUrl}");
            _mapRoot.QueueFree();
            _mapRoot = null;
            _builtMapFingerprint = null; // a later, playable map must still build
            return;
        }
        StartMapMusic(_geometry.ScenePath);

        // Announce the arrival — and if we asked to FORGE one dungeon but the
        // server served another (it doesn't host it?), say so loudly. A join by
        // instance id takes whatever dungeon that instance runs, so no check.
        var info = DungeonCatalog.ForScene(_geometry.ScenePath ?? "");
        _hud.AnnounceLocation(info?.Name
            ?? System.IO.Path.GetFileNameWithoutExtension(_geometry.ScenePath ?? "the uncharted depths"));
        if (ClientConfig.Mode == JoinMode.Create && info is { } served && served.Id != ClientConfig.Dungeon)
            GD.PrintErr($"Asked to raid {ClientConfig.Dungeon} but the server served {served.Id} — " +
                        "is that dungeon hosted? (Check the server's startup log.)");
    }

    /// <summary>Loop the map's theme. Catalog dungeons name their track (several can
    /// share one); anything else falls back to its scene name, so a custom map drops
    /// in a matching assets/audio/&lt;scene&gt;_theme.wav with no code change. Runs only
    /// on a genuine map (re)build, so a same-map reconnect doesn't restart it; no
    /// matching track just plays nothing.</summary>
    private void StartMapMusic(string? scenePath)
    {
        if (string.IsNullOrEmpty(scenePath))
        {
            MusicJukebox.Instance.Silence();
            return;
        }
        var key = DungeonCatalog.ForScene(scenePath) is { } info
            ? info.MusicKey
            : System.IO.Path.GetFileNameWithoutExtension(scenePath).ToLowerInvariant();
        var track = $"res://assets/audio/{key}_theme.wav";
        if (MusicPlayer.Exists(track))
            MusicJukebox.Instance.Play(track, -10f); // takes over from the title theme carried in from the menus
        else
            MusicJukebox.Instance.Silence();
    }

    private void SaveStill(string path) => GetViewport().GetTexture().GetImage().SavePng(path);

    private void OnWelcome(WelcomePacket welcome)
    {
        // Pin the run: whatever we asked for, we are IN instance InstanceId now.
        // A mid-run reconnect then rejoins this same instance instead of forging
        // a fresh one (and being alone in an identical-looking dungeon).
        ClientConfig.Mode = JoinMode.Join;
        ClientConfig.InstanceId = welcome.InstanceId;

        // A reconnect is a brand-new join server-side — fresh player id, empty
        // inventory, full health, fresh input buffer. Mirror that exactly.
        // (The portal banner may fire again: a rejoined world is news again.)
        _portalAnnounced = false;
        _state.Reset();
        _localPlayer.BeginSession(welcome.PlayerId, _geometry?.SpawnPoint ?? SysVec3.Zero, _movement,
                                  ClientConfig.PlayerClass);
        GD.Print($"Joined instance #{welcome.InstanceId} as player {welcome.PlayerId} ({ClientConfig.PlayerClass})");
    }

    /// <summary>The boss has fallen and the way out stands open — the server pulled us
    /// through the portal. Hand the report to the summary screen and leave the match.</summary>
    private void OnRunComplete(RunCompletePacket run)
    {
        RunSummaryScreen.Summary = run;
        GD.Print($"Run complete — {run.Gold} gold, {run.ItemsLooted} relics, " +
                 $"{run.FoesSlain} foes felled in {run.DurationSeconds}s.");
        GetTree().ChangeSceneToFile(RunSummaryScreen.ScenePath); // _ExitTree closes the connection
    }

    private void OnSnapshot(WorldSnapshotPacket snapshot)
    {
        // Announce the portal the first time it appears — the whole warband sees
        // the way open the moment the boss falls, wherever they stand.
        if (snapshot.PortalOpen && !_portalAnnounced)
        {
            _portalAnnounced = true;
            _hud.AnnounceLocation("The Way Opens");
        }

        foreach (var p in snapshot.Players)
        {
            if (p.Id != _localPlayer.PlayerId)
                continue;
            if (_state.SetHealth(p.Health)) // authoritative, never predicted
                _hud.OnDamage();
            _localPlayer.Reconcile(p);
            break;
        }

        _worldView.Apply(snapshot, _localPlayer.PlayerId);
    }

    private void OnItemPickedUp(ItemPickedUpPacket loot)
    {
        switch ((LootKind)loot.Kind)
        {
            case LootKind.Gold:
                _state.AddGold(loot.Amount);
                GD.Print($"Picked up {loot.Amount} gold ({_state.Gold} total)");
                break;

            case LootKind.HealthPotion:
                // The heal itself arrives authoritatively in the next snapshot.
                GD.Print($"Drank a health potion (+{loot.Amount} health)");
                break;

            default:
                _state.AddItem(new Item(loot.ItemId, loot.Name, (ItemRarity)loot.Rarity, (ItemType)loot.Type, loot.Power));
                GD.Print($"Looted {loot.Name} (Power {loot.Power})");
                break;
        }
    }

    private void OnEquipmentUpdate(EquipmentUpdatePacket packet) =>
        _state.SetEquipment(packet.WeaponItemId, packet.ArmorItemId, packet.TrinketItemId);
}

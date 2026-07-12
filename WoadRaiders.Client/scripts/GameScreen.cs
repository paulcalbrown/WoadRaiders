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
    private Node3D? _mapRoot;            // all dungeon visuals live under here, so a map swap can rebuild them
    private int? _builtMapFingerprint;   // fingerprint of the map the visuals were built for

    public override void _Ready()
    {
        ClientConfig.EnsureLoaded();
        ClientActions.EnsureRegistered();

        _worldView = new WorldView(this);
        _camera = new CameraRig();
        AddChild(_camera);
        _hud = new HudController();
        AddChild(_hud);

        _connection = new ClientConnection(ClientConfig.Host, ClientConfig.Port, ClientConfig.PlayerName);
        _localPlayer = new LocalPlayer(_connection);

        _connection.GeometryReceived += OnGeometry;
        _connection.Welcomed += OnWelcome;
        _connection.SnapshotReceived += OnSnapshot;
        _connection.ItemPickedUp += OnItemPickedUp;
        _connection.EquipmentUpdated += OnEquipmentUpdate;
        _connection.Start();
    }

    public override void _Process(double delta)
    {
        // Frame order is load-bearing: Poll fires the reconcile/welcome handlers
        // before Advance ticks prediction, and the views must move before the
        // fader and camera read the render position.
        _connection.Poll(delta);

        // Freeze prediction while the link is down: the world is frozen too, and
        // walking a ghost through it would only earn a big snap on rejoin.
        if (_connection.State == ConnectionState.Playing)
            _localPlayer.Advance(delta);
        _localPlayer.UpdateRenderPosition(delta);

        _worldView.Update(delta, _localPlayer.PlayerId, _localPlayer.RenderPosition, _localPlayer.Swinging);

        if (_localPlayer.Active)
        {
            var bodyCentre = _localPlayer.RenderPosition + Vector3.Up * BodyHeight;
            _fader.Update(bodyCentre);
            _camera.Follow(bodyCentre, delta);
        }
        else
        {
            _camera.Follow(Vector3.Zero, delta);
        }

        _hud.Refresh(_state, _connection.State, delta);
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

    private void OnGeometry(DungeonGeometryPacket packet)
    {
        _geometry = DungeonSnapshot.ToGeometry(packet);

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
        DungeonVisualBuilder.Build(_mapRoot, _geometry, _fader);
    }

    private void OnWelcome(WelcomePacket welcome)
    {
        // A reconnect is a brand-new join server-side — fresh player id, empty
        // inventory, full health, fresh input buffer. Mirror that exactly.
        _state.Reset();
        _localPlayer.BeginSession(welcome.PlayerId, _geometry?.SpawnPoint ?? SysVec3.Zero, _geometry);
        GD.Print($"Joined as player {welcome.PlayerId}");
    }

    private void OnSnapshot(WorldSnapshotPacket snapshot)
    {
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
        _state.AddItem(new Item(loot.ItemId, loot.Name, (ItemRarity)loot.Rarity, (ItemType)loot.Type, loot.Power));
        GD.Print($"Looted {loot.Name} (Power {loot.Power})");
    }

    private void OnEquipmentUpdate(EquipmentUpdatePacket packet) =>
        _state.SetEquipment(packet.WeaponItemId, packet.ArmorItemId, packet.TrinketItemId);
}

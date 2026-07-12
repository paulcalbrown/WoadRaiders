using Godot;
using LiteNetLib;
using WoadRaiders.Core;
using WoadRaiders.Shared;
using SysVec3 = System.Numerics.Vector3; // Core simulates in 3D (Y-up), same convention as Godot.

namespace WoadRaiders.Client;

/// <summary>
/// Composition root for the 3D isometric client. This node only wires the pieces
/// together and orchestrates the frame:
///
///   <see cref="ClientConnection"/> (transport + lifecycle) feeds
///   <see cref="ClientState"/> (inventory/equipment/health replica) and
///   <see cref="LocalPlayer"/> (prediction + reconciliation), which drive
///   <see cref="WorldView"/> (entity views), <see cref="HudController"/>,
///   <see cref="CameraRig"/>, and <see cref="OcclusionFader"/>;
///   <see cref="DungeonVisualBuilder"/> stands the map up when geometry arrives.
///
/// The simulation is fully 3D (System.Numerics, Y-up) — the same convention as
/// Godot, so sim positions map to the scene 1:1.
///
/// Arrows move · Space attacks · walk over loot · I = inventory · 1-9 = equip.
/// </summary>
public partial class NetworkClient : Node3D
{
    private const float BodyHeight = 22f; // approx body-centre height, for camera/fade reference

    private readonly ClientState _state = new();
    private readonly OcclusionFader _fader = new();
    private ClientConnection _connection = null!;
    private LocalPlayer _localPlayer = null!;
    private WorldView _worldView = null!;
    private HudController _hud = null!;
    private CameraRig _camera = null!;
    private DungeonGeometry? _geometry;
    private bool _dungeonBuilt;

    public override void _Ready()
    {
        ClientActions.EnsureRegistered();

        _worldView = new WorldView(this);
        _camera = new CameraRig();
        AddChild(_camera);
        _hud = new HudController();
        AddChild(_hud);

        var (host, port) = ParseServerArg();
        _connection = new ClientConnection(host, port, "Woad Raider");
        _localPlayer = new LocalPlayer(_connection);

        _connection.GeometryReceived += OnGeometry;
        _connection.Welcomed += OnWelcome;
        _connection.SnapshotReceived += OnSnapshot;
        _connection.ItemPickedUp += OnItemPickedUp;
        _connection.EquipmentUpdated += p => _state.SetEquipment(p.WeaponItemId, p.ArmorItemId, p.TrinketItemId);
        _connection.Start();
    }

    public override void _Process(double delta)
    {
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

        // A reconnect (same server, same match) re-sends the map; the visuals are
        // already standing, so only the collision geometry above is refreshed.
        if (_dungeonBuilt)
            return;
        _dungeonBuilt = true;
        DungeonVisualBuilder.Build(this, _geometry, _fader);
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

    /// <summary>
    /// Server address from <c>--server=host[:port]</c> in the user args (after
    /// <c>--</c> on the Godot command line); defaults to loopback. Matchmaking
    /// (PlayFab) will hand out real addresses later — this is the dev override.
    /// </summary>
    private static (string Host, int Port) ParseServerArg()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--server="))
                continue;
            var value = arg["--server=".Length..];
            var colon = value.LastIndexOf(':');
            if (colon < 0)
                return (value, NetConfig.DefaultPort);
            if (int.TryParse(value[(colon + 1)..], out var port))
                return (value[..colon], port);
            GD.PrintErr($"Bad --server value '{value}' — using the default port.");
            return (value[..colon], NetConfig.DefaultPort);
        }
        return ("127.0.0.1", NetConfig.DefaultPort);
    }
}

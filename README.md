# WoadRaiders

A public co-op, **server-authoritative** ARPG dungeon crawler (Celtic/Pictish woad-raider
theme), rendered in **3D isometric**. Real-time action, loot, hand-crafted dungeons — built so that
online multiplayer with matchmaking is a first-class concern from day one, not a bolt-on.

This repository is the **architecture scaffold**: a working client ⇄ dedicated-server skeleton
you can grow the actual game on top of.

---

## Architecture

The golden rule of this codebase: **the simulation is engine-free and the server owns the
truth.** Clients may only ever send *input*; the server decides positions, damage, and loot.

```
WoadRaiders.Core      Pure C# simulation — combat, movement, loot, dungeon geometry.
                      No engine, no networking. Deterministic. Unit-tested.
        ▲   ▲
        │   │
WoadRaiders.Shared    The wire protocol: message types + (de)serialization
        ▲   ▲         (LiteNetLib). References Core.
        │   │
        │   └────────────────────────┐
        │                            │
WoadRaiders.Server            WoadRaiders.Client
Headless dedicated server.    Godot 4 (.NET) 3D-isometric client.
Plain console app, no         Predicts movement, renders meshes,
engine → containerizes        sends input. Open in Godot to run.
cheaply, allocated per
match by hosting later.
```

`WoadRaiders.Core.Tests` covers the simulation (movement, clamping, determinism).

### Target frameworks
| Project | TFM | Why |
|---|---|---|
| `Server`, `Core.Tests` | `net10.0` | Pure .NET; run on the latest runtime. |
| `Core`, `Shared` | `net8.0;net10.0` | Multi-targeted so the net10 server *and* the net8 Godot client can both consume them. |
| `Client` (Godot) | `net8.0` | Godot 4.x embeds the **.NET 8** runtime and can't load a net10 assembly. |

When your Godot build runs on .NET 10, flip the client to `net10.0` and you can drop the `net8.0`
target from `Core`/`Shared`.

### Networking model
- Transport: **LiteNetLib 2.x** (reliable UDP). All gameplay traffic on channel 0.
- `Welcome` / `JoinRequest`: `ReliableOrdered` (must arrive).
- `Input` / `WorldSnapshot`: `Sequenced` (unreliable, but never delivers a stale packet after a
  newer one — ideal for high-frequency state).
- Server steps the simulation at **30 Hz** and broadcasts snapshots at **20 Hz**.

---

## Running it

### 1. The dedicated server (works today, no engine needed)
```bash
dotnet run --project WoadRaiders.Server
# → WoadRaiders dedicated server listening on udp/9050 (sim 30Hz, snapshots 20Hz)
```
Optional custom port: `dotnet run --project WoadRaiders.Server -- 9060`

### 2. The Godot client
Requires the **.NET/Mono build** of Godot (installed here as **Godot 4.7** via Scoop — the CLI
command is `godot-mono`). The client csproj is pinned to `Godot.NET.Sdk/4.7.0` to match; keep the
two in lockstep if you upgrade Godot.
1. Open the `WoadRaiders.Client` folder in the Godot editor once (it generates the `.godot/`
   import cache), then press **Play**. From the CLI: `godot-mono --path WoadRaiders.Client`.
2. An orthographic isometric camera eases after you (with directional shadows). **Arrows** move,
   **Space** attacks (cleave), **I** opens the inventory (**1-9** to equip). You're the blue
   capsule; other players are green; enemies are orange with billboard health bars; loot spins,
   bobs, and glows by rarity. Walls between you and the camera fade out so you're never hidden.

### 3. A two-player local test
Start the server, then launch **two** client instances (Godot editor "Play" + an exported build,
or two editor windows). Both characters are driven by the same authoritative server.

### 4. Run the tests
```bash
dotnet test WoadRaiders.Core.Tests
```

---

## Roadmap

Build the *fun* locally first (client → `localhost` server); light up the online backend once the
core loop earns it.

- [x] **Client-side prediction + reconciliation** — implemented in `Core.ClientPrediction`
      (engine-free, unit-tested). The local player applies input instantly and reconciles against
      each snapshot; drift stays ~1–2 ticks. Remote players ease toward their latest snapshot.
- [ ] **Remote interpolation** — buffer timestamped snapshots and render remote players ~100 ms in
      the past for smoothness (currently a simple ease-toward-latest). Optional refinement: a
      server-side input queue to erase the last tick of prediction drift.
- [x] **Combat (first pass)** — server-authoritative melee cleave, enemies with seek/attack AI,
      damage, enemy death + respawn, player respawn. Logic in `Core`, unit-tested. Client sends only
      the attack intent; the client predicts movement, never damage.
- [ ] **Combat polish** — directional/aimed attacks, on-screen health bars, downed state + revive,
      more enemy types, attack telegraphs. (Currently: radius cleave, immediate respawn at origin.)
- [x] **3D simulation** — `Core` simulates in full 3D world space (`System.Numerics.Vector3`,
      **Y-up**, matching Godot and glTF conventions, so the client maps sim positions 1:1). Dungeon
      shape sits behind the `IDungeonGeometry` seam. Movement input stays 2D ground-plane intent
      (`MoveX`/`MoveZ`); the geometry decides height.
- [x] **Fully 3D dungeon geometry (`DungeonGeometry`)** — dungeons are sets of **world-space solid
      boxes** + spawn markers, exactly what a Godot-editor scene reduces to. Collision is a vertical
      **cylinder-vs-box** test that is 3D-aware (walls block; beams above head height don't), sliding
      and shared verbatim by server and client prediction. Ships over the wire on join.
- [x] **Hand-crafted maps from the Godot editor** — author any scene with `CollisionShape3D`/
      `BoxShape3D` solids, a `Marker3D` named `PlayerSpawn`, and `EnemySpawn*` markers, then export
      it with `tools/export_dungeon.gd` (runs headless) to JSON; serve it with
      `dotnet run --project WoadRaiders.Server -- --map maps/YourMap.json`. `maps/TestArena.tscn` is
      a working example, and the server defaults to it when run without `--map`. **Maps are the only
      way dungeons exist** — procedural generation has been removed by design (hand-crafted maps are
      the product direction).
- [x] **Dungeon art pass** — hand-crafted maps now **render their own Godot scene** (meshes,
      materials, lights authored in the editor; the collision boxes stay the sim truth). The export
      tool records the source scene in the JSON; the server passes it over the wire; the client
      instantiates it — falling back to placeholder textured boxes if the scene is missing. The wall
      occlusion fade works on authored meshes too (tall meshes only, via `GeometryInstance3D`
      transparency; opt out with a `no_fade` group). `TestArena.tscn` demos it with its own
      materials and torch-lit braziers.
- [x] **Real map with a glTF kit** — the **KayKit Dungeon Remastered** kit (CC0, 203 glTF models)
      is installed at `addons/kaykit_dungeon_remastered`, and `maps/Barrow.tscn` ("The Barrow") is a
      sprawling seven-room dungeon built from it — entry hall, pillared great hall, shrine,
      storeroom, barracks, treasury, and a long crypt, joined by looping corridors, with auto-placed
      wall torches, banners, and props (241 floor tiles, 193 collision solids). Enemy density is
      **map-driven**: the server targets 2 enemies per `EnemySpawn` marker (clamped 4–24). Serve it with
      `dotnet run --project WoadRaiders.Server -- --map WoadRaiders.Client/maps/Barrow.json`.
      Kit pieces are on a 4-unit grid, placed under a ×20-scaled `Visuals` node (1 kit tile = 80
      world units); collision boxes and markers are authored in world units as usual.
- [ ] **Dungeon content & depth** — more maps and kit variety; BepuPhysics for non-box collision
      and DotRecast navmesh for smarter AI pathing, all behind `IDungeonGeometry`.
- [ ] **Gauntlet-style dungeons** — enemy generators (destroy to stop the horde), an exit/portal to
      descend to the next level, health-drain + food pickups, keys/doors/gates, themed realms.
- [x] **Loot (first pass)** — enemies drop themed items on death (rarity-weighted, Celtic/Pict
      names like "Chieftain's Blade"); players auto-collect nearby drops; server-authoritative
      per-player inventory with reliable pickup events; ground loot in snapshots. `Core`, unit-tested.
- [x] **Equipment & inventory** — equip items (I opens inventory, 1-9 equips); weapon/trinket Power
      boosts your cleave, armor soaks incoming damage. Equipping is server-validated. `Core`,
      unit-tested. This closes the core loop: kill → loot → equip → hit harder.
- [ ] **Loot & progression polish** — loot visibility (drops linger/beam before pickup), item
      affixes/multiple stats, character levels/XP, and persistence via PlayFab Economy.
- [ ] **Matchmaking + managed dedicated hosting + accounts/economy** — planned via **PlayFab**
      (Azure/.NET-aligned; its Economy system maps onto ARPG loot). Client → PlayFab for login +
      matchmaking → allocated server instance; swap `localhost` for the allocated address.

---

## Project layout
| Project | Role | Depends on |
|---|---|---|
| `WoadRaiders.Core` | Authoritative simulation, engine-free | — |
| `WoadRaiders.Shared` | Wire protocol / packets | Core, LiteNetLib |
| `WoadRaiders.Server` | Headless dedicated server | Core, Shared |
| `WoadRaiders.Client` | Godot 4 (.NET) game client | Core, Shared |
| `WoadRaiders.Core.Tests` | Simulation unit tests | Core |

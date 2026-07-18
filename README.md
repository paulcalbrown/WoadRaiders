# WoadRaiders

A public co-op, **server-authoritative** ARPG (Celtic/Pictish woad-raider theme) played across
**open 3D realms** — sprawling, fully vertical highlands in the spirit of **Gauntlet Legends /
Dark Legacy**, seen through a perspective **chase camera** that swings behind your direction of
travel. Real-time action, loot, generated-offline realms — built so that online multiplayer with
matchmaking is a first-class concern from day one, not a bolt-on.

What started as an architecture scaffold is now a playable slice: pick one of **four classes**,
**forge or join a raid instance** with up to 8 players, climb the realm to its
boss, and step out through the portal to a run summary.

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
Headless dedicated server.    Godot 4 (.NET) chase-camera client.
Plain console app, no         Predicts movement, renders meshes,
engine → containerizes        sends input. Open in Godot to run.
cheaply, allocated per
match by hosting later.
```

Each library has a matching test project: `WoadRaiders.Core.Tests` (the simulation —
movement, clamping, determinism, spawn policy), `WoadRaiders.Shared.Tests` (the
wire-protocol mappers), and `WoadRaiders.Server.Tests` (server internals).

### Target frameworks
| Project | TFM | Why |
|---|---|---|
| `Server`, `Core.Tests` | `net10.0` | Pure .NET; run on the latest runtime. |
| `Core`, `Shared` | `net8.0;net10.0` | Multi-targeted so the net10 server *and* the net8 Godot client can both consume them. |
| `Client` (Godot) | `net8.0` | Godot 4.x embeds the **.NET 8** runtime and can't load a net10 assembly. |

When your Godot build runs on .NET 10, flip the client to `net10.0` and you can drop the `net8.0`
target from `Core`/`Shared`.

### Networking model
- Transport: **LiteNetLib 2.x** (reliable UDP). All gameplay traffic on channel 0; the delivery
  method varies per packet.
- `Welcome` / `JoinRequest` / `Input` (and the other request/event packets): `ReliableOrdered`
  (must arrive, in order). Input is reliable-ordered so the server's per-player **input buffer**
  receives every input exactly once, in sequence — it applies one per tick, replaying the
  client's stream 1:1, so reconciliation introduces ~zero correction (no prediction pop).
- `WorldSnapshot`: **chunked over `Unreliable`** (`SnapshotChunks`). Unreliable delivery caps a
  packet at ~one MTU and never fragments, but the world's size is unbounded (enemies respawn,
  loot piles up) — so every snapshot is framed as one or more tick-stamped chunks (most fit in
  one), and the client's `SnapshotAssembler` reassembles them with a tick guard that restores
  the never-deliver-stale property the old Sequenced channel provided.
- The **connection key** (`WoadRaiders.v14`) is bumped whenever the wire format changes — the
  only build-compatibility gate at connect time. A refused connect is answered with a
  `ConnectDenied` payload (frozen format, readable across version gates) saying why — outdated
  build (with the download URL) or full server — so the client can tell the player instead of
  silently retrying forever. Inbound messages are **rate-limited per connection** (token
  bucket), so one hot client can't starve the loop.
- The server steps each instance's simulation at **30 Hz** and broadcasts snapshots at **20 Hz**;
  it hosts up to **16 live instances** of **8 raiders** each.

---

## Running it

### 1. The dedicated server (works today, no engine needed)
```bash
dotnet run --project WoadRaiders.Server
# → WoadRaiders dedicated server listening on udp/9050 (1 maps, up to 16 instances
#   of 8 raiders; sim 30Hz, snapshots 20Hz). Ctrl+C to stop.
```
Without arguments it loads **every catalog realm** (The Crag) from the `maps/`
directory beside its binary (the build copies the canonical JSON there from
`WoadRaiders.Client/maps/`, so a published server is self-contained) and lets players
forge/join instances of them. Options: a bare number sets the
port (`… -- 9060`); `--map path/to/map.json` pins every instance to one map (dev
convenience for map work, e.g. `--map WoadRaiders.Client/maps/TestArena.json`). The server
hosts **geometry JSON**; realm `.tscn` scenes are the *authoring* format, baked to JSON by
the map tools (the generator does it automatically, hand-made scenes via
`WoadRaiders.Client/tools/bake_realm.gd`).

### 2. The Godot client
Requires the **.NET/Mono build** of Godot (installed here as **Godot 4.7** via Scoop — the CLI
command is `godot-mono`). The client csproj is pinned to `Godot.NET.Sdk/4.7.0` to match; keep the
two in lockstep if you upgrade Godot.
1. Open the `WoadRaiders.Client` folder in the Godot editor once (it generates the `.godot/`
   import cache), then press **Play**. From the CLI: `godot-mono --path WoadRaiders.Client`.
2. The flow: a Celtic/gothic **title screen** (your name + server endpoint — exported builds
   default to the public dev server `woadraiders.eastus.azurecontainer.io`; editor runs default
   to `127.0.0.1:9050`) → **character select** (Knight, Rogue, Mage, or Ranger — per-class stats
   live in `Core.ClassArchetypes`; Mage and Ranger fire real projectiles) → **realm select**
   (The Crag) → the **raid browser**, where you forge a fresh instance or join a
   live one → the run itself. You arrive through a blue entrance portal; fellow raiders wear
   overhead nameplates with woad-blue health bars. Kill the **realm's lord** and a green exit
   portal opens — step through it to end the run on a **summary screen** (time, gold, relics,
   the warband's kill tally). Generated chiptune themes play throughout; a music autoload
   carries the menu theme seamlessly across screens.
3. Controls: **WASD/arrows** move (camera-relative — up is into the screen), **right-click**
   (hold) paths toward the cursor, **left-click** attacks aimed at the cursor, **Space** attacks
   in your current facing, **I** opens the inventory (**1-9** to equip), **Esc** backs out.
   Skeleton **minions** chase you down, **rogues** dart in fast, **mages** rain bolts from the
   overlooks, and the realm's lord — a hulking armored skeleton — holds the walled summit court.
   All animated KayKit characters that face their movement and play idle/run/attack clips
   (enemies carry billboard health bars; your health bar sits at the top of the screen with a
   "recently lost" damage-chip trail). Loot spins and glows by rarity. Solids between you and
   the camera fade out; the chase camera keeps itself clear of the terrain.
   (Dev flags: `--play` skips straight into the game, `--select` to the class picker,
   `--screenshot` saves title stills and exits.)

### 3. A two-player local test
Start the server, then launch **two** client instances (Godot editor "Play" + an exported build,
or two editor windows). Forge a raid in the first client; it then appears in the second client's
raid browser — join it and both characters share one authoritative instance. (Two clients that
each *forge* get isolated worlds — that's the instancing working as intended.) Note an exported
build defaults to the **public** server — point it at the local one with `--server=127.0.0.1`
or by typing it in the title-screen box.

### 4. Run the tests
```bash
dotnet test   # resolves WoadRaiders.slnx — runs Core, Shared, and Server test projects
```

### 5. Ship a release
Releases are CI/CD: **bump the `ConnectionKey` version and merge to main** — the
[Release workflow](.github/workflows/release.yml) tests every push, and when the new version has
no tag yet it builds everything on a clean runner and publishes automatically (Actions'
`GITHUB_TOKEN` also pushes the container image to ghcr.io, which local runs can't without a
`write:packages` token). Manual dispatch offers `publish=false` (full dry-run build, artifacts
attached to the run) and `force=true` (re-issue an existing version's release from the current
commit). After publishing, CI rolls the **public dev server** — an Azure Container Instance at
`woadraiders.eastus.azurecontainer.io` (udp/9050; ACI because Container Apps has no UDP
ingress) — onto the new image via `tools/deploy-aci.ps1` and OIDC federated login (no stored
Azure secrets). Stop it when idle with `az container stop -g woadraiders -n woadraiders-server`.
The workflow drives the same script a local release uses, so the paths can't drift:
```powershell
.\tools\release.ps1            # export the client (Windows + macOS), publish the server
                               # (win-x64 + linux-x64), build the image, write build/latest.json
.\tools\release.ps1 -Publish   # ...and create the GitHub release (needs an authenticated gh)
```
A release ships the two client builds, a self-contained dedicated server for Windows and Linux
(`WoadRaiders-Server-<rid>.zip`, maps included — unzip and run; on Linux
`chmod +x WoadRaiders.Server` first, zips don't carry the execute bit), and a ready-to-deploy
container image (`tools/server.Dockerfile`, built from the same linux-x64 bytes as the zip):
`docker load -i WoadRaiders-Server-image.tar.gz`, then
`docker run -d -p 9050:9050/udp ghcr.io/paulcalbrown/woadraiders-server:vN` — or pull that ref
straight from ghcr.io once pushed (`-Publish` pushes it when the token has `write:packages`).
Every release also carries a `latest.json` manifest beside the binaries; GitHub's stable
`releases/latest/download/latest.json` redirect makes the newest release the answer. The client
fetches it when the title screen opens (in the background — offline or slow networks just mean
no news) and shows a **"get the update"** notice when the released protocol version is ahead of
its own. A stale client that connects anyway is refused with the same download URL (the
`ConnectDenied` handshake above) — so players learn about updates both before and at the door.
`dotnet run tools/UpdateProbe.cs` sanity-checks the live manifest after publishing.

---

## Roadmap

Build the *fun* locally first (client → `localhost` server); light up the online backend once the
core loop earns it.

- [x] **Client-side prediction + reconciliation** — implemented in `Core.ClientPrediction`
      (engine-free, unit-tested). The local player applies input instantly and reconciles against
      each snapshot; drift stays ~1–2 ticks. Remote players ease toward their latest snapshot.
- [x] **Server-side input buffer** — the server buffers each client's inputs and applies exactly
      one per tick in sequence order (a small jitter cushion; `ServerInputBuffer` in `Core`,
      unit-tested), replaying the client's stream 1:1 so reconciliation introduces ~zero correction
      — the drift that caused the visible prediction pop is gone at the source (the smoke test
      asserts `maxCorrection ≈ 0`). Input is sent `ReliableOrdered` so nothing is lost or reordered.
- [x] **Snapshot chunking** — world snapshots are split across UDP-sized chunks
      (`Shared.SnapshotChunks` + `SnapshotAssembler`), so an unbounded world can never outgrow
      a packet and crash the server. Every snapshot takes the chunked path, so the multi-chunk
      code is exercised constantly rather than only on the day the world finally gets big.
- [ ] **Remote interpolation** — buffer timestamped snapshots and render remote players ~100 ms in
      the past for smoothness (currently a simple ease-toward-latest).
- [x] **Combat (first pass)** — server-authoritative directional melee (a frontal cleave: every
      enemy in front and within reach takes the hit — not a 360° area sweep), enemies with
      seek/attack AI, damage, enemy death + respawn, player respawn. Attacks are **aimed**:
      left-click swings toward the cursor (the aim ships in the input packet). **Casters fire
      authoritative spell bolts** — real projectiles that travel, are blocked by walls, and can
      be side-stepped (damage lands on impact, not on cast). Logic in `Core`, unit-tested. The
      client predicts movement, never damage.
- [x] **Four playable classes** — Knight (armored line-holder), Rogue (fast, fragile,
      knife-quick), Mage (slow glass cannon, heavy bolts), Ranger (skirmisher, rapid light
      bolts), picked on a character-select screen. The stat table (`Core.ClassArchetypes`)
      is shared by server, prediction, and tests; the class ships as a byte in the join
      request and player snapshots and picks the client's KayKit adventurer model and gear.
- [x] **Health bars & presentation pass** — a top-of-screen player health bar and enemy
      billboard bars, both with a "recently lost health" **damage-chip** trail (`Core.DamageChip`,
      unit-tested); overhead **nameplates** on fellow raiders (woad-blue vs enemy red); a
      downward faction-coloured spotlight on every character; arrivals staged through a blue
      **entrance portal** with a walk-out (pure render effect — prediction untouched).
- [ ] **Combat polish** — downed state + revive, more enemy types, attack telegraphs.
      (Currently: immediate respawn at origin.)
- [x] **3D simulation** — `Core` simulates in full 3D world space (`System.Numerics.Vector3`,
      **Y-up**, matching Godot and glTF conventions, so the client maps sim positions 1:1). Dungeon
      shape sits behind the `IDungeonGeometry` seam. Movement input stays 2D ground-plane intent
      (`MoveX`/`MoveZ`); the geometry decides height.
- [x] **Open realms with real verticality (the Gauntlet rework)** — the game left its
      fixed-isometric dungeon roots for **Gauntlet Legends / Dark Legacy**-style realms.
      `DungeonGeometry` gained a smooth **heightfield terrain** base plane (bilinear
      `HeightField`, shipped bit-exact over the wire so prediction walks the same ground) under
      the solid boxes; movement now **rides the ground** — each step lands on the surface at its
      destination, a rise beyond `SimConstants.StepHeight` (18) is a wall or cliff, drops are
      unlimited (one-way jump-downs are level design). Sight lines respect terrain crests;
      player bolts **hug the slopes** and sail level over gorges, enemy bolts aim in full 3D so
      overlook mages rain fire downhill. Realms are authored as **natural Godot .tscn
      scenes**: the generated scene is saved by **Godot's own serializer** and contains only
      built-in nodes and resources — a REAL displaced terrain `ArrayMesh`, stone, braziers,
      lights, markers; no scripts, no metadata — so it opens whole in any Godot editor,
      exactly as if someone had modelled it there (clients missing a custom map's scene
      rebuild the realm from the wire geometry instead). The camera became a perspective
      **chase rig** that swings behind your travel and keeps clear of the land (movement
      keys are camera-relative). The first realm, **The Crag**
      (glen → gorge bridge → switchback climbs → rolling moor with a standing-stone circle →
      walled summit court, ~260 units of climb), is **generated scene-first**: its design
      lives client-side (`scripts/tools/CragDesign.cs` — the layout math — consumed by
      `RealmSceneBuilder`, which has the whole engine to dress the realm with: any meshes,
      materials, particles, or asset kits; boulder fields are the first pure scenery).
      `tools/GenerateRealm.cs` orchestrates: Godot builds and saves `Crag.tscn` itself
      (ResourceSaver, random ids normalized so regeneration is byte-deterministic), then
      `Crag.json` — the geometry the server hosts — is **baked FROM the scene** by the same
      `bake_realm.gd` every hand-sculpted realm uses; nothing is ever generated from the
      JSON, so the sim format never limits what a realm can look like. Validation runs the
      real sim rules against the BAKED geometry: a virtual raider walks the whole route
      with `Move`, and `Core.RealmValidator` flood fills prove every camp reachable /
      borders sealed / no stranding pits — the same checks hand-made realms get
      (`tools/ValidateRealm.cs`).
      The Barrow and Cairn dungeons were removed with the old camera. Wire `v14`; probes:
      `tools/TerrainProbe.cs` (terrain on the wire, spawn on the ground, the authoritative Y
      climbing as you walk, replay determinism) + the existing class/instance/portal probes.
- [x] **Fully 3D dungeon geometry (`DungeonGeometry`)** — dungeons are sets of **world-space solid
      boxes** + spawn markers, exactly what a Godot-editor scene reduces to. Collision is a vertical
      **cylinder-vs-box** test that is 3D-aware (walls block; beams above head height don't), sliding
      and shared verbatim by server and client prediction. Ships over the wire on join.
- [x] **Hand-crafted maps from the Godot editor** — author any scene with `CollisionShape3D`/
      `BoxShape3D` solids, a `Marker3D` named `PlayerSpawn`, typed `EnemySpawn*` markers (name
      contains `Rogue`/`Mage` for those types), and an optional `BossSpawn`; bake it to the
      geometry the server hosts with `WoadRaiders.Client/tools/bake_realm.gd`, then serve:
      `dotnet run --project WoadRaiders.Server -- --map maps/YourRealm.json`.
      `maps/TestArena.tscn` is a working example of the conventions. **The game only ever
      consumes map files** — runtime procedural generation stays out by design; the shipping
      maps are hand-crafted or generated *offline* into the same formats. **Open realms are
      authored the same way** (see the open-realms entry above): give the scene terrain —
      ANY meshes in the `terrain` group (sculpt in Blender, use CSG, or edit the generated
      realm's Terrain mesh), which the bake tool samples from above onto a heightfield grid
      (`terrain_cell_size` metadata on the root overrides the 40-unit default; put big
      ground meshes in `no_fade` too; the sampling math is the unit-tested
      `Core.TerrainSampler`) — plus nodes in the `brazier` group for fire props, then check
      the baked JSON with `dotnet run tools/ValidateRealm.cs` (camps reachable, borders
      sealed, no stranding pits — the same bar the generated realm passes). Clients that
      have the scene render it as authored; clients that don't rebuild the realm from the
      wire geometry, so a custom-map server never shows anyone an empty world.
- [x] **Dungeon art pass** — hand-crafted maps **render their own Godot scene** (meshes,
      materials, lights authored in the editor; the collision boxes stay the sim truth). The export
      tool records the source scene in the JSON; the server passes it over the wire; the client
      instantiates it — falling back to placeholder textured boxes if the scene is missing. The wall
      occlusion fade works on authored meshes too (tall meshes only, via `GeometryInstance3D`
      transparency; opt out with a `no_fade` group). `TestArena.tscn` demos it with its own
      materials and torch-lit braziers.
- [x] **Real map with a glTF kit** *(retired in the open-realms rework — kept here as history;
      the KayKit dungeon kit remains installed for future interiors)* — the **KayKit Dungeon
      Remastered** kit (CC0, 203 glTF models)
      is installed at `addons/kaykit_dungeon_remastered`, and `maps/Barrow.tscn` ("The Barrow") was
      a sprawling dungeon built from it: entry hall, twin pillared halls, a north wing (shrine,
      storeroom, mage reliquary), a south wing (columned crypt, rogue ossuary), and a grand
      processional east to the antechamber and the **Barrow King's throne room** — all joined by
      looping corridors, with auto-placed wall torches, banners, and props (474 collision solids,
      30 typed enemy spawns). Enemy density and **mix** are map-driven: the server spawns 1 enemy
      per typed `EnemySpawn` marker (clamped 4–40), replenished one every 6 s. Kit pieces are on a
      4-unit grid, placed under a ×20-scaled `Visuals` node (1 kit tile = 80 world units);
      collision boxes and markers are authored in world units as usual.
- [x] **A second dungeon, generated offline** *(retired with the Barrow; its
      generate-and-validate approach lives on, grown up, in `tools/GenerateRealm.cs`)* —
      `maps/Cairn.tscn` ("The Cairn") was a ring-tomb of standing stones computed by a
      file-based generator into an authored-quality scene + JSON pair, validating its own
      output (JSON round-trip + a flood fill proving the boss and every spawn reachable).
- [x] **Dungeon instances** — the server hosts player-forged **instances**: a join request
      either forges a fresh instance (its own `GameSession`, world, and enemy population) or
      enters a live one by id, so separate warbands never share a world. The client's raid
      browser lists live instances of the chosen dungeon and refreshes while open; denied joins
      (gone/full) bounce you back with a reason. Emptied instances linger 60 s (a disconnected
      raider can rejoin), then are reaped. Verified end-to-end by a scripted probe
      (`tools/InstanceProbe.cs`).
- [x] **The boss portal & run summary** — when the boss falls, a green **exit portal** opens;
      stepping through it ends your run and the server sends a `RunComplete` summary (duration,
      gold, relics looted, the warband's kill tally), shown on a run-summary screen with roads
      back to character select, dungeon select, or the main menu.
- [x] **Enemy types & the first boss** — enemies are typed (`EnemyType` + an `EnemyArchetypes`
      stat table in `Core`): **Minions** (baseline melee), **Rogues** (fast, fragile, quick
      stabs), **Mages** (slow casters that strike from range), and the **Boss** (the Barrow
      King: heavy hits, guaranteed triple loot, respawns 120 s after falling). Enemies **aggro
      by proximity** (idle at their posts until a player nears — essential on a large map) and
      leash back home. Maps choose the mix with marker names (`EnemySpawn7_Rogue`,
      `EnemySpawn12_Mage`, `BossSpawn`); the type ships as a byte on enemy snapshots and picks
      the client model, scale, attack clip, and health-bar size. **Mages are ranged**: they fire
      an authoritative spell bolt (a glowing projectile in the world snapshot) that flies, is
      stopped by walls, and misses if you side-step — so you can dodge a mage but not a minion.
- [x] **Animated characters** — players and enemies are rigged KayKit characters (the
      adventurer pack's Knight/Rogue/Mage bodies for the four classes, the skeleton pack for
      enemies) at `addons/kaykit_character_pack_{adventures,skeletons}`. They face their
      movement direction and play **idle / run / attack** clips. Facing and movement are
      derived client-side from motion; the attack clip is driven by an authoritative `Attacking`
      flag on the snapshot (set when an attack lands, ticked down, broadcast).
- [ ] **Dungeon content & depth** — more maps and kit variety; BepuPhysics for non-box collision
      and DotRecast navmesh for smarter AI pathing, all behind `IDungeonGeometry`.
- [ ] **Gauntlet-style systems** — enemy generators (destroy to stop the horde), an exit/portal to
      descend to the next realm, health-drain + food pickups, keys/doors/gates, more themed
      realms (the realm FORM itself shipped in the open-realms rework above).
- [x] **Loot** — slain enemies drop **gold piles** (75%, added to your purse), **health potions**
      (50%, consumed on pickup), and **equipment** (50% from common enemies; the boss always pays
      out): rarity-weighted items with woad-raider names ("Chieftain's Battleaxe", "Morrígan's
      Dagger") across the KayKit weapon kit's eight types (swords, axes, dagger, staff, crossbow,
      shield). Players auto-collect nearby drops; the inventory is server-authoritative with
      reliable pickup events; ground loot rides the snapshots. `Core`, unit-tested.
- [x] **Equipment & inventory** — equip items (I opens inventory, 1-9 equips); every weapon
      type boosts your strike's Power, the shield equips to the armor slot and soaks incoming
      damage (the trinket slot is reserved). Equipping is server-validated. `Core`, unit-tested.
      This closes the core loop: kill → loot → equip → hit harder.
- [x] **Title screen, UI dress & music** — a code-first screen flow (title → class → dungeon →
      raid browser → run → summary) wearing a reusable Celtic/gothic UI kit (knotwork, fog
      backdrop, blackletter Pirata One type, glowing menu widgets). The **music is generated by
      code**: `tools/GenerateTitleMusic.cs` and `tools/GenerateBarrowMusic.cs` render looping
      chiptune WAVs (loop points in the `smpl` chunk), and a `MusicJukebox` autoload carries the
      menu theme seamlessly across screens and swaps to the dungeon's theme in the run.
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
| `WoadRaiders.Shared.Tests` | Wire-protocol mapper tests | Shared |
| `WoadRaiders.Server.Tests` | Server-internals tests | Server |

`tools/` holds .NET 10 file-based apps (`dotnet run tools/<Name>.cs`): the realm generator
(`GenerateRealm.cs` — orchestrates the scene-first chain: Godot builds The Crag's scene
from its design, the served JSON is baked from it, and the baked geometry is validated),
the realm checker (`ValidateRealm.cs` — the same playability bar for hand-made bakes, plus
`--compare` for cross-format geometry proofs), the two music generators, and scripted
LiteNetLib **probes** (`ClassProbe`, `InstanceProbe`, `PortalProbe`, `TerrainProbe`) that
verify class stats, instance isolation, the portal flow, and the terrain/verticality
end-to-end against a running server — no Godot needed. The Godot-side pieces (the
`CragDesign` layout math + `RealmSceneBuilder`, `bake_realm.gd` — baking any scene to
server geometry via the C# `RealmBaker` + `Core.TerrainSampler`, `build_realm_scene.gd`,
scene measurement) live in `WoadRaiders.Client/`.

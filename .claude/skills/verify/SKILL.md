---
name: verify
description: How to run and observe WoadRaiders end-to-end — headless server + a scripted LiteNetLib probe client — to verify server/protocol changes without launching Godot.
---

# Verifying WoadRaiders changes

## Build & test

```powershell
dotnet build WoadRaiders.slnx          # solution file is .slnx, not .sln
dotnet test WoadRaiders.slnx --no-build
```

## Run the dedicated server

```powershell
dotnet run --project WoadRaiders.Server -- [port] --map <map.json>
# Without --map it loads EVERY catalog realm map (DungeonCatalog: Crag.json).
# The server hosts geometry JSON only; .tscn scenes are the AUTHORING format,
# baked to JSON by the tools below.
# Players forge/join INSTANCES of them: a JoinRequest either creates a
# fresh instance (Mode=Create, naming the realm) or enters a live one by id
# (Mode=Join); each instance is its own GameSession/world. With --map only that
# map is loaded and every forged instance uses it. Empty instances are reaped
# after a 60 s linger.
# Listens on udp/9050 by default. Stop it via the process owning the port:
#   Stop-Process -Id (Get-NetUDPEndpoint -LocalPort 9050).OwningProcess[0] -Force
```

Custom maps are plain JSON (see `DungeonGeometryFile` docs): `spawn: [x,y,z]`,
`enemySpawns: [[x,y,z],...]`, optional `enemySpawnTypes` (0 Minion, 1 Rogue,
2 Mage — parallel array), optional `bossSpawn`, optional `terrain` (a smooth
heightfield: originX/originZ/cellSize/width/depth + row-major `heights` — the
base plane movement rides; see the verticality rules in `DungeonGeometry`).
The geometry carries SIM truth only — scenery lives in the .tscn and never
reaches the wire.

**Realms are authored as Godot .tscn scenes and PLAYED from baked JSON —
and the SCENE COMES FIRST, for generated and hand-made realms alike.**
The generated scene is a NATURAL Godot file — saved by Godot's own
ResourceSaver, built-in nodes and resources only, a REAL displaced terrain
ArrayMesh plus free-form scenery (boulder fields today), no scripts, no
metadata; it opens whole in any Godot editor. Designs live CLIENT-SIDE as
`IRealmDesign`s (`WoadRaiders.Client/scripts/tools/CragDesign.cs` — layout
math) that build a `RealmScene` (the facade holding the bake conventions) and
hand it back, so a design can place ANYTHING Godot can express; a new scene
starts EMPTY, so a design states its own sky, lights, and materials (a look
belongs to its realm; `TerrainSurface` and `SceneryRecipes` offer shared
pieces, but a realm states its own colours). The `RealmSceneBuilder` that runs them knows
no realm — it names the root after the design and saves. Nothing is ever generated
from the geometry JSON. `dotnet run tools/GenerateRealm.cs [realm]` (default
`Crag`) does the whole chain: builds the client → drives godot-mono to build
`<realm>.tscn` from its design (tools/build_realm_scene.gd) → normalizes the
serializer's random ids (deterministic regeneration) → bakes `<realm>.json`
FROM the scene via the standard hand-made pipeline (bake_realm.gd) →
validates the BAKED geometry (RealmValidator + the route walk, where the
realm has a route). Reshape a realm by editing its design and rerunning; add
one with a design class + a line in `RealmDesigns`. Hand-edits to the .tscn
are equally fine — re-bake + ValidateRealm afterwards (steps 2–3 below).
The hand-made pipeline (any scene in `WoadRaiders.Client/maps/`):
  1. Terrain: ANY meshes in group `terrain` (the natural way — sculpt in
     Blender, CSG, or edit the generated Terrain mesh; put big ground meshes
     in `no_fade` too). A `RealmTerrain` node or `metadata/terrain_*` on the
     root also work (bake reads them without sampling). Collision:
     axis-aligned `BoxShape3D` CollisionShape3Ds. Markers: `PlayerSpawn`
     (required), `EnemySpawnN[_Rogue|_Mage]`, `BossSpawn`. Scenery (braziers,
     banners, rocks) needs NO convention — the bake ignores what it doesn't
     recognise, so place whatever you like.
  2. Bake to server geometry: `dotnet build WoadRaiders.Client`, then
     `godot-mono --headless --path WoadRaiders.Client -s
     res://tools/bake_realm.gd -- res://maps/MyRealm.tscn
     res://maps/MyRealm.json` (sampling cell 40; root metadata
     `terrain_cell_size` overrides; the sampling math is unit-tested
     Core.TerrainSampler).
  3. `dotnet run tools/ValidateRealm.cs WoadRaiders.Client/maps/MyRealm.json`
     — Core.RealmValidator proves camps reachable, borders sealed, no
     stranding pits. `--compare` proves two maps carry identical sim geometry.
  4. Serve the JSON with `--map`; every client renders the scene as authored.
     A client whose build lacks that .tscn REFUSES the raid (terminal
     Incompatible + the download URL) — there is no from-geometry renderer,
     so a map must ship in the build that plays it.
`SpawnDirector` clamps the live population to 40 regulars no matter how many
markers the map has. An all-Mage map keeps projectiles in the snapshot, which
is the cheapest way to inflate world size.

## Drive the wire protocol without Godot

Working examples live in `tools/` (.NET 10 file-based apps — `dotnet run
tools/<Probe>.cs` with the server up):
- `ClassProbe.cs` forges an instance as a mage, walks to the nearest enemy,
  shoots it, and asserts class + projectile facts from the snapshot stream.
- `TerrainProbe.cs` verifies the open-realm terrain: the heightfield
  arrives on the wire, the spawn stands ON the ground, walking east RAISES the
  authoritative Y (server-side verticality), and replaying the same inputs over
  the client-rebuilt geometry lands where the server says (prediction-grade
  determinism).
- `InstanceProbe.cs` drives four clients through the instance flow: forge,
  browse the list, join by id, cross-instance isolation, and the
  JoinDenied path. Run it against a FRESH server.
- `PortalProbe.cs` verifies the end-of-run chain: kill the boss, the portal
  opens in the snapshot, walking in yields a RunComplete and removal from
  the world. It needs the tiny arena so the fight is fast — start the server
  with `--map tools/maps/portal_arena.json`.
- `ConnectProbe.cs` verifies the connect-refusal handshake: a stale key (and
  junk connect data) is rejected WITH a `ConnectDenied` payload naming the
  server's key + download URL, and the current key connects. Re-run it after
  every `ConnectionKey` bump — the payload's format is frozen across versions.
- `UpdateProbe.cs` fetches and validates the `latest.json` release manifest
  (no server needed — it hits GitHub Releases, or a URL/file you pass). Run
  it after every `tools/release.ps1 -Publish`. The client-side notice can be
  tested without a release: serve a fake manifest locally and launch the
  client with `--manifest=http://127.0.0.1:PORT/latest.json --screenshot=out.png`.
Adapt their skeletons for new protocol checks.

A minimal console probe (project ref to `WoadRaiders.Shared`, LiteNetLib comes
transitively) is enough to exercise joins, snapshots, and the chunk assembler:
connect with `NetConfig.ConnectionKey`, send `MessageType.JoinRequest` framed
via `NetProtocol.Frame` (Mode=Create + Dungeon forges an instance; Mode=Join +
InstanceId enters one — the Welcome echoes the instance id), then feed received
`WorldSnapshot` packets (after the type byte) into `SnapshotAssembler.TryAdd`.
Track ticks to assert no stale delivery. Run several probe processes to add
players (~37 bytes + name each) in ONE instance (join the first probe's
instance id) when you need the snapshot to cross the single-packet MTU budget
and split into chunks.

Gotchas:
- The ConnectionKey is version-gated (`WoadRaiders.vN`) — a stale probe build
  is rejected at connect. Since v13 the reject carries a `ConnectDenied`
  payload (server key + reason) in `DisconnectInfo.AdditionalData`; a probe
  that dials with the wrong key can read it to learn the server's key.
- To verify the server CONTAINER image (podman on this machine), probe the
  podman machine's IP, not 127.0.0.1 — Windows' WSL2 localhost relay does not
  forward UDP, so a published `-p 9050:9050/udp` port is unreachable via
  localhost and every dial just times out. Get the IP with
  `podman machine ssh "ip -4 addr show eth0"`, then
  `dotnet run tools/ConnectProbe.cs <machine-ip>`.
- The Godot client (`WoadRaiders.Client`) needs `godot-mono` and a window;
  probe the socket instead unless the change is client-rendering code.

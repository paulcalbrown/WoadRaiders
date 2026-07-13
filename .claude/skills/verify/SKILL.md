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
# Without --map it serves WoadRaiders.Client\maps\TestArena.json (3 spawns).
# Listens on udp/9050 by default. Stop it via the process owning the port:
#   (Get-NetUDPEndpoint -LocalPort 9050).OwningProcess | Stop-Process -Force
```

Custom maps are plain JSON (see `DungeonGeometryFile` docs): `spawn: [x,y,z]`,
`enemySpawns: [[x,y,z],...]`, optional `enemySpawnTypes` (0 Minion, 1 Rogue,
2 Mage — parallel array), optional `bossSpawn`. `SpawnDirector` clamps the live
population to 40 regulars no matter how many markers the map has. An all-Mage
map keeps projectiles in the snapshot, which is the cheapest way to inflate
world size.

## Drive the wire protocol without Godot

A working example lives at `tools/ClassProbe.cs` (a .NET 10 file-based app —
`dotnet run tools/ClassProbe.cs` with the server up): it joins as a mage,
walks to the nearest enemy, shoots it, and asserts class + projectile facts
from the snapshot stream. Adapt its skeleton for new protocol checks.

A minimal console probe (project ref to `WoadRaiders.Shared`, LiteNetLib comes
transitively) is enough to exercise joins, snapshots, and the chunk assembler:
connect with `NetConfig.ConnectionKey`, send `MessageType.JoinRequest` framed
via `NetProtocol.Frame`, then feed received `WorldSnapshot` packets (after the
type byte) into `SnapshotAssembler.TryAdd`. Track ticks to assert no stale
delivery. Run several probe processes to add players (33 bytes each) when you
need the snapshot to cross the single-packet MTU budget and split into chunks.

Gotchas:
- The ConnectionKey is version-gated (`WoadRaiders.vN`) — a stale probe build
  is silently rejected at connect.
- The Godot client (`WoadRaiders.Client`) needs `godot-mono` and a window;
  probe the socket instead unless the change is client-rendering code.

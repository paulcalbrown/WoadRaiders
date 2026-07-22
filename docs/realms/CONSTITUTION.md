# The Realm Constitution

Spec revision: 1 — 2026-07-21 — status: **proposed**

The rules every realm inherits. A realm spec opens with `Inherits: REALM-C-*` and
states only what is particular to it; nothing here is restated per realm.

These are not new rules. Every one of them is already true of the pipeline — the
Constitution's job is to put them where a spec (and an agent) can cite them,
instead of leaving them scattered through XML doc comments and session memory.
Each carries the code that is its authority.

## Conventions

| Keyword | Meaning |
| --- | --- |
| **MUST** / **SHALL** | Normative. A validator, a test, or a hard pipeline failure enforces it — or one is owed and named. |
| **SHOULD** | Strong default. A realm that breaks it records the reason in its own §Deviations. |
| **MAY** | Permitted variation. |
| anything else | Rationale. Binds nothing. |

Requirement IDs are `REALM-C-###`. Realm specs use `<REALM>-<CATEGORY>-###`.
Every requirement carries an annotation: `[checked: <oracle>]` when something
mechanical proves it, `[judged: <who>]` when a human decides, `[checked: TODO]`
when the check is owed and does not exist yet — an honest backlog.

---

## The shape of the pipeline

### REALM-C-001 — The scene is authored; the geometry is derived
The realm pipeline SHALL build the Godot scene first and bake the served
geometry from it; nothing SHALL ever be generated from the baked JSON.
`tools/GenerateRealm.cs` runs the chain: build the client → import assets →
`build_realm_scene.gd` (`RealmSceneBuilder`) → normalise ids → `bake_realm.gd`
(`RealmBaker`) → validate.
*Authority:* [tools/GenerateRealm.cs](../../tools/GenerateRealm.cs), [RealmSceneBuilder.cs](../../WoadRaiders.Client/scripts/tools/RealmSceneBuilder.cs)
`[checked: tools/GenerateRealm.cs runs the whole chain and fails loudly]`

### REALM-C-002 — One naming rule
A realm MUST contain exactly one `Marker3D` named `PlayerSpawn`. Enemy camps are
`EnemySpawn*` (the name carries the type: `Rogue`, `Mage`, else Minion), the boss
is `BossSpawn`, and the exit is `PortalSpawn`. There is no other naming
convention in the format, and only the first is required.
*Authority:* [RealmSceneFile.cs](../../WoadRaiders.Core/RealmSceneFile.cs)
`[checked: RealmSceneBuilder refuses to save; RealmSceneFile.Parse throws; RealmSceneFileTests]`

### REALM-C-002a — A realm may stage its own ending
Where a realm authors `PortalSpawn`, the exit SHALL open there; absent one, it
opens where the boss stood. The fallback is the honest default for a realm whose
last chamber IS its ending, and the marker exists because otherwise the run stops
on the tick of the kill and the falling-action beat has nowhere to live.
*Authority:* [RealmScene.SetPortalSpawn](../../WoadRaiders.Client/scripts/tools/RealmScene.cs), [GameSession.cs](../../WoadRaiders.Server/GameSession.cs)
`[checked: PortalRunTests.A_realm_that_authors_a_portal_opens_the_way_out_where_it_says]`

### REALM-C-003 — Every mesh is collision
Every `MeshInstance3D` under the scene root SHALL be baked as collision,
whatever it is and wherever it sits — no privileged mesh type, no exception for
instanced kit props. The single opt-out is the `no_collide` group
(`RealmScene.DeclarePassable`), which is inherited by the whole subtree and
never revoked.
*Authority:* [RealmBaker.cs](../../WoadRaiders.Client/scripts/tools/RealmBaker.cs), [MeshTriangles.cs](../../WoadRaiders.Client/scripts/tools/MeshTriangles.cs)
`[checked: the bake prints the excluded mesh and triangle count on every run]`

### REALM-C-004 — Tag intent, never tag facts
A realm MUST NOT introduce a convention that duplicates a computable fact.
Whether a surface holds a raider up is its normal; whether it can be walked is
the navmesh's answer; whether it is too small to matter is the agent radius.
`no_collide` is legitimate precisely because it duplicates nothing — a banner
and a wall panel are the same thin slab and only the author knows which one a
raider walks through.

`no_collide` MUST NOT be used to quiet a route the validator complained about.
That complaint is the level design talking; excusing the prop hides it, and a
blocked route the validator can prove is infinitely preferable to geometry it
can never miss.
*Authority:* [RealmSceneFile.cs `NoCollideGroup`](../../WoadRaiders.Core/RealmSceneFile.cs)
`[judged: review — watch the bake's exclusion count for growth]`

### REALM-C-005 — A batch is a rendering decision, never a collision one
`MeshTriangles.Collect` takes both mesh-bearing node types: a
`MultiMeshInstance3D` is read **per instance**, exactly as a `MeshInstance3D` is,
and is excused only by `no_collide` like anything else.

This was not always true, and the reason it is now is worth keeping. Verified
empirically on Godot 4.7 (`WoadRaiders.Client/tools/probe_serialize.gd`): a
MultiMesh, a `GPUParticles3D` draw pass, a `Decal`, a `FogVolume` and an
`OccluderInstance3D` all round-trip into the saved `.tscn`. Of those the
MultiMesh was the only one bearing geometry an author could mistake for a wall —
and skipping it made a batch of a thousand grave slabs both intangible AND
invisible to the exclusion tally. An exemption with no tag on it and no count
against it is precisely what `no_collide` is counted aloud to prevent, so
collapsing draw calls MUST NOT be a way to opt out of the simulation.

What genuinely bears no triangles — particles, decals, fog volumes, occluders —
contributes nothing and needs no tag, because none of them is geometry the realm
is modelled from and no author could mistake one for stone.
*Authority:* [MeshTriangles.cs](../../WoadRaiders.Client/scripts/tools/MeshTriangles.cs)
`[checked: the bake's no_collide tally counts a MultiMesh once per live instance]`

### REALM-C-006 — If it renders right, it bakes right
Godot winds front faces clockwise; the soup and Recast's slope filter want
counter-clockwise, and `MeshTriangles` swaps two corners per triangle as it
samples. A design therefore MUST simply build meshes Godot renders front-facing.
A surface that renders inside-out in Godot bakes as an overhang, and a realm
whose floors are overhangs has no floor at all — silently.
*Authority:* [MeshTriangles.cs](../../WoadRaiders.Client/scripts/tools/MeshTriangles.cs)
`[checked: TODO — assert every baked realm's spawn point has ground beneath it]`

### REALM-C-007 — State the role, or the cast lands at zero
A mesh the realm is built from SHOULD be filed with a role verb
(`AddFloor` / `AddStructure` / `AddGeometry`). Only `AddFloor` puts a mesh's real
triangles into the design-time soup that `OnFloor` seats markers on; a design
whose ground is modelled but unfiled gets `0` from `FloorAt` and seats its whole
cast at height zero.
*Authority:* [RealmScene.cs](../../WoadRaiders.Client/scripts/tools/RealmScene.cs)
`[checked: RealmValidator — a cast at height 0 inside masonry fails reachability]`

### REALM-C-008 — `OnFloor` always names a level
Every call to `OnFloor` / `FloorAt` MUST pass the height the caller means. A
realm may stack walkable levels, and the convenient answer — the topmost — is
silently wrong underneath anything. Reading it as "topmost surface" once put
every Crypt marker on the roofs (spawn Y 0 → 680).
*Authority:* [RealmScene.cs](../../WoadRaiders.Client/scripts/tools/RealmScene.cs)
`[checked: RealmValidator reachability]`

### REALM-C-009 — Instanced sub-scenes: own the root, not the internals
`PackedScene.Pack` serialises only nodes owned by the packed root, so an
instanced `.glb`'s ROOT must be owned, and its internals must not — otherwise
the whole imported tree is inlined instead of staying an `ExtResource`.
`RealmSceneBuilder.SetOwnerRecursive` already does exactly this.

Consequently a design MUST NOT parent anything under an instanced sub-scene's
root: `Pack` drops such children regardless of ownership unless the instance is
marked editable (godotengine/godot#90823). Hang the light *beside* the torch
prop, never inside it.
*Authority:* [RealmSceneBuilder.cs](../../WoadRaiders.Client/scripts/tools/RealmSceneBuilder.cs)
`[checked: TODO — assert prop light count survives the round trip]`

### REALM-C-010 — Regeneration rewrites nothing
Regenerating an unchanged realm SHALL produce a byte-identical `.tscn`. Designs
MUST NOT use framework RNG; deterministic hashing only. `NormalizeSceneIds`
renames the per-save ids `ResourceSaver` invents — **both** `[sub_resource]` and
`[ext_resource]`.

Ext ids are normalized for a reason that is invisible until it bites: one
pointing at an IMPORTED asset already gets a stable id from the uid its
`.import` pins, so a realm referencing only kit props looks reproducible. One
pointing at a resource the build itself wrote — a sculpted mesh library — has no
uid, and Godot mints a fresh random suffix on every save. Normalizing both means
the guarantee rests here rather than on which flavour of resource a realm
happens to reference.
*Authority:* [tools/GenerateRealm.cs `NormalizeSceneIds`](../../tools/GenerateRealm.cs), [probe_extresource.gd](../../WoadRaiders.Client/tools/probe_extresource.gd)
`[checked: TODO — CI generates twice and compares hashes]`

### REALM-C-010a — A realm's mesh library lives beside its scene
A realm modelled from sculpted meshes SHOULD register them with
`RealmScene.SharedMesh`, which writes each piece to `maps/<Realm>/<piece>.res`
and hands back the disk-backed resource, so placements serialize as ExtResource
and the scene holds no inlined geometry.

The cost this manages is CHURN, not size. `REALM-C-010` means every design
change rewrites the whole scene, and base64 vertex blobs do not delta-compress,
so an inlined library commits a fresh full copy of the geometry every time a
single wall moves. Split out, a layout change rewrites a small text file with a
readable diff and the geometry blob changes only when the geometry does.
Measured (`probe_scene_cost.gd`): a scene costs ~41 bytes per inlined unique
triangle and ~217 per placement, and the split moves 100% of the former out.
Piece names MUST be stable across runs — the name is the file name.
*Authority:* [RealmScene.SharedMesh](../../WoadRaiders.Client/scripts/tools/RealmScene.cs)
`[checked: TODO — assert no realm scene carries inlined vertex data]`

### REALM-C-011 — Scenes with modelled geometry are bake-only
The engine-free reader can measure a `BoxMesh` from scene text and nothing else;
it refuses any other mesh and any opaque instance rather than quietly returning
a smaller realm than the scene describes. A realm modelled from `ArrayMesh` or
kit instances therefore MUST be validated through its **baked JSON**, not
through `ValidateRealm <realm>.tscn`. This is a limit of reading `.tscn` as
text, not a limit on what a realm may be built from.
*Authority:* [RealmSceneFile.cs](../../WoadRaiders.Core/RealmSceneFile.cs)
`[checked: RealmSceneFile.Parse throws on an unmeasurable mesh or an opaque instance]`

---

## What the world is allowed to be

### REALM-C-012 — The validation bar
A realm SHALL pass `RealmValidator.Validate` with zero issues on its baked
geometry: the boss and every enemy camp reachable from the spawn, and nowhere
the spawn can reach stranded from the boss. A realm with an intended path
SHOULD also register a route in `tools/GenerateRealm.cs` and be walked by a
virtual raider under the real `Move` rules.
*Authority:* [RealmValidator.cs](../../WoadRaiders.Core/RealmValidator.cs)
`[checked: RealmValidator.Validate; RealmValidatorTests]`

### REALM-C-013 — Masonry hides walkable islands
Recast rasterizes a solid as a hollow shell, so a room's floor slab running on
underneath its own walls and pillars keeps a walkable span with metres of
clearance above it. **Every thick wall and pillar in a realm contains a walkable
navmesh island, disconnected from everything.**

Nothing MAY treat a successful navmesh snap, or `TriangleSoup.FloorHeightAt`, as
proof that a raider can be at a point. The honest question is connectivity to
the spawn: path to the point and judge where the walker actually lands.
*Authority:* [RealmValidator.cs stranding sweep](../../WoadRaiders.Core/RealmValidator.cs)
`[checked: RealmValidatorTests.A_reachable_pit_with_no_way_back_to_the_boss_is_stranding]`

### REALM-C-014 — Lay stairs along walls, never across floors
A flight rising more than a step is a WALL for most of its length. One struck
diagonally across a floor does not merely climb — it partitions, pinning a
margin against the stone that can be walked forever and left never. Flights MUST
run along a wall, so each flight IS the margin instead of walling one off.
*Authority:* the Crypt chasm fix, [CryptDesign.cs](../../WoadRaiders.Client/scripts/tools/CryptDesign.cs)
`[checked: RealmValidator stranding sweep]`

### REALM-C-015 — A dais is a step, not a plinth
Any platform the cast is seated on MUST rise less than
`SimConstants.StepHeight` (18). A dais taller than a step entombs whoever stands
on it: nothing can climb it, and every cell in the realm reports as stranded.
`[checked: RealmValidator reachability of the boss]`

### REALM-C-016 — Movement geometry facts a design must respect
Derived from `SimConstants` and `NavMeshBuilder`, and true of every realm:

| Fact | Value | Consequence for a design |
| --- | --- | --- |
| Raider height | 44 u | Headroom below 44 u is not walkable at all. |
| Raider radius | 14 u | The navmesh is eroded 14 u from every wall face. |
| Step height | 18 u | Treads MUST rise ≤ 16 u. Drops of any size are legal. |
| Move speed | 220 u/s | One tick's step is 7.33 u. |
| Max walkable slope | 67.83° | The grade a raider beats at StepHeight per tick. |
| Blocker threshold | ≈ 87° (`WallNormalY`) | Deliberately NOT the navmesh's 67.8° — steep ground stays descendable. |
| Navmesh cell | 5 u XZ, 2 u Y | A lane under ~40 u wide bakes to nothing. |
| Navmesh snap extent | 24 XZ / 48 Y | A face longer than that reach is a one-way shelf, not a slide. |

A design SHOULD keep every traversable lane ≥ 2 raider diameters (56 u) wide, and
MUST NOT emit ground in the 30°–45° band where a slope reads as neither clearly
walkable nor clearly not.
`[checked: TODO — a metrics assertion over the baked soup]`

### REALM-C-017 — The camera's headroom law
The chase camera fits itself to the space by asking the geometry. Derived from
`CameraRig` (`AimUpBias` 30, `OpenBoomLength` 430 / `OpenPitchDegrees` 40,
`RoofedBoomLength` 250 / `RoofedPitchDegrees` 25, `CeilingClearance` 25,
`MinBoomLength` 120), the ceiling above a raider's feet decides the shot:

| Ceiling above floor | Camera | Register |
| --- | --- | --- |
| ≥ 332 u | full open fit — boom 430, pitch 40° | **OPEN** |
| ≥ 238 u | mid fit | **PRESS** (loose) |
| ≥ 161 u | tightest standing fit — boom 250, pitch 25° | **PRESS** (tight) |
| < 161 u | pinned; the spring arm reels the boom toward 120 | **CRAWL** |

A realm MAY use CRAWL deliberately, and SHOULD keep any single CRAWL stretch
under about three seconds of travel (≈ 660 u). It MUST NOT place a fight there.
*Authority:* [CameraRig.cs](../../WoadRaiders.Client/scripts/world/CameraRig.cs)
`[checked: TODO — sample ceiling height along each realm's route]`

### REALM-C-018 — Packs must not chain-pull
Aggro ranges are Minion 480, Rogue 560, Mage 620 (`EnemyArchetypes`), and social
aggro spreads along sight lines. Two camp markers within **2 × 620 = 1240 u** of
each other MUST either be further apart than that or have a blocker between
their anchors. A realm that ignores this is one pull from waking its own floor.
`[checked: TODO — a camp-separation sweep beside RealmValidator's stranding sweep]`

### REALM-C-019 — Population is capacity, not a cast list
`SpawnDirector` targets `clamp(markers, 4, 40)` live regulars and tops up from
random markers when the target exceeds the marker count. A realm that wants its
authored mix served exactly SHOULD place exactly 40 camp markers.
*Authority:* [SpawnDirector.cs](../../WoadRaiders.Core/SpawnDirector.cs)
`[checked: TODO — assert the baked realm's camp count]`

---

## What a realm may cost

### REALM-C-019a — A realm's population is bounded by sight, not by the wire
Every raider is sent the whole warband plus everything within their realm's
`SightRadius` (`DungeonCatalog`), so what a snapshot costs tracks what a raider
can SEE rather than what the realm holds. A realm SHALL therefore size its
population from its encounters and set `SightRadius` from its own legibility —
never the other way round.

The number to design against is the **chunk count**, not the byte count.
Snapshots ride Unreliable, are split across MTU-sized chunks, and losing any one
chunk discards the whole update — so an unfiltered world does not degrade
gradually as it grows, it drops a rising fraction of ALL its updates. A realm
SHOULD hold a raider's snapshot to **≤ 3 chunks** (≈ 3.4 KB).

Sizing a `SightRadius` by intuition does not work, and the failure is
one-directional: a circular radius on a long, narrow realm covers far more of it
than it looks like it should. Measured on a 7200 × 2800 realm holding 300
enemies — radius 2200 reaches 175 of them (5 chunks, barely better than sending
everything), while 1200 reaches 60 (2 chunks). Use `tools/MeasureSnapshot.cs`.
*Authority:* [WorldSnapshot.cs](../../WoadRaiders.Shared/WorldSnapshot.cs), [DungeonCatalog.cs](../../WoadRaiders.Core/DungeonCatalog.cs)
`[checked: WorldSnapshotTests.Around; tools/MeasureSnapshot.cs for the sizing]`

### REALM-C-020 — A realm is shipped, not sent
A realm's geometry SHALL be a **build artifact the client already carries**, and
the wire SHALL be its fallback, not its delivery. The join carries a
`RealmSnapshot.Digest` of the copy the client holds; the server sends the
geometry only when that does not match its own, exactly.

Three things make this safe, and a realm spec may rely on all of them:
- The client builds **the very packet the server would have sent** from its own
  files and digests that (`Client.LocalRealms`), so a match means identical by
  construction rather than by agreement.
- The navmesh is a build artifact too (`maps/<Realm>.navmesh`, written by
  `tools/GenerateRealm.cs`), so **neither end bakes at run time**. That is a
  stronger determinism guarantee than the wire ever gave: it used to rest on
  "the server bakes once and shares", which still had one machine baking.
- Every failure — no copy, a stale copy, a realm the server was handed with
  `--map`, an unreadable file — falls to sending. The expensive outcome is a
  redundant transfer; the cheap-looking one is a client predicting on different
  stone, which surfaces as rubber-banding nobody can trace.

**Consequently a realm's size is no longer bounded by the join.** What remains:
`.tscn` size, navmesh bake TIME (`REALM-C-021`, unchanged — that is area, not
bytes), BVH query cost, and client memory. A realm spec SHOULD state a join
ceiling only for the **fallback** path — what a client lacking the realm waits
for — and MUST NOT derive its triangle budget from it.
*Authority:* [RealmSnapshot.cs](../../WoadRaiders.Shared/RealmSnapshot.cs), [LocalRealms.cs](../../WoadRaiders.Client/scripts/net/LocalRealms.cs), [GameServer.cs](../../WoadRaiders.Server/GameServer.cs)
`[checked: tools/RealmIdentityProbe.cs — held / stale / absent, over a real socket]`

### REALM-C-020b — The fallback realm is chunked, so its size is a design choice
A realm's fallback geometry SHALL be sent as `Shared.GeometryChunks` — several
reliable messages of `ChunkBytes` (4 MB) each — never as one send.

The reason is that a single fragmented reliable message has a ceiling of
`ushort.MaxValue` fragments times **the negotiated MTU**, so it is not a fixed
number: a peer behind a tunnel or a low-MTU link gets a lower one than the peer
beside it. Measured on loopback (`tools/MeasureReliableLimit.cs`): 62 MB arrived
intact, 64 MB threw `TooBigPacketException`. Chunking removes the dependency
entirely — at 4 MB a chunk is ~7,500 fragments even at a 576-byte path MTU, an
order under the limit — so **how big a realm may be is a decision this project
makes, not a property of a player's network.**

An earlier revision of this rule claimed a 37.1 MB ceiling derived from
`MaxSequence`. That was reasoning from a constant name, and testing disproved
it: the reliable window is 64 packets and sequences wrap harmlessly, so
`MaxSequence` bounds nothing here. It is recorded because the mistake is
instructive — the failure it predicted (silent corruption past a soft limit) was
worse than the truth (a clean refusal at a hard one), and a spec that invents
dangers is as costly as one that misses them.
`[checked: GeometryChunksTests; tools/RealmIdentityProbe.cs rebuilds the realm over a socket]`

### REALM-C-021 — Navmesh cost tracks AREA, not triangles
Benchmarked: 10× the triangles at constant area cost 1.5× bake time; 9× the area
cost 7.9×. Sculpted detail is nearly free; a bigger footprint is not. A realm
spec MUST state a walkable-area target, not only a triangle budget.
`[checked: TODO — record bake ms and walkable area in the generator's summary]`

### REALM-C-022 — Textures are free on the wire
Only geometry crosses the join. Materials, textures, lights, decals, particles
and fog are client-side, and cost repository size and VRAM only. A realm SHOULD
therefore spend generously on surface and light, and frugally on triangles and
floor area — the opposite of the intuitive allocation.
`[judged: art review — the wire budget in REALM-C-020 is the only hard limit]`

### REALM-C-022a — Solid structure offers itself as an occluder
A blocking slab broad enough to hide anything behind it SHALL be accompanied by
an `OccluderInstance3D`, emitted from the slab's OWN bounds. Godot documents
occlusion culling as most effective in exactly this shape of world — many small
indoor rooms — and without it every light, decal and particle system in a
chamber the raider cannot see is still submitted.

Two rules make this safe. The occluder MUST be **conservative** — never larger
than the solid it stands for, or it culls what is really visible — which a box's
own extent satisfies exactly. And it is **derived, never authored**: the editor's
bake step is unavailable headless, but a design that lays a wall already knows
that wall's extent, so there is nothing for an author to get wrong and nothing to
drift. `BoxKit.Structure` does it unasked, above a two-module threshold, so
pillars and parapet caps — which hide too little to pay for the test — are
skipped. Measured: the Crypt emits 57, the open-air Crag none.
*Authority:* [BoxKit.cs](../../WoadRaiders.Client/scripts/tools/BoxKit.cs), [RealmScene.AddOccluder](../../WoadRaiders.Client/scripts/tools/RealmScene.cs)
`[checked: the scene build prints the occluder count; the bake's triangle count is unchanged by them]`

### REALM-C-023 — Third-party assets carry provenance
Any vendored asset MUST be recorded in `docs/ASSETS.md` with source URL, asset
id, licence string, resolution, and the date obtained. Licences on free-asset
sites are mutable; CC0's irrevocability protects only what you can prove you
obtained under it.
`[checked: TODO — spec lint asserts every res:// asset directory has a manifest row]`

### REALM-C-024 — A realm states how big a metre is, and converts
Every realm MUST state its **unit scale** — how many world units make a metre —
and MUST convert any engine quantity measured in world units through it. The
Crypt runs 24 units to the metre (a raider is ~44, a 4 m door is 96).

The engine never knows this, and it is silent about it. Godot's light
attenuation is

    (1 − (d/range)⁴)² · d^(−decay)

with `d` in WORLD UNITS. Under inverse square (`decay = 2`), a light stated at a
value that reads sensibly in a metric project is therefore **24² = 576× too dim**
in the Crypt. Volumetric fog density and length are per-unit in exactly the same
way, and were 25× too dense for the same reason.

What makes this constitutional rather than one realm's bug is HOW it fails. It is
not an error, a missing resource, or a warning. The geometry is all present and
correctly shaded; the realm simply renders black, with every light contributing
less than the ambient meant to fill behind it. Measured before the fix: the
brightest pixel anywhere in the Crypt was 0.021, in every chamber, in every
frame. Nothing in the build log said a word.

The Crag never hit it because a `DirectionalLight3D` has no distance term at all
— so an outdoor realm lit by a sun is immune, and the first indoor realm pays the
whole cost. Any realm adding a positional light or volumetric fog inherits this.
*Authority:* [CryptDesign.Gloom.cs `Candela`](../../WoadRaiders.Client/scripts/tools/CryptDesign.Gloom.cs)
`[checked: CryptSpecTests.No_light_is_stated_in_metres_by_mistake — every light_energy in the scene is at least Metre]`

### REALM-C-025 — The library holds only what the design still makes
A realm's shared mesh library MUST be swept after each build: any `.res` beside
the scene that this build did not write is dead and MUST be deleted. Without the
sweep the directory only grows — renaming a piece, or giving one a parameter it
did not have, leaves the old file on disk, referenced by nothing, complained
about by nothing, and shipped inside the client anyway.
*Authority:* [RealmScene.SweepLibrary](../../WoadRaiders.Client/scripts/tools/RealmScene.cs)
`[checked: the scene build prints what it swept; regeneration is byte-identical, which a stale file cannot be]`

---

## Deferred

Known and deliberately not built, because no realm is yet big enough to justify
them: a tiled navmesh (DotRecast supports it; the current code uses the
single-tile path) and a simplified collision proxy bounded by realm extent
rather than source complexity. An untested lever worth trying before either:
coarsening Recast's `detailSampleDist` / `detailSampleMaxError`, since the
navmesh is ~78% of the Crypt's packet and `SurfaceY` already refines its heights
against the soup.

---

## Review gate

- [ ] Every requirement has an oracle or an explicit `[judged]`.
- [ ] No requirement restates something a realm spec should own.
- [ ] Every `[checked: TODO]` is either scheduled or accepted as a known gap.

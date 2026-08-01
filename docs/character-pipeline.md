# Character pipeline: sketch → GLB as code

Design for a source-controlled, API-driven pipeline that turns reference art into
rigged, animated, game-ready GLB characters — starting with Torga (the non-chibi
Knight replacement), eventually every class and enemy. Everything runs locally
(RTX 5090), every stage is reproducible from committed inputs, and no stage
depends on a web service that can vanish.

**The core idea: a character is a build artifact.** A small committed spec (the
sketch, prompts, seeds, budgets, clip mapping) plus pinned models and committed
scripts deterministically produce `Torga.glb`. Regenerating a character is
`uv run art-pipeline torga`, not an afternoon of clicking.

**Scripting is Python-first.** The game's CI keeps its PowerShell
(`tools/release.ps1` precedent), but everything in `art-pipeline/` is Python:
the orchestrator, bootstrap, validators, and the Blender scripts (which are
Python by nature). The pipeline's own environment is pinned with `uv`
(`pyproject.toml` + committed `uv.lock`) — the same philosophy as
`models.lock.json`, applied to code dependencies.

## Repo layout

```
art-pipeline/
  README.md
  pyproject.toml            # the pipeline as a Python package; uv.lock pins its environment
  models.lock.json          # every AI model: HF repo id, revision (commit), sha256, dest path
  bootstrap.py              # idempotent: ComfyUI portable + custom nodes (pinned commits) + models
  characters/
    torga/
      spec.toml             # name, mesh prefix, height_m, target_tris, seed, clip map
      sketch.jpg            # the friend's reference art (the only human input)
      style_anchor.png      # approved output of stage 1 (committed once accepted)
  workflows/                # ComfyUI workflows in API format (JSON), parameterized via placeholders
    s1_style_anchor.json    #   Qwen-Image-Edit + ControlNet pose → painterly T-pose sheet
    s2_mesh.json            #   TRELLIS.2 geometry+texture (face-count target set here)
    s3_rig.json             #   UniRig / Make-It-Animatable auto-rig
  blender/
    normalize.py            # scale/origin/facing, mesh renaming to <Prefix>_*, material flattening
    assemble_anims.py       # retarget clip library onto the rig, rename clips to contract, export GLB
  clips/                    # the shared animation library (CC0 GLB clips), one skeleton
    library_skeleton.glb    # THE skeleton — every character rigs to this
    Idle.glb  Running_A.glb  Jump_Idle.glb  1H_Melee_Attack_Chop.glb  ...
  validate.py               # the gate: contract checks before anything reaches assets/
  run.py                    # orchestrator: drives ComfyUI API + headless Blender end to end
```

Outputs land in `WoadRaiders.Client/assets/characters/<Name>.glb` and are also
committed (players shouldn't need a GPU pipeline to build the game — the
pipeline is how assets are *made*, not how the game is *built*).

## Stage graph

```
sketch.jpg ─→ [S1 ComfyUI: style anchor] ─→ style_anchor.png   (human approves once, commit)
style_anchor.png ─→ [S2 ComfyUI: TRELLIS.2] ─→ raw mesh GLB (face target ~30k set in workflow)
raw mesh ─→ [S3 ComfyUI: UniRig/MIA rig] ─→ rigged GLB
rigged GLB ─→ [S4 Blender headless: normalize + assemble] ─→ candidate GLB
candidate ─→ [S5 validate.py gate] ─→ assets/characters/<Name>.glb
```

### S1 — style anchor (nano banana, full stop — revised 2026-08-01)

**All 2D character authoring happens in nano banana**: the anchor from the
friend's sketch, the back view for multi-image generation, and roster
consistency (its character-consistency across edits beat local Qwen in
every session comparison). The picked renders are committed
(`style_anchor.png`, `back_view.png`) — approved intermediates are
committed (rule 5), so downstream stages never depend on the external tool.

Qwen exits the character pipeline entirely. Its old jobs are obsolete:
pose surgery (Meshy enforces A/T-pose at generation), de-cel shading (a
TRELLIS.2-era workaround), style-ref consistency (nano banana's is
better). The local Qwen/ComfyUI stack remains installed for NON-character
work — realm dressing, prop texturing, 2D game assets — per the realm AI
notes. The S1 Qwen workflows and the OpenPose template stay in the repo
as archived tooling, out of the character path.

The anchor image is what the mesh provider propagates into textures, so
this stage IS the style enforcement point. Current direction: cel-shaded,
bold outlines, flat color (supersedes the earlier soft-gouache notes).
Anchor requirements for Meshy input: full body, A- or T-pose, empty
hands, white background, front + back pair with matching pose and style.

### S2 — mesh (provider-pluggable; Meshy primary as of 2026-08-01)

**The 2026-08-01 verdict**: eight measured iterations of local TRELLIS.2
(multi-view conditioning, projection baking, head kitbashing, LOD baking)
produced geometry that was fine and textures that never crossed the bar —
photographic-organic mush vs the game's clean stylized color. Commercial
stacks (integrated gen → quad retopo → texture tuning) are years ahead for
finished characters: Meshy-6 won 63.8% of 1,331 senior-artist blind votes
vs Tripo 3.1, has A/T-pose control, quad topology, multi-image (front+back)
input, a 600+ clip animation library with FREE rig/animate, full-GLB
multi-clip export, and a REST API (+MCP server). Pro $20/mo ≈ 33 full
characters. Tripo's API (~$0.20-0.25/asset) stays the creature option
(quadruped/serpentine/avian skeletons).

So S2 becomes a **provider abstraction** (`provider = "meshy" | "trellis2"`
in the spec): Meshy for hero/roster finals via its Multi-Image API — fed by
OUR committed anchors and back views, which remain the style-consistency
mechanism no cloud tool provides. TRELLIS.2 stays installed as the
zero-cost draft/prop path. Committed outputs (reproducibility rule 5)
matter MORE with metered generation.

**Where the local pipeline earns its keep** (the honest division):
2D authoring & roster consistency (anchors, pose contract, back views —
now Meshy inputs); API orchestration + character-as-code; the game
contract layer (clip renames to `Idle`/`Running_A`/…, `Knight_*` mesh
naming, scale/facing, validate.py — no vendor will ever do this);
assembly (weapons, LODs, Godot import); free local drafting; and
provider portability as models improve.

### S2 adoption notes (from RunComfy's advanced TRELLIS.2 workflow, researched 2026-07-31)

Their workflow runs a different wrapper, but three techniques transfer to ours:

1. **Multi-view generation** — front + back conditioning via our pack's native
   `Trellis2MultiViewImageToShape` (per-view image+mask, `blend_temperature`).
   The back view comes from Qwen's pose-preserving edit of the anchor. Kills
   the hallucinated back side of single-image generation.
2. **LOD baking from voxel data** — their batch-simplify pattern: from one
   generation's voxelgrid, bake each LOD (hero-res, game-res) via
   simplify → UV unwrap → RasterizePBR, instead of decimating a baked mesh
   in Blender (which shreds UVs). Changes the M3 plan: Blender normalizes
   and assembles, but LOD textures bake in-graph.
3. **Mesh hygiene before rigging** — fill holes + fix normals (GeomPack has
   the nodes) ahead of auto-rigging; optional quad remesh for deformation.

Their Qwen-generated normal-map guide images are noted but not adopted.

**ComfyUI-UniRig** (same author + comfy-env pattern as our TRELLIS2 node,
bundles Blender internally) running **Make-It-Animatable** for humanoids — both
MIT. Rigs a mesh in under a second, headlessly.

**Interactive path: ComfyUI-mesh2motion.** Mesh2Motion is an open-source (CC0)
Mixamo alternative — humanoid *and* quadruped/bird/serpent skeletons, its own
CC0 animation library, and multi-animation single-GLB export. The
`ComfyUI-mesh2motion` node embeds its editor as a dialog: right-click any 3D
node (Load3D/SaveGLB/…), place the skeleton by hand, bind, pick animations, and
the rigged+animated GLB round-trips back into the graph. Manual bone placement
means it can't be the automated path, but it is three better things: the
**rescue path** when auto-rigging mangles a character, the **QC path** for
eyeballing a rig with animations before committing, and the likely **v1 path**
for Torga — a working in-game character while the M3 automation is still being
built. Its non-humanoid skeletons also make it the current best answer for
future creature enemies. (AccuRIG 2.0 remains a last-resort external tool.)

### S4 — headless Blender (the deterministic assembly layer)

`blender -b -P <script>.py -- --spec spec.toml` — fully scriptable, no GUI, and
the scripts are ordinary committed Python. Blender is pinned by version in
`bootstrap.py` (winget/scoop). Two scripts:

- **normalize.py** — feet at origin, face +Z (glTF forward), height from
  `spec.toml`, mesh objects renamed to `<MeshPrefix>_*` (the `CharacterLoadout`
  visibility contract), and **painterly material flattening**: metallic → 0,
  roughness → high, keep albedo. TRELLIS.2 emits photo-PBR metallic/roughness;
  the hand-painted direction wants albedo-dominant honest-light materials.
- **assemble_anims.py** — retargets the committed clip library onto the
  character's rig, renames clips to the game contract, exports final GLB with
  one glTF animation per clip.

### S5 — validation gate (the game contract, executable)

`validate.py` (pygltflib or Blender-python) fails the build unless:

- clips exactly match the contract: `Idle`, `Running_A`, `Jump_Idle`, plus the
  class attack clip from `spec.toml` (`1H_Melee_Attack_Chop` for Torga)
- `Running_A` root stays in place (max root XZ drift < ε — catches root motion)
- every mesh name starts with `MeshPrefix` or is a declared weapon mesh
- height within tolerance of `spec.toml`, feet at y≈0, faces +Z
- triangle count ≤ budget; texture ≤ 2048; single skeleton, all clips target it

This is the piece that makes "replace all the chibi models" safe: the contract
lives in one place and every generated character proves compliance before it
can touch `assets/`.

## The shared skeleton (the Mixamo replacement, part 2)

One skeleton (`clips/library_skeleton.glb`) serves every character, so the clip
library is built once:

- **Base library**: Quaternius Universal Animation Library 1+2 — CC0, glTF,
  250+ humanoid clips. Committed directly to `clips/` (CC0 = no license risk).
- **Second CC0 source**: Mesh2Motion's animation library (Blender source rigs
  on its GitHub) — harvestable into `clips/`. Pick ONE primary skeleton for the
  library to avoid double-retargeting; the other source's clips get retargeted
  once, offline, when imported.
- **Custom motion, later**: text-to-motion inside ComfyUI — **Kimodo**
  (NVIDIA, repo Apache-2.0; verify weight license before shipping output) or
  **HY-Motion 1.0** (Tencent — assume the EU/UK/KR exclusion until proven
  otherwise, same as Hunyuan3D; check before use). Video mocap via
  ComfyUI-Video2MotionCapture (GVHMR) is the "act it out with a phone" option.
- Mixamo's website is deliberately **not** in the loop: it's unmaintained,
  unscriptable (no API), and everything it provided now has a local equivalent.

## Driving ComfyUI: API for the pipeline, MCP for the conversation

**API (the backbone, in `run.ps1`)**: ComfyUI's native HTTP interface —
`POST /prompt` with API-format workflow JSON (seeds/paths patched in from
`spec.toml`), progress via the `/ws` websocket, results via `/history` and
`/view`. Server launched headless (`--port 8188 --disable-auto-launch`). This
is plain HTTP — `run.py` uses `httpx` + `websockets`, no SDK dependency,
CI-able later on a self-hosted GPU runner.

**MCP (the interactive layer)**: [artokun/comfyui-mcp] — local-first MCP server
+ Claude Code plugin (~108 tools: run workflows, edit the live graph, manage
models/nodes). Register it in `.claude/settings` so future sessions can drive
the live ComfyUI instance conversationally — "regenerate Torga's anchor with a
narrower tabard" — while the committed workflows stay the source of truth. MCP
is for *authoring and debugging* the pipeline; the API scripts are for
*running* it.

## Reproducibility rules

1. `models.lock.json` pins every model to a HF revision + sha256; bootstrap
   verifies hashes. No `latest`, ever.
2. Custom nodes pinned to commit SHAs in `bootstrap.ps1` (git clone + checkout).
3. Workflows stored in **API format** only (UI-format JSON drifts with frontend
   versions; API format is the execution contract).
4. Seeds live in `spec.toml`. A character rebuild with an unchanged spec is
   byte-comparable (modulo GPU nondeterminism — accept "visually identical").
5. Approved intermediates (`style_anchor.png`) are committed: stage 2+ can be
   rebuilt even if the image model is swapped later.
6. The pipeline's Python environment is pinned by `uv.lock`; the ComfyUI side
   is pinned by `bootstrap.py` (portable version + node commit SHAs). The two
   never share an interpreter — ComfyUI keeps its embedded Python.

## Licensing snapshot (verified 2026-07-31)

| Piece | License | Ship-safe? |
|---|---|---|
| TRELLIS.2 + weights | MIT | yes |
| Qwen-Image-Edit-2511 | Apache 2.0 | yes |
| UniRig / Make-It-Animatable | MIT / MIT | yes |
| Quaternius UAL 1+2 clips | CC0 | yes |
| Kimodo (NVIDIA) | repo Apache-2.0 | verify weight license |
| HY-Motion 1.0 (Tencent) | Hunyuan-style | assume EU/UK/KR exclusion — verify |
| Mesh2Motion app + its animations | CC0 / CC0 | yes |
| Blender / ComfyUI | GPL (tool) / GPL (tool) | outputs unencumbered |

## Build-out milestones

- **M1** — commit this doc + `art-pipeline/` package skeleton (`pyproject.toml`
  + `uv.lock`); export the TRELLIS.2 workflow in API format; `run.py` drives
  S2 alone (image in → GLB out).
- **M1.5 (optional fast track)** — rig + animate the first TRELLIS.2 Torga
  mesh interactively via ComfyUI-mesh2motion → validate → in-game. A playable
  non-chibi knight before any of M2/M3 exists.
- **M2** — S1: Qwen-Image-Edit-2511 + ControlNet Union in ComfyUI
  (`models.lock` grows); fixed T-pose keypoint template; Torga style anchor
  approved and committed.
- **M3** — S3 + S4 + S5: UniRig/MIA node, the two Blender scripts, the
  validator; Quaternius clips committed; first full `run.py torga` → GLB that
  passes the gate → in-game on the class card.
- **M4** — MCP registration, `bootstrap.py` hardening (clean-machine test),
  and the second character (Rogue) to prove the "hours not days" claim.

## Open decisions

1. **Style LoRA** — once 3–4 approved anchors exist, train a small WoadRaiders
   painterly LoRA for Qwen-Image so style consistency stops depending on prompt
   wording. Deferred until there's data.
2. **Clip retarget mechanics** — which skeleton is THE skeleton: UAL's, MIA's
   output rig, or Mesh2Motion's humanoid rig. Whichever wins, the other
   sources' clips get retargeted onto it once at import time. Decide in M3
   when we see MIA's actual bone layout.
3. **Weapons** — separate small TRELLIS.2 generations (axe, shield) attached to
   hand bones in `assemble_anims.py`, per the `CharacterLoadout` separate-mesh
   contract. KayKit weapons remain the stopgap.
```

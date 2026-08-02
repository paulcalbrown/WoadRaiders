"""Pipeline orchestrator.

    uv run art-pipeline torga --stage s1              # sketch -> anchor candidates
    uv run art-pipeline torga --stage s2 --image p.png # image -> textured mesh GLB

Reads characters/<name>/spec.toml, patches the committed API-format workflow
for the stage (seeds, budgets, prompts), queues it on the local ComfyUI
server, and lands outputs in characters/<name>/build/.
"""

from __future__ import annotations

import argparse
import copy
import json
import sys
import time
import tomllib
from pathlib import Path

from .comfy import ComfyClient, ComfyError, find_glb_outputs

ROOT = Path(__file__).resolve().parent.parent  # art-pipeline/


def load_toml(path: Path) -> dict:
    with path.open("rb") as f:
        return tomllib.load(f)


def connect(pipeline: dict) -> ComfyClient:
    comfy = ComfyClient(pipeline["comfy"]["url"])
    if not comfy.alive():
        sys.exit(
            f"ComfyUI is not responding at {pipeline['comfy']['url']} — "
            "start it (Desktop shortcut / run_nvidia_gpu.bat) and retry."
        )
    return comfy


def patch_s1(
    workflow: dict,
    spec: dict,
    sketch_name: str,
    pose_name: str,
    style_name: str | None,
    seed: int,
) -> dict:
    """Bind one character's spec to the committed S1 (style anchor) workflow."""
    wf = copy.deepcopy(workflow)
    s1 = spec["s1"]
    wf["1"]["inputs"]["image"] = sketch_name
    wf["2"]["inputs"]["image"] = pose_name
    if style_name:
        wf["3"]["inputs"]["image"] = style_name
    else:
        # No style reference: drop the loader and its encoder hookups. Qwen
        # treats reference images as content, so an absent ref beats a wrong one.
        del wf["3"]
        for node in ("30", "31"):
            del wf[node]["inputs"]["image3"]
    wf["30"]["inputs"]["prompt"] = s1["prompt"]
    wf["31"]["inputs"]["prompt"] = s1.get("negative", "")
    # Lightning trades text authority for speed; anchors can afford slow.
    wf["15"]["inputs"]["strength_model"] = 1 if s1.get("lightning", True) else 0
    wf["40"]["inputs"]["seed"] = seed
    wf["40"]["inputs"]["steps"] = s1["steps"]
    wf["40"]["inputs"]["cfg"] = s1["cfg"]
    wf["42"]["inputs"]["filename_prefix"] = f"{spec['character']['name'].lower()}_anchor"
    return wf


def find_image_outputs(history_entry: dict) -> list[tuple[str, str]]:
    found = []
    for node_output in history_entry.get("outputs", {}).values():
        for item in node_output.get("images", []):
            if item.get("type") == "output":
                found.append((item["filename"], item.get("subfolder", "")))
    return found


def run_s1(character: str, seeds: int) -> list[Path]:
    """Generate style-anchor candidates: sketch + fixed T-pose template -> N images."""
    char_dir = ROOT / "characters" / character
    spec = load_toml(char_dir / "spec.toml")
    pipeline = load_toml(ROOT / "pipeline.toml")
    comfy = connect(pipeline)

    workflow = json.loads((ROOT / "workflows" / "s1_anchor.json").read_text())
    sketch = comfy.upload_image(char_dir / spec["s1"]["sketch"])
    pose = comfy.upload_image(ROOT / "workflows" / "tpose_openpose.png")
    # Style refs are game-wide (art-pipeline/style/), resolved from the root.
    style_ref = spec["s1"].get("style_ref")
    style = comfy.upload_image(ROOT / style_ref) if style_ref else None

    out_dir = char_dir / "build" / "anchors"
    out_dir.mkdir(parents=True, exist_ok=True)
    results: list[Path] = []
    base_seed = spec["s1"]["seed"]
    for i in range(seeds):
        seed = base_seed + i
        patched = patch_s1(workflow, spec, sketch, pose, style, seed)
        print(f"[S1] {character}: candidate {i + 1}/{seeds} (seed {seed})")
        started = time.monotonic()
        entry = comfy.wait(comfy.queue(patched))
        for filename, subfolder in find_image_outputs(entry):
            dest = comfy.download_output(
                filename, subfolder, out_dir / f"anchor_seed{seed}.png"
            )
            results.append(dest)
            print(f"[S1]   {time.monotonic() - started:.0f}s -> {dest}")
    if not results:
        raise ComfyError("S1 finished without producing images")
    print(f"[S1] review the candidates in {out_dir}; commit the winner as "
          f"characters/{character}/{spec['s2']['style_anchor']}")
    return results


def patch_s2(workflow: dict, spec: dict, image_name: str, back_name: str | None) -> dict:
    """Bind one character's spec to the committed S2 workflow."""
    wf = copy.deepcopy(workflow)
    s2 = spec["s2"]
    name = spec["character"]["name"].lower()
    wf["1"]["inputs"]["image"] = image_name
    if back_name:
        wf["2"]["inputs"]["image"] = back_name
    else:
        # Multi-view node treats every non-front view as optional.
        del wf["2"], wf["6"]
        del wf["82"]["inputs"]["back_image"], wf["82"]["inputs"]["back_mask"]
    wf["68"]["inputs"]["resolution"] = str(s2["mesh_resolution"])
    wf["82"]["inputs"]["seed"] = s2["seed"]
    wf["83"]["inputs"]["seed"] = s2["seed"]
    wf["83"]["inputs"]["tex_sampling_steps"] = s2.get("tex_steps", 12)
    wf["83"]["inputs"]["tex_guidance_strength"] = s2.get("tex_guidance", 3.0)
    wf["83"]["inputs"]["tex_guidance_rescale"] = s2.get("tex_guidance_rescale", 0.2)
    wf["97"]["inputs"]["target_face_count"] = s2["target_tris"]
    wf["98"]["inputs"]["texture_size"] = s2["texture_size"]
    wf["100"]["inputs"]["target_face_count"] = s2.get("game_tris", 30000)
    wf["86"]["inputs"]["filename_prefix"] = name
    wf["103"]["inputs"]["filename_prefix"] = f"{name}_game"
    return wf


def run_s2(character: str, image_override: Path | None) -> Path:
    char_dir = ROOT / "characters" / character
    spec = load_toml(char_dir / "spec.toml")
    pipeline = load_toml(ROOT / "pipeline.toml")

    image = image_override or (char_dir / spec["s2"]["style_anchor"])
    if not image.is_file():
        sys.exit(
            f"input image not found: {image}\n"
            "No approved style anchor yet? Run --stage s1, pick a candidate, "
            "commit it — or pass one explicitly with --image."
        )

    comfy = connect(pipeline)

    workflow = json.loads((ROOT / "workflows" / "s2_mesh.json").read_text())
    uploaded = comfy.upload_image(image)
    back_path = char_dir / spec["s2"]["back_view"] if spec["s2"].get("back_view") else None
    back = comfy.upload_image(back_path) if back_path and back_path.is_file() else None
    patched = patch_s2(workflow, spec, uploaded, back)

    print(f"[S2] {character}: image={image.name} back={'yes' if back else 'no'} "
          f"seed={spec['s2']['seed']} faces<={spec['s2']['target_tris']} "
          f"game<={spec['s2'].get('game_tris', 30000)} res={spec['s2']['mesh_resolution']}")
    started = time.monotonic()
    wall_started = time.time()
    prompt_id = comfy.queue(patched)
    print(f"[S2] queued {prompt_id}")
    comfy.wait(prompt_id)
    elapsed = time.monotonic() - started

    # The export nodes don't reliably report through history; take each
    # prefix's newest file from ComfyUI's output directory on disk.
    build = char_dir / "build"
    build.mkdir(parents=True, exist_ok=True)
    out_dir = Path(pipeline["comfy"]["root"]) / "ComfyUI" / "output"
    name = character.lower()
    results = []
    for prefix, out_name in [
        (f"{name}_game", f"{character}_game.glb"),
        (name, f"{character}_mesh.glb"),
    ]:
        candidates = sorted(
            (p for p in out_dir.glob(f"{prefix}*.glb")
             if p.stat().st_mtime >= wall_started - 60
             and (prefix.endswith("_game") or "_game" not in p.name)),
            key=lambda p: p.stat().st_mtime,
        )
        if not candidates:
            raise ComfyError(f"no {prefix}*.glb produced in {out_dir}")
        dest = build / out_name
        dest.write_bytes(candidates[-1].read_bytes())
        results.append(dest)
        print(f"[S2] {out_name}: {dest.stat().st_size / 1e6:.1f} MB")
    print(f"[S2] done in {elapsed:.0f}s")
    return results[-1]


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="art-pipeline",
        description="WoadRaiders character pipeline (docs/character-pipeline.md)",
    )
    parser.add_argument("character", help="character folder under characters/")
    parser.add_argument(
        "--stage",
        choices=["s1", "s2", "rig", "ingest"],
        default="s2",
        help="s1: anchor candidates; s2: image -> mesh (default); "
        "rig: MIA auto-rig the newest inbox GLB; "
        "ingest: Meshy GLB -> contract-passing game asset",
    )
    parser.add_argument("--dry-run", action="store_true", help="ingest: inventory only")
    parser.add_argument(
        "--image",
        type=Path,
        help="s2 input override (otherwise the spec's committed style anchor)",
    )
    parser.add_argument(
        "--seeds",
        type=int,
        default=4,
        help="s1: number of anchor candidates to generate (default 4)",
    )
    args = parser.parse_args()

    try:
        if args.stage == "s1":
            run_s1(args.character, args.seeds)
        elif args.stage == "rig":
            from .rig import run_rig

            run_rig(args.character)
        elif args.stage == "ingest":
            from .ingest import run_ingest

            run_ingest(args.character, args.dry_run)
        else:
            run_s2(args.character, args.image)
    except ComfyError as e:
        sys.exit(f"pipeline failed: {e}")


if __name__ == "__main__":
    main()

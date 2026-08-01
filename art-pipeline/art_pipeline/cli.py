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


def patch_s2(workflow: dict, spec: dict, image_name: str) -> dict:
    """Bind one character's spec to the committed S2 workflow."""
    wf = copy.deepcopy(workflow)
    s2 = spec["s2"]
    wf["1"]["inputs"]["image"] = image_name
    wf["68"]["inputs"]["resolution"] = str(s2["mesh_resolution"])
    wf["82"]["inputs"]["seed"] = s2["seed"]
    wf["83"]["inputs"]["seed"] = s2["seed"]
    wf["83"]["inputs"]["tex_sampling_steps"] = s2.get("tex_steps", 12)
    wf["97"]["inputs"]["target_face_count"] = s2["target_tris"]
    wf["98"]["inputs"]["texture_size"] = s2["texture_size"]
    wf["86"]["inputs"]["filename_prefix"] = spec["character"]["name"].lower()
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
    patched = patch_s2(workflow, spec, uploaded)

    print(f"[S2] {character}: image={image.name} seed={spec['s2']['seed']} "
          f"faces<={spec['s2']['target_tris']} res={spec['s2']['mesh_resolution']}")
    started = time.monotonic()
    prompt_id = comfy.queue(patched)
    print(f"[S2] queued {prompt_id}")
    entry = comfy.wait(prompt_id)
    elapsed = time.monotonic() - started

    build = char_dir / "build"
    glbs = find_glb_outputs(entry)
    if glbs:
        filename, subfolder = glbs[-1]
        dest = comfy.download_output(filename, subfolder, build / f"{character}_mesh.glb")
    else:
        # The export node may not report through history; fall back to the
        # newest matching file in ComfyUI's output directory on disk.
        out_dir = Path(pipeline["comfy"]["root"]) / "ComfyUI" / "output"
        prefix = character.lower()
        candidates = sorted(
            out_dir.glob(f"{prefix}*.glb"), key=lambda p: p.stat().st_mtime
        )
        if not candidates:
            raise ComfyError(
                f"prompt finished but no GLB found via history or {out_dir}"
            )
        build.mkdir(parents=True, exist_ok=True)
        dest = build / f"{character}_mesh.glb"
        dest.write_bytes(candidates[-1].read_bytes())

    size_mb = dest.stat().st_size / 1e6
    print(f"[S2] done in {elapsed:.0f}s -> {dest} ({size_mb:.1f} MB)")
    return dest


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="art-pipeline",
        description="WoadRaiders character pipeline (docs/character-pipeline.md)",
    )
    parser.add_argument("character", help="character folder under characters/")
    parser.add_argument(
        "--stage",
        choices=["s1", "s2"],
        default="s2",
        help="s1: style anchor candidates; s2: image -> mesh (default)",
    )
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
        else:
            run_s2(args.character, args.image)
    except ComfyError as e:
        sys.exit(f"pipeline failed: {e}")


if __name__ == "__main__":
    main()

"""Pipeline orchestrator. M1 scope: stage S2 only (image -> textured mesh GLB).

    uv run art-pipeline torga --image path/to/anchor.png

Reads characters/<name>/spec.toml, patches the committed API-format workflow
(seed, face target, texture size, resolution, image), queues it on the local
ComfyUI server, and lands the GLB in characters/<name>/build/.
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


def patch_s2(workflow: dict, spec: dict, image_name: str) -> dict:
    """Bind one character's spec to the committed S2 workflow."""
    wf = copy.deepcopy(workflow)
    s2 = spec["s2"]
    wf["1"]["inputs"]["image"] = image_name
    wf["68"]["inputs"]["resolution"] = str(s2["mesh_resolution"])
    wf["82"]["inputs"]["seed"] = s2["seed"]
    wf["83"]["inputs"]["seed"] = s2["seed"]
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
            "No approved style anchor yet? Pass one explicitly with --image."
        )

    comfy = ComfyClient(pipeline["comfy"]["url"])
    if not comfy.alive():
        sys.exit(
            f"ComfyUI is not responding at {pipeline['comfy']['url']} — "
            "start it (Desktop shortcut / run_nvidia_gpu.bat) and retry."
        )

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
        "--image",
        type=Path,
        help="input image override (otherwise the spec's committed style anchor)",
    )
    args = parser.parse_args()

    try:
        run_s2(args.character, args.image)
    except ComfyError as e:
        sys.exit(f"pipeline failed: {e}")


if __name__ == "__main__":
    main()

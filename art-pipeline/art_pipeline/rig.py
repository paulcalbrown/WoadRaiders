"""Rig stage: hands-free MIA auto-rig for the newest inbox mesh.

    uv run art-pipeline warrior --stage rig

Takes the newest GLB from characters/<name>/<source_dir>/ (the Meshy inbox),
loads it by ABSOLUTE PATH via GeomPackLoadMeshPath (UniRig's own loader
validates against a file list frozen at install-time scan — a known trap),
runs Make-It-Animatable auto-rigging headlessly, and lands the rigged
outputs in characters/<name>/build/:

    <name>_rigged.fbx   — mixamo-skeleton FBX (input for animation retarget)
    <name>_rigged.glb   — same rig as GLB (quick inspection)

Animations come next (UniRigApplyAnimation with clips from
ComfyUI/input/animation_templates/mixamo/, or Mesh2Motion interactively);
then ingest enforces the contract.
"""

from __future__ import annotations

import json
import shutil
import sys
import time
from pathlib import Path

import httpx

from .cli import ROOT, load_toml


def run_rig(character: str) -> None:
    char_dir = ROOT / "characters" / character
    spec = load_toml(char_dir / "spec.toml")
    pipeline = load_toml(ROOT / "pipeline.toml")
    url = pipeline["comfy"]["url"].rstrip("/")
    comfy_root = Path(pipeline["comfy"]["root"])

    src_dir = char_dir / spec["ingest"]["source_dir"]
    prop_files = {p["file"] for p in spec["ingest"].get("props", {}).values()}
    candidates = [p for p in src_dir.glob("*.glb") if p.name not in prop_files]
    if not candidates:
        raise SystemExit(f"[rig] no GLB in {src_dir} — drop your Meshy download there")
    src = max(candidates, key=lambda p: p.stat().st_mtime)
    print(f"[rig] source: {src.name} ({src.stat().st_size/1e6:.1f} MB)")

    name = spec["character"]["name"].lower()
    wf = {
        "1": {"class_type": "GeomPackLoadMeshPath",
              "inputs": {"file_path": str(src.resolve())}},
        "2": {"class_type": "MIALoadModel",
              "inputs": {"precision": "fp32", "attn_backend": "auto"}},
        "3": {"class_type": "MIAAutoRig",
              "inputs": {"trimesh": ["1", 0], "model": ["2", 0],
                         "fbx_name": f"{name}_rig", "no_fingers": False,
                         "use_normal": False, "reset_to_rest": True}},
        "4": {"class_type": "UniRigPreviewRiggedMesh",
              "inputs": {"fbx_output_path": ["3", 0]}},
    }
    client = httpx.Client(base_url=url, timeout=30.0)
    r = client.post("/prompt", json={"prompt": wf, "client_id": "art-pipeline-rig"})
    if r.status_code != 200:
        raise SystemExit(f"[rig] queue rejected: {r.text[:800]}")
    prompt_id = r.json()["prompt_id"]
    print(f"[rig] queued {prompt_id} (first run downloads MIA weights)")

    deadline = time.monotonic() + 1200
    entry = None
    while time.monotonic() < deadline:
        time.sleep(5)
        entry = client.get(f"/history/{prompt_id}").json().get(prompt_id)
        if entry:
            break
    if not entry:
        raise SystemExit("[rig] timed out waiting for the rig job")
    status = entry.get("status", {})
    if status.get("status_str") == "error":
        raise SystemExit(f"[rig] job failed: {json.dumps(status)[:1200]}")

    out_dir = comfy_root / "ComfyUI" / "output"
    build = char_dir / "build"
    build.mkdir(parents=True, exist_ok=True)
    found = []
    for ext in ("fbx", "glb"):
        matches = sorted(out_dir.rglob(f"{name}_rig*.{ext}"), key=lambda p: p.stat().st_mtime)
        if matches:
            dest = build / f"{character}_rigged.{ext}"
            shutil.copyfile(matches[-1], dest)
            found.append(dest)
            print(f"[rig] -> {dest} ({dest.stat().st_size/1e6:.1f} MB)")
    if not found:
        raise SystemExit(f"[rig] job succeeded but no {name}_rig* outputs in {out_dir}")
    print("[rig] done — next: animations (UniRigApplyAnimation or Mesh2Motion), then ingest")


if __name__ == "__main__":
    run_rig(sys.argv[1] if len(sys.argv) > 1 else "warrior")

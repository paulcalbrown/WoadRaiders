"""Animate stage: apply the repo clip library to the rigged character.

    uv run art-pipeline warrior --stage animate

Takes build/<name>_rigged.fbx (the rig stage's corrected, grounded output)
and every clip in clips/mixamo/ (filename = contract clip name), and
produces build/<name>_animated.glb — the multi-clip asset ingest consumes.
Runs entirely in the UniRig env's Blender; ComfyUI is not involved.
"""

from __future__ import annotations

import subprocess
import sys
from pathlib import Path

from .cli import ROOT, load_toml

CLIPS_DIR = ROOT / "clips" / "mixamo"


def collect_clips(char_dir: Path, spec: dict) -> list[Path]:
    """Layered library: shared clips/mixamo/ + characters/<name>/clips/.

    A character file with the same stem overrides the shared one; extras on
    either side ride along. [animate].exclude in the spec drops shared clips
    a character shouldn't have.
    """
    layered: dict[str, tuple[str, Path]] = {
        p.stem: ("shared", p) for p in sorted(CLIPS_DIR.glob("*.fbx"))
    }
    own_dir = char_dir / "clips"
    for p in sorted(own_dir.glob("*.fbx")) if own_dir.exists() else []:
        layered[p.stem] = ("character", p)
    for name in spec.get("animate", {}).get("exclude", []):
        layered.pop(name, None)
    for stem, (source, _) in sorted(layered.items()):
        print(f"[animate]   {stem:<12} ({source})")
    return [p for _, p in layered.values()]


def run_animate(character: str) -> Path:
    char_dir = ROOT / "characters" / character
    spec = load_toml(char_dir / "spec.toml")
    pipeline = load_toml(ROOT / "pipeline.toml")
    rigged = char_dir / "build" / f"{character}_rigged.fbx"
    if not rigged.exists():
        raise SystemExit(f"[animate] {rigged} missing — run --stage rig first")
    clips = collect_clips(char_dir, spec)
    if not clips:
        raise SystemExit(
            f"[animate] no clips in {CLIPS_DIR} or {char_dir / 'clips'} — "
            "download Mixamo clips (FBX, Without Skin, 30 fps; "
            "filename = contract name) first"
        )

    rig_cfg = pipeline.get("rig", {})
    pixi, manifest = rig_cfg.get("pixi"), rig_cfg.get("unirig_manifest")
    if not (pixi and manifest):
        raise SystemExit("[animate] pipeline.toml [rig] pixi/unirig_manifest not set")

    out = char_dir / "build" / f"{character}_animated.glb"
    script = Path(__file__).parent / "bpy_scripts" / "apply_clips.py"
    r = subprocess.run(
        [pixi, "run", "--manifest-path", manifest, "python", str(script),
         str(rigged), str(out)] + [str(c) for c in clips],
        capture_output=True, text=True, timeout=900,
    )
    for line in (r.stdout + r.stderr).strip().splitlines()[-4:]:
        print(f"[animate]   {line}")
    if r.returncode != 0 or not out.exists():
        raise SystemExit(f"[animate] failed (exit {r.returncode})")
    print(f"[animate] -> {out} ({out.stat().st_size/1e6:.1f} MB) — next: --stage ingest")
    return out


if __name__ == "__main__":
    run_animate(sys.argv[1] if len(sys.argv) > 1 else "warrior")

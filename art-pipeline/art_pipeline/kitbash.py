"""Head kitbash: give the hero a face the full-figure generation cannot.

A single TRELLIS.2 pass spreads its latent over the whole figure, leaving the
head a smear. This stage crops the anchor's head, has Qwen repaint it with
volumetric shading (a flat cel face reads as a 2D picture and generates a
card; shading gives the model depth evidence), generates a head-only mesh at
full latent resolution, and merges it onto the body at the collar, where
chunky geometry hides the seam.

    uv run python -m art_pipeline.kitbash torga

Requires the body mesh (S2 + reproject) in build/. Produces
build/<name>_kitbash.glb — body + high-detail head as two submeshes.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image

from .cli import ROOT, load_toml, connect
from .comfy import find_glb_outputs


def crop_head(spec: dict, char_dir: Path) -> Path:
    h = spec["head"]
    a = Image.open(char_dir / spec["s2"]["style_anchor"])
    w, hh = a.size
    l, t, r, b = h["crop"]  # fractions of the anchor
    crop = a.crop((int(l * w), int(t * hh), int(r * w), int(b * hh)))
    cw, ch = crop.size
    canvas = Image.new("RGB", (int(ch * 1.15), int(ch * 1.15)), "white")
    canvas.paste(crop, ((canvas.size[0] - cw) // 2, (canvas.size[1] - ch) // 2))
    dest = char_dir / "build" / "head_crop.png"
    dest.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(dest)
    return dest


def shade_head(spec: dict, char_dir: Path, comfy) -> Path:
    """Qwen repaint: same head, volumetric shading — depth cues for TRELLIS."""
    h = spec["head"]
    img = comfy.upload_image(char_dir / "build" / "head_crop.png")
    wf = json.loads((ROOT / "workflows" / "s1_anchor.json").read_text())
    for node in ("1", "2", "3"):
        wf[node]["inputs"]["image"] = img
    wf["30"]["inputs"]["prompt"] = h["shade_prompt"]
    wf["40"]["inputs"]["seed"] = h["seed"]
    wf["42"]["inputs"]["filename_prefix"] = "head_shaded"
    entry = comfy.wait(comfy.queue(wf))
    from .cli import find_image_outputs

    dest = char_dir / "build" / "head_shaded.png"
    for filename, subfolder in find_image_outputs(entry):
        comfy.download_output(filename, subfolder, dest)
    return dest


def generate_head(spec: dict, char_dir: Path, comfy, pipeline: dict) -> Path:
    h = spec["head"]
    img = comfy.upload_image(char_dir / "build" / "head_shaded.png")
    wf = json.loads((ROOT / "workflows" / "s2_mesh.json").read_text())
    wf["1"]["inputs"]["image"] = img
    wf["68"]["inputs"]["resolution"] = str(spec["s2"]["mesh_resolution"])
    wf["82"]["inputs"]["seed"] = h["seed"]
    wf["83"]["inputs"]["seed"] = h["seed"]
    wf["83"]["inputs"]["tex_sampling_steps"] = spec["s2"].get("tex_steps", 12)
    wf["83"]["inputs"]["tex_guidance_strength"] = spec["s2"].get("tex_guidance", 3.0)
    wf["83"]["inputs"]["tex_guidance_rescale"] = spec["s2"].get("tex_guidance_rescale", 0.2)
    wf["97"]["inputs"]["target_face_count"] = h["target_tris"]
    wf["98"]["inputs"]["texture_size"] = 2048
    wf["86"]["inputs"]["filename_prefix"] = "kitbash_head"
    entry = comfy.wait(comfy.queue(wf))
    dest = char_dir / "build" / "head_gen.glb"
    glbs = find_glb_outputs(entry)
    if glbs:
        comfy.download_output(glbs[-1][0], glbs[-1][1], dest)
    else:  # export node doesn't always report via history; take newest from disk
        out_dir = Path(pipeline["comfy"]["root"]) / "ComfyUI" / "output"
        newest = max(out_dir.glob("kitbash_head*.glb"), key=lambda p: p.stat().st_mtime)
        dest.write_bytes(newest.read_bytes())
    return dest


def _slab_stats(mesh, lo_frac, hi_frac):
    v = mesh.vertices
    y0, y1 = v[:, 1].min(), v[:, 1].max()
    m = (v[:, 1] >= y0 + lo_frac * (y1 - y0)) & (v[:, 1] <= y0 + hi_frac * (y1 - y0))
    slab = v[m]
    return slab[:, 0].max() - slab[:, 0].min(), slab.mean(axis=0)


def merge(spec: dict, char_dir: Path) -> Path:
    h = spec["head"]
    body = trimesh.load(char_dir / "build" / f"{char_dir.name}_mesh_projected.glb", force="mesh")
    head = trimesh.load(char_dir / "build" / "head_gen.glb", force="mesh")

    # Scale the head so helmet widths agree (top slab of each), then pin the
    # helmet crown and center.
    bw, bc = _slab_stats(body, 0.94, 1.0)
    hw, hc = _slab_stats(head, 0.90, 1.0)
    s = bw / hw
    head.apply_scale(s)
    _, hc = _slab_stats(head, 0.90, 1.0)
    delta = bc - hc
    delta[1] = (body.vertices[:, 1].max() - head.vertices[:, 1].max())
    head.apply_translation(delta)

    # Cut: body loses everything above its collar top; head keeps everything
    # above its own collar bottom. The overlap band hides inside the collar.
    by0, by1 = body.vertices[:, 1].min(), body.vertices[:, 1].max()
    body_cut = by0 + h["body_cut"] * (by1 - by0)
    keep_faces = (body.vertices[body.faces][:, :, 1] < body_cut).any(axis=1)
    body.update_faces(keep_faces)
    body.remove_unreferenced_vertices()

    hy0, hy1 = head.vertices[:, 1].min(), head.vertices[:, 1].max()
    head_cut = hy0 + h["head_cut"] * (hy1 - hy0)
    keep_faces = (head.vertices[head.faces][:, :, 1] > head_cut).any(axis=1)
    head.update_faces(keep_faces)
    head.remove_unreferenced_vertices()

    scene = trimesh.Scene()
    scene.add_geometry(body, node_name="Body")
    scene.add_geometry(head, node_name="Head")
    out = char_dir / "build" / f"{char_dir.name}_kitbash.glb"
    scene.export(out)
    print(f"[kitbash] body {len(body.faces)} + head {len(head.faces)} faces -> {out}")
    return out


def main() -> None:
    character = sys.argv[1] if len(sys.argv) > 1 else "torga"
    char_dir = ROOT / "characters" / character
    spec = load_toml(char_dir / "spec.toml")
    pipeline = load_toml(ROOT / "pipeline.toml")
    build = char_dir / "build"

    if not (build / "head_gen.glb").exists():
        comfy = connect(pipeline)
        crop_head(spec, char_dir)
        shade_head(spec, char_dir, comfy)
        generate_head(spec, char_dir, comfy, pipeline)
    merge(spec, char_dir)


if __name__ == "__main__":
    main()

"""Bake the style anchor onto the mesh's front-facing texels.

TRELLIS.2's appearance model is the texture-fidelity ceiling: it paints soft
approximations, never the anchor's crisp linework. But the mesh was generated
FROM the anchor, so an orthographic front projection lines up with it. This
stage replaces every texel that faces the camera (and is not occluded) with
the anchor's actual pixels, blending back to the generated texture as the
surface turns away. The front — class card, the mostly-frontal game read —
becomes pixel-faithful to the approved image.

    uv run python -m art_pipeline.reproject torga

Reads  characters/<name>/build/<name>_mesh.glb  +  the spec's style anchor.
Writes characters/<name>/build/<name>_mesh_projected.glb
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import trimesh
from PIL import Image

from .cli import ROOT, load_toml

# Texels whose normal faces the camera harder than this get anchor pixels;
# the band between FADE_LO..FADE_HI blends anchor over generated.
FADE_LO = 0.15
FADE_HI = 0.55
DEPTH_TOL = 0.015  # of figure depth: how far behind the z-buffer still counts as visible
FEATHER = 0.03  # height-fraction feather at exclusion band edges


def _raster_barycentric(tri2d: np.ndarray, values: np.ndarray, size: int, channels: int):
    """Rasterize triangles in [0,1]^2 with per-vertex values -> (size,size,channels) + coverage."""
    out = np.zeros((size, size, channels), dtype=np.float64)
    depth = np.full((size, size), -np.inf)
    cover = np.zeros((size, size), dtype=bool)
    px = tri2d * (size - 1)
    for t in range(px.shape[0]):
        p, val = px[t], values[t]
        lo = np.floor(p.min(axis=0)).astype(int)
        hi = np.ceil(p.max(axis=0)).astype(int)
        if hi[0] < 0 or hi[1] < 0 or lo[0] >= size or lo[1] >= size:
            continue
        lo = np.maximum(lo, 0)
        hi = np.minimum(hi, size - 1)
        xs = np.arange(lo[0], hi[0] + 1)
        ys = np.arange(lo[1], hi[1] + 1)
        gx, gy = np.meshgrid(xs, ys)
        d = (p[1][0] - p[0][0]) * (p[2][1] - p[0][1]) - (p[2][0] - p[0][0]) * (p[1][1] - p[0][1])
        if abs(d) < 1e-12:
            continue
        w0 = ((p[1][0] - gx) * (p[2][1] - gy) - (p[2][0] - gx) * (p[1][1] - gy)) / d
        w1 = ((p[2][0] - gx) * (p[0][1] - gy) - (p[0][0] - gx) * (p[2][1] - gy)) / d
        w2 = 1.0 - w0 - w1
        inside = (w0 >= -1e-6) & (w1 >= -1e-6) & (w2 >= -1e-6)
        if not inside.any():
            continue
        interp = (
            w0[..., None] * val[0] + w1[..., None] * val[1] + w2[..., None] * val[2]
        )
        # z (last channel) doubles as the depth key so overlapping UV islands
        # and front-view rasterization both keep the nearest surface.
        z = interp[..., -1]
        yy, xx = gy[inside], gx[inside]
        zi = z[inside]
        better = zi > depth[yy, xx]
        yy, xx = yy[better], xx[better]
        depth[yy, xx] = zi[better]
        out[yy, xx] = interp[inside][better]
        cover[yy, xx] = True
    return out, cover, depth


def reproject(character: str) -> Path:
    char_dir = ROOT / "characters" / character
    spec = load_toml(char_dir / "spec.toml")
    glb = char_dir / "build" / f"{character}_mesh.glb"
    anchor_path = char_dir / spec["s2"]["style_anchor"]

    mesh = trimesh.load(glb, force="mesh")
    material = mesh.visual.material
    tex = np.asarray(material.baseColorTexture.convert("RGB"), dtype=np.float64) / 255.0
    atlas = tex.shape[0]
    uv = np.asarray(mesh.visual.uv)
    v = np.asarray(mesh.vertices)
    f = np.asarray(mesh.faces)
    vn = np.asarray(mesh.vertex_normals)

    anchor = Image.open(anchor_path).convert("RGB")
    a = np.asarray(anchor, dtype=np.float64) / 255.0
    ah, aw = a.shape[:2]

    # Figure bbox in the anchor (white background): where any channel departs white.
    fg = (a < 0.94).any(axis=2)
    ys, xs = np.where(fg)
    ax0, ax1, ay0, ay1 = xs.min(), xs.max(), ys.min(), ys.max()

    # Mesh bbox in the front plane (x right, y up, +z toward viewer).
    mx0, my0 = v[:, 0].min(), v[:, 1].min()
    mx1, my1 = v[:, 0].max(), v[:, 1].max()

    # 1) UV-space pass: per-texel world position (xyz) keyed on +z.
    tri_uv = uv[f]
    tri_val = np.concatenate([v[f], vn[f]], axis=2)  # xyz + normal per corner
    tri_val = tri_val[..., [0, 1, 3, 4, 5, 2]]  # move z last as the depth key
    texel, covered, _ = _raster_barycentric(tri_uv, tri_val, atlas, 6)
    tx, ty = texel[..., 0], texel[..., 1]
    nx, ny, nz = texel[..., 2], texel[..., 3], texel[..., 4]
    tz = texel[..., 5]

    # 2) Front-view z-buffer at atlas resolution (screen xy -> nearest z).
    span = max(mx1 - mx0, my1 - my0)
    sxy = np.stack([(v[:, 0] - mx0) / span, (v[:, 1] - my0) / span], axis=1)
    tri_screen = sxy[f]
    _, scover, zbuf = _raster_barycentric(tri_screen, v[f][..., [0, 1, 2]], atlas, 3)

    # 3) For every covered texel: is it the front surface, and does it face us?
    su = np.clip(((tx - mx0) / span) * (atlas - 1), 0, atlas - 1).astype(int)
    sv = np.clip(((ty - my0) / span) * (atlas - 1), 0, atlas - 1).astype(int)
    depth_at = zbuf[sv, su]
    zspan = v[:, 2].max() - v[:, 2].min()
    front_surface = tz >= depth_at - DEPTH_TOL * max(zspan, 1e-6)
    n_len = np.sqrt(nx**2 + ny**2 + nz**2) + 1e-9
    facing = nz / n_len
    w = np.clip((facing - FADE_LO) / (FADE_HI - FADE_LO), 0.0, 1.0)
    w = np.where(front_surface & covered, w, 0.0)

    # Exclusion bands (fractions of figure height, feathered): regions where
    # the mesh geometry deviates too much from the flat drawing for projection
    # to line up — faces, mostly. The generated texture keeps those.
    yfrac = (ty - my0) / (my1 - my0 + 1e-9)
    for lo, hi in spec.get("reproject", {}).get("exclude_y", []):
        keep = np.clip(
            np.minimum(np.abs(yfrac - lo), np.abs(yfrac - hi)) / FEATHER, 0.0, 1.0
        )
        keep = np.where((yfrac > lo) & (yfrac < hi), 0.0, keep)
        w *= keep

    # 4) Sample the anchor: front-plane xy -> figure-bbox pixel.
    px = ax0 + (tx - mx0) / (mx1 - mx0 + 1e-9) * (ax1 - ax0)
    py = ay0 + (1.0 - (ty - my0) / (my1 - my0 + 1e-9)) * (ay1 - ay0)
    px = np.clip(px, 0, aw - 1).astype(int)
    py = np.clip(py, 0, ah - 1).astype(int)
    sampled = a[py, px]

    # glTF UV origin is top-left; our rasterizer indexed row 0 at v=0 (bottom).
    tex_flipped = tex[::-1]
    blended = tex_flipped * (1 - w[..., None]) + sampled * w[..., None]
    blended = blended[::-1]

    material.baseColorTexture = Image.fromarray(
        (np.clip(blended, 0, 1) * 255).astype(np.uint8)
    )
    out = glb.with_name(f"{character}_mesh_projected.glb")
    mesh.export(out)
    print(f"[reproject] {int(w.astype(bool).sum())} texels took anchor pixels -> {out}")
    return out


if __name__ == "__main__":
    reproject(sys.argv[1] if len(sys.argv) > 1 else "torga")

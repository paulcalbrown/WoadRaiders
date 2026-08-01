"""Ingest: turn a human-downloaded Meshy GLB into a contract-passing game asset.

    uv run art-pipeline warrior --stage ingest [--dry-run]

Reads the newest character GLB from characters/<name>/<source_dir>/ and:
inventories it, renames clips to the standard contract (Idle/Run/Fall/Attack)
from [ingest.clips], prefixes mesh nodes, rigid-attaches [ingest.props] to
hand bones, normalizes to height_m with feet at y=0, checks Run for root
motion, validates budgets, and promotes to
WoadRaiders.Client/assets/characters/<Name>.glb with a provenance sidecar.

All edits are glTF JSON-level (pygltflib): geometry, skinning and animation
data are never re-encoded.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import struct
import sys
from datetime import date
from pathlib import Path

import numpy as np
from PIL import Image
from pygltflib import GLTF2, Node

from .cli import ROOT, load_toml

CONTRACT_CLIPS = ["Idle", "Run", "Fall", "Attack"]
COMPONENT_FMT = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}
TYPE_LEN = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}


def _blob(gltf: GLTF2) -> bytes:
    return gltf.binary_blob()


def read_accessor(gltf: GLTF2, idx: int) -> np.ndarray:
    acc = gltf.accessors[idx]
    bv = gltf.bufferViews[acc.bufferView]
    fmt = COMPONENT_FMT[acc.componentType]
    n = TYPE_LEN[acc.type]
    itemsize = struct.calcsize(fmt)
    stride = bv.byteStride or itemsize * n
    base = (bv.byteOffset or 0) + (acc.byteOffset or 0)
    blob = _blob(gltf)
    out = np.empty((acc.count, n), dtype=np.dtype(fmt))
    if stride == itemsize * n:
        out = np.frombuffer(
            blob, dtype=np.dtype(fmt), count=acc.count * n, offset=base
        ).reshape(acc.count, n)
    else:
        for i in range(acc.count):
            off = base + i * stride
            out[i] = struct.unpack_from(f"<{n}{fmt}", blob, off)
    return out


def bounds(gltf: GLTF2) -> tuple[np.ndarray, np.ndarray]:
    lo = np.full(3, np.inf)
    hi = np.full(3, -np.inf)
    for mesh in gltf.meshes:
        for prim in mesh.primitives:
            pos = prim.attributes.POSITION
            if pos is None:
                continue
            acc = gltf.accessors[pos]
            if acc.min and acc.max:
                lo = np.minimum(lo, acc.min)
                hi = np.maximum(hi, acc.max)
    return lo, hi


def tri_count(gltf: GLTF2) -> int:
    total = 0
    for mesh in gltf.meshes:
        for prim in mesh.primitives:
            if prim.indices is not None:
                total += gltf.accessors[prim.indices].count // 3
            elif prim.attributes.POSITION is not None:
                total += gltf.accessors[prim.attributes.POSITION].count // 3
    return total


def texture_sizes(gltf: GLTF2) -> list[tuple[str, int, int]]:
    sizes = []
    for i, img in enumerate(gltf.images):
        if img.bufferView is None:
            continue
        bv = gltf.bufferViews[img.bufferView]
        data = _blob(gltf)[bv.byteOffset or 0 : (bv.byteOffset or 0) + bv.byteLength]
        try:
            with Image.open(io.BytesIO(data)) as im:
                sizes.append((img.name or f"image_{i}", *im.size))
        except Exception:
            sizes.append((img.name or f"image_{i}", -1, -1))
    return sizes


def inventory(gltf: GLTF2) -> dict:
    lo, hi = bounds(gltf)
    inv = {
        "clips": [a.name for a in gltf.animations],
        "mesh_nodes": [n.name for n in gltf.nodes if n.mesh is not None],
        "skins": len(gltf.skins),
        "joints": [gltf.nodes[j].name for j in (gltf.skins[0].joints if gltf.skins else [])],
        "tris": tri_count(gltf),
        "textures": texture_sizes(gltf),
        "bounds_lo": lo.tolist(),
        "bounds_hi": hi.tolist(),
        "height_units": float(hi[1] - lo[1]),
    }
    print(f"[ingest] clips: {inv['clips']}")
    print(f"[ingest] mesh nodes: {inv['mesh_nodes']}")
    print(f"[ingest] skins: {inv['skins']}, joints: {len(inv['joints'])}")
    print(f"[ingest] tris: {inv['tris']}, textures: {inv['textures']}")
    print(f"[ingest] height: {inv['height_units']:.3f} units, bounds y "
          f"[{lo[1]:.3f}, {hi[1]:.3f}]")
    return inv


def rename_clips(gltf: GLTF2, mapping: dict) -> None:
    available = {a.name: a for a in gltf.animations}
    missing = {}
    for contract, source in mapping.items():
        if not source:
            missing[contract] = "(unmapped in spec)"
        elif source not in available:
            missing[contract] = f"'{source}' not in file"
    if missing:
        raise SystemExit(
            f"[ingest] clip mapping failed: {missing}\n"
            f"[ingest] clips present in file: {sorted(available)}"
        )
    for contract, source in mapping.items():
        available[source].name = contract
    print(f"[ingest] clips renamed: {mapping}")


def prefix_mesh_nodes(gltf: GLTF2, prefix: str) -> None:
    for node in gltf.nodes:
        if node.mesh is not None:
            name = node.name or "Mesh"
            if not name.startswith(prefix):
                node.name = f"{prefix}_{name}"
    print(f"[ingest] mesh nodes prefixed with {prefix}_")


def _euler_quat(deg: list) -> list:
    from scipy.spatial.transform import Rotation

    return Rotation.from_euler("xyz", deg, degrees=True).as_quat().tolist()


def merge_prop(gltf: GLTF2, prop_path: Path, cfg: dict) -> None:
    prop = GLTF2().load(str(prop_path))
    pblob = prop.binary_blob()
    blob = bytearray(_blob(gltf))
    while len(blob) % 4:
        blob.append(0)
    shift = len(blob)
    blob.extend(pblob)

    B, A = len(gltf.bufferViews), len(gltf.accessors)
    I, S, T = len(gltf.images), len(gltf.samplers), len(gltf.textures)
    M, ME, N = len(gltf.materials), len(gltf.meshes), len(gltf.nodes)

    for bv in prop.bufferViews:
        bv.buffer = 0
        bv.byteOffset = (bv.byteOffset or 0) + shift
        gltf.bufferViews.append(bv)
    for acc in prop.accessors:
        acc.bufferView = (acc.bufferView or 0) + B
        gltf.accessors.append(acc)
    for img in prop.images:
        if img.bufferView is not None:
            img.bufferView += B
        gltf.images.append(img)
    for smp in prop.samplers:
        gltf.samplers.append(smp)
    for tex in prop.textures:
        if tex.source is not None:
            tex.source += I
        if tex.sampler is not None:
            tex.sampler += S
        gltf.textures.append(tex)
    for mat in prop.materials:
        pbr = mat.pbrMetallicRoughness
        for ref in filter(None, [
            pbr.baseColorTexture if pbr else None,
            pbr.metallicRoughnessTexture if pbr else None,
            mat.normalTexture, mat.occlusionTexture, mat.emissiveTexture,
        ]):
            ref.index += T
        gltf.materials.append(mat)
    for mesh in prop.meshes:
        for prim in mesh.primitives:
            for attr in vars(prim.attributes):
                v = getattr(prim.attributes, attr)
                if isinstance(v, int):
                    setattr(prim.attributes, attr, v + A)
            if prim.indices is not None:
                prim.indices += A
            if prim.material is not None:
                prim.material += M
        gltf.meshes.append(mesh)
    mesh_i = 0
    for node in prop.nodes:
        if node.mesh is not None:
            node.mesh += ME
            # Prop mesh nodes become MeshInstances in Godot; they must carry
            # the contract name or CharacterLoadout hides them.
            node.name = cfg["name"] if mesh_i == 0 else f"{cfg['name']}_{mesh_i}"
            mesh_i += 1
        node.children = [c + N for c in (node.children or [])]
        gltf.nodes.append(node)

    prop_roots = [r + N for r in prop.scenes[prop.scene or 0].nodes]

    joints = {gltf.nodes[j].name: j for j in gltf.skins[0].joints} if gltf.skins else {}
    bone = cfg["bone"]
    if bone not in joints:
        raise SystemExit(
            f"[ingest] prop bone '{bone}' not found. Skeleton joints: {sorted(joints)}"
        )
    holder = Node(
        name=cfg["name"],
        children=prop_roots,
        translation=list(cfg.get("offset", [0, 0, 0])),
        rotation=_euler_quat(list(cfg.get("rotation_deg", [0, 0, 0]))),
        scale=[cfg.get("scale", 1.0)] * 3,
    )
    gltf.nodes.append(holder)
    joint_node = gltf.nodes[joints[bone]]
    joint_node.children = (joint_node.children or []) + [len(gltf.nodes) - 1]

    for ext in prop.extensionsUsed or []:
        if ext not in (gltf.extensionsUsed or []):
            gltf.extensionsUsed = (gltf.extensionsUsed or []) + [ext]

    gltf.buffers[0].byteLength = len(blob)
    gltf.set_binary_blob(bytes(blob))
    print(f"[ingest] prop '{cfg['name']}' from {prop_path.name} attached to {bone}")


def normalize(gltf: GLTF2, height_m: float, yaw_deg: float = 0.0) -> float:
    lo, hi = bounds(gltf)
    height = float(hi[1] - lo[1])
    s = height_m / height
    wrapper = Node(
        name="IngestRoot",
        children=list(gltf.scenes[gltf.scene or 0].nodes),
        scale=[s, s, s],
        translation=[0.0, -float(lo[1]) * s, 0.0],
    )
    if yaw_deg:
        wrapper.rotation = _euler_quat([0, yaw_deg, 0])
    gltf.nodes.append(wrapper)
    gltf.scenes[gltf.scene or 0].nodes = [len(gltf.nodes) - 1]
    print(f"[ingest] normalized: {height:.3f} units -> {height_m} m "
          f"(scale {s:.4f}), feet at y=0")
    return s


def check_run_drift(gltf: GLTF2, height_m: float, scale: float) -> None:
    anim = next((a for a in gltf.animations if a.name == "Run"), None)
    if anim is None:
        raise SystemExit("[ingest] no 'Run' clip after rename — cannot check root motion")
    worst = 0.0
    for ch in anim.channels:
        if ch.target.path != "translation":
            continue
        sampler = anim.samplers[ch.sampler]
        vals = read_accessor(gltf, sampler.output)
        drift = float(np.linalg.norm((vals[-1] - vals[0])[[0, 2]])) * scale
        worst = max(worst, drift)
    limit = 0.02 * height_m
    if worst > limit:
        raise SystemExit(
            f"[ingest] Run drifts {worst:.3f} m in XZ (limit {limit:.3f}) — "
            "root motion detected; pick an IN-PLACE run in Meshy"
        )
    print(f"[ingest] Run root drift {worst*100:.1f} cm — in place ✓")


def validate(gltf: GLTF2, spec: dict) -> None:
    problems = []
    clips = [a.name for a in gltf.animations]
    for c in CONTRACT_CLIPS:
        if c not in clips:
            problems.append(f"missing contract clip {c}")
    if len(gltf.skins) != 1:
        problems.append(f"expected 1 skin, found {len(gltf.skins)}")
    tris = tri_count(gltf)
    if tris > spec["ingest"]["max_tris"]:
        problems.append(f"tris {tris} > budget {spec['ingest']['max_tris']}")
    for name, w, h in texture_sizes(gltf):
        if max(w, h) > spec["ingest"]["max_texture"]:
            problems.append(f"texture {name} {w}x{h} > {spec['ingest']['max_texture']}")
    prefix = spec["character"]["mesh_prefix"]
    for n in gltf.nodes:
        if n.mesh is not None and not (n.name or "").startswith(prefix):
            problems.append(f"mesh node '{n.name}' lacks prefix {prefix}")
    if problems:
        raise SystemExit("[ingest] VALIDATION FAILED:\n  - " + "\n  - ".join(problems))
    print(f"[ingest] validation PASSED ({tris} tris, clips {clips})")


def run_ingest(character: str, dry_run: bool = False, out: Path | None = None) -> Path | None:
    char_dir = ROOT / "characters" / character
    spec = load_toml(char_dir / "spec.toml")
    ing = spec["ingest"]
    src_dir = char_dir / ing["source_dir"]
    prop_files = {p["file"] for p in ing.get("props", {}).values()}
    candidates = [
        p for p in src_dir.glob("*.glb") if p.name not in prop_files
    ]
    if not candidates:
        raise SystemExit(f"[ingest] no character GLB in {src_dir}")
    src = max(candidates, key=lambda p: p.stat().st_mtime)
    print(f"[ingest] source: {src.name} ({src.stat().st_size/1e6:.1f} MB)")

    gltf = GLTF2().load(str(src))
    inventory(gltf)
    if dry_run:
        return None

    rename_clips(gltf, dict(ing["clips"]))
    prefix_mesh_nodes(gltf, spec["character"]["mesh_prefix"])
    for key, cfg in ing.get("props", {}).items():
        merge_prop(gltf, src_dir / cfg["file"], cfg)
    scale = normalize(gltf, spec["character"]["height_m"])
    check_run_drift(gltf, spec["character"]["height_m"], scale)
    validate(gltf, spec)

    name = spec["character"]["name"]
    dest = out or (ROOT.parent / "WoadRaiders.Client" / "assets" / "characters" / f"{name}.glb")
    dest.parent.mkdir(parents=True, exist_ok=True)
    gltf.save(str(dest))
    sidecar = {
        "source_file": src.name,
        "source_sha256": hashlib.sha256(src.read_bytes()).hexdigest(),
        "ingested": date.today().isoformat(),
        "character": name,
        "clips": [a.name for a in gltf.animations],
    }
    dest.with_suffix(".glb.provenance.json").write_text(json.dumps(sidecar, indent=2))
    print(f"[ingest] PROMOTED -> {dest}")
    return dest


def main() -> None:
    parser = argparse.ArgumentParser(prog="art-pipeline-ingest")
    parser.add_argument("character")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    run_ingest(args.character, args.dry_run, args.out)


if __name__ == "__main__":
    main()

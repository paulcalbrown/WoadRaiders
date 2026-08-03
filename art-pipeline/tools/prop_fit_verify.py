"""Verify the SHIPPED Warrior.glb prop fit by posing it at Idle t=0 (CPU skinning).

Renders front/three-quarter/side views of the promoted GLB in the idle pose to
characters/warrior/build/idle_pose_check.png and asserts the solved world-space
orientations: blade/edge direction (pinned to tools/prop_fit_solve.py output),
shield kite point at the ground, shield face forward. Run from art-pipeline:
uv run --with matplotlib python tools/prop_fit_verify.py
"""
import numpy as np
from pathlib import Path
from pygltflib import GLTF2
from scipy.spatial.transform import Rotation
import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d.art3d import Poly3DCollection

REPO = Path(__file__).resolve().parents[2]
GLB = REPO / "WoadRaiders.Client" / "assets" / "characters" / "Warrior.glb"
OUT = REPO / "art-pipeline" / "characters" / "warrior" / "build" / "idle_pose_check.png"

g = GLTF2().load(str(GLB))
BLOB = g.binary_blob()
FMT = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}
NCOMP = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}

def read_acc(idx):
    acc = g.accessors[idx]
    bv = g.bufferViews[acc.bufferView]
    n = NCOMP[acc.type]
    dt = np.dtype(FMT[acc.componentType])
    base = (bv.byteOffset or 0) + (acc.byteOffset or 0)
    out = np.frombuffer(BLOB, dtype=dt, count=acc.count * n, offset=base).reshape(acc.count, n).copy()
    if acc.normalized:
        out = out.astype(np.float64) / np.iinfo(dt).max
    return out

parent = {}
for i, n in enumerate(g.nodes):
    for c in n.children or []:
        parent[c] = i

trs = {}
for i, n in enumerate(g.nodes):
    trs[i] = [list(n.translation or [0, 0, 0]), list(n.rotation or [0, 0, 0, 1]), list(n.scale or [1, 1, 1])]

anim = next(a for a in g.animations if a.name == "Idle")
for ch in anim.channels:
    v = read_acc(anim.samplers[ch.sampler].output)[0].tolist()
    trs[ch.target.node][{"translation": 0, "rotation": 1, "scale": 2}[ch.target.path]] = v

world = {}
def world_of(i):
    if i in world:
        return world[i]
    t, r, s = trs[i]
    m = np.eye(4)
    m[:3, :3] = Rotation.from_quat(r).as_matrix() * np.array(s)
    m[:3, 3] = t
    w = world_of(parent[i]) @ m if i in parent else m
    world[i] = w
    return w

skin = g.skins[0]
ibms = read_acc(skin.inverseBindMatrices).reshape(-1, 4, 4).transpose(0, 2, 1)
jmats = np.stack([world_of(j) @ ibms[k] for k, j in enumerate(skin.joints)])

tris_all, cols_all = [], []
COLORS = {"Warrior_Weapon": (0.60, 0.68, 0.78), "Warrior_Shield": (0.72, 0.22, 0.18)}

def emit(mesh_idx, verts_world, color):
    mesh = g.meshes[mesh_idx]
    for prim in mesh.primitives:
        f = read_acc(prim.indices).reshape(-1, 3)
        tris_all.append(verts_world[f])
        cols_all.append(np.full((len(f), 3), color))

def under(idx, names):
    while True:
        if (g.nodes[idx].name or "") in names:
            return g.nodes[idx].name
        if idx not in parent:
            return None
        idx = parent[idx]

prop_dirs = {}
for i, n in enumerate(g.nodes):
    if n.mesh is None:
        continue
    holder = under(i, COLORS.keys())
    if n.skin is not None:
        prim = g.meshes[n.mesh].primitives[0]
        v = read_acc(prim.attributes.POSITION)
        jj = read_acc(prim.attributes.JOINTS_0).astype(int)
        ww = read_acc(prim.attributes.WEIGHTS_0).astype(np.float64)
        ww = ww / np.clip(ww.sum(1, keepdims=True), 1e-9, None)
        vh = np.concatenate([v, np.ones((len(v), 1))], axis=1)
        posed = np.zeros((len(v), 3))
        for k in range(4):
            M = jmats[jj[:, k]]
            posed += ww[:, k:k + 1] * np.einsum("nij,nj->ni", M[:, :3, :], vh)
        emit(n.mesh, posed, (0.75, 0.73, 0.70))
    else:
        W = world_of(i)
        v = read_acc(g.meshes[n.mesh].primitives[0].attributes.POSITION)
        vh = np.concatenate([v, np.ones((len(v), 1))], axis=1)
        posed = (W[:3, :] @ vh.T).T
        emit(n.mesh, posed, COLORS.get(holder, (0.6, 0.6, 0.6)))
        if holder and holder not in prop_dirs:
            hi = next(k for k, nd in enumerate(g.nodes) if nd.name == holder)
            HR = world_of(hi)[:3, :3]
            prop_dirs[holder] = HR / np.linalg.norm(HR, axis=0)

print("--- orientation asserts (Idle@0, world space, character faces +Z) ---")
HR = prop_dirs["Warrior_Weapon"]
u = np.array([-0.9527, 0.0, 0.3038])
u = u / np.linalg.norm(u)
blade = HR @ u
edge = HR @ np.cross(u, [0.0, 1.0, 0.0])
EXPECTED_BLADE = np.array([0.0444, 0.8420, 0.5377])   # prop_fit_solve.py output
EXPECTED_EDGE = np.array([-0.7362, -0.3363, 0.5874])
print(f"blade dir world: {np.round(blade, 3)}  (expected {EXPECTED_BLADE})")
print(f"edge axis world: {np.round(edge, 3)}  (expected {EXPECTED_EDGE})")
assert blade @ EXPECTED_BLADE > 0.995, "blade dir drifted from the solve!"
assert edge @ EXPECTED_EDGE > 0.995, "edge axis drifted from the solve!"
HR = prop_dirs["Warrior_Shield"]
point = HR @ np.array([0.0, -1.0, 0.0])
face = HR @ np.array([0.0, 0.0, 1.0])
print(f"shield point world: {np.round(point, 3)}  (want ~(0,-1,0) = ground)")
print(f"shield face  world: {np.round(face, 3)}  (want Z >> 0 = forward)")
assert point[1] < -0.9, "shield point not at the ground!"
assert face[2] > 0.85, "shield face not forward!"
print("ASSERTS PASSED")

tris = np.vstack(tris_all)
cols = np.vstack(cols_all)
allv = tris.reshape(-1, 3)
center = allv.mean(axis=0)
yr = allv[:, 1].max() - allv[:, 1].min()
views = [("front", 180), ("three-quarter", 135), ("side (left)", 90)]
fig = plt.figure(figsize=(21, 8), facecolor="white")
for i, (title, az) in enumerate(views):
    ax = fig.add_subplot(1, 3, i + 1, projection="3d")
    a = np.deg2rad(az)
    eye = np.array([np.sin(a), 0.25, np.cos(a)])
    eye /= np.linalg.norm(eye)
    nrm = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
    lam = nrm @ eye / np.clip(np.linalg.norm(nrm, axis=1), 1e-9, None)
    shade = np.clip(np.abs(lam) * 0.45 + 0.6, 0, 1)
    order = np.argsort(tris.mean(axis=1) @ eye)
    t3 = (tris - center)[order][:, :, [0, 2, 1]]
    ax.add_collection3d(Poly3DCollection(t3, facecolors=np.clip(cols[order] * shade[order, None], 0, 1), edgecolor="none"))
    s = 1.1 * yr
    zlo = allv[:, 1].min() - center[1]
    ax.set_xlim(-s / 2, s / 2)
    ax.set_ylim(-s / 2, s / 2)
    ax.set_zlim(zlo, zlo + s)
    ax.set_box_aspect((1, 1, 1))
    ax.view_init(elev=6, azim=az - 90)
    ax.axis("off")
    ax.set_title(f"Idle pose — {title}")
plt.tight_layout()
OUT.parent.mkdir(parents=True, exist_ok=True)
fig.savefig(OUT, dpi=110)
print("wrote", OUT)

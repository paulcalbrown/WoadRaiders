"""Solve [ingest.props] rotation/offset for the Warrior's sword against the IDLE pose.

A rigid prop attach only reads right in the pose players actually see, so this
solves in the sampled Idle@0 frame of build/warrior_animated.glb, not the
T-pose. The base solve puts the blade on the fist axis (hand local +X) with the
edge-to-edge axis as parallel to the forearm as the wrist bend allows; the
knobs below are Paul's by-eye adjustments layered on top. Re-run after any
re-rig or clip change, paste the printed rotation_deg/offset into spec.toml,
then `uv run art-pipeline warrior --stage ingest` and verify with
tools/prop_fit_verify.py. Run: uv run python tools/prop_fit_solve.py
"""
import numpy as np
from pathlib import Path
from pygltflib import GLTF2
from scipy.spatial.transform import Rotation

ROLL_DEG = 15.0     # CCW looking down the blade from the tip
TILT_DEG = 13.0     # blade tilt away from the face
FWD_DEG = 8.0       # forward pitch: realigns the handle with the fist line

# sword anatomy, measured from meshy/sword.glb (see spec.toml comments)
GRIP_CENTER = np.array([0.6137, 0.0008, -0.1697])
GRIP_TO_TIP_AXIS = np.array([-0.9527, 0.0, 0.3038])
SCALE = 0.61
PALM = np.array([0.0, 0.07, 0.025])       # grip anchor in RightHand local
GRIP_TO_TIP_LEN = 0.9                     # approx, pre-bake units

ROOT = Path(__file__).resolve().parents[1] / "characters" / "warrior"
g = GLTF2().load(str(ROOT / "build" / "warrior_animated.glb"))
BLOB = g.binary_blob()
FMT = {5120: "b", 5121: "B", 5122: "h", 5123: "H", 5125: "I", 5126: "f"}
NCOMP = {"SCALAR": 1, "VEC3": 3, "VEC4": 4, "MAT4": 16}

def read_acc(idx):
    acc = g.accessors[idx]
    bv = g.bufferViews[acc.bufferView]
    n = NCOMP[acc.type]
    dt = np.dtype(FMT[acc.componentType])
    base = (bv.byteOffset or 0) + (acc.byteOffset or 0)
    return np.frombuffer(BLOB, dtype=dt, count=acc.count * n, offset=base).reshape(acc.count, n).copy()

parent = {}
for i, n in enumerate(g.nodes):
    for c in n.children or []:
        parent[c] = i
by_name = {n.name: i for i, n in enumerate(g.nodes)}
trs = {i: [list(n.translation or [0, 0, 0]), list(n.rotation or [0, 0, 0, 1]), list(n.scale or [1, 1, 1])]
       for i, n in enumerate(g.nodes)}
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

wh = world_of(by_name["mixamorig:RightHand"])
whead = world_of(by_name["mixamorig:Head"])
Rh = wh[:3, :3] / np.linalg.norm(wh[:3, :3], axis=0)
hand_pos = wh[:3, 3]
face_pos = whead[:3, 3] + np.array([0.0, 0.06, 0.10])   # eye-ish point
print("hand:", np.round(hand_pos, 3), " face:", np.round(face_pos, 3))

# base solve: blade on the fist axis, edge-to-edge axis // forearm (projected)
u = GRIP_TO_TIP_AXIS / np.linalg.norm(GRIP_TO_TIP_AXIS)
t = np.array([0.0, 1.0, 0.0])             # blade-flat normal in prop space
wf = world_of(by_name["mixamorig:RightForeArm"])
fore = wh[:3, 3] - wf[:3, 3]; fore /= np.linalg.norm(fore)
d = Rh.T @ fore; d[0] = 0.0; d /= np.linalg.norm(d)
x_t = np.array([1.0, 0.0, 0.0])
T = np.column_stack([x_t, np.cross(d, x_t), d])
B = np.column_stack([u, t, np.cross(u, t)])
R_old = Rotation.from_matrix(T @ B.T).as_matrix()

# knob 1: roll about the blade (local X); CCW seen from the tip = +rotation
R1 = Rotation.from_rotvec(np.deg2rad(ROLL_DEG) * np.array([1.0, 0, 0])).as_matrix()

# knob 2: tilt the blade away from the face
b_world = Rh @ x_t
tip = hand_pos + GRIP_TO_TIP_LEN * b_world
away = tip - face_pos; away /= np.linalg.norm(away)
b_new = b_world + np.tan(np.deg2rad(TILT_DEG)) * away
b_new /= np.linalg.norm(b_new)
axis_w = np.cross(b_world, b_new)
ang = np.arcsin(np.clip(np.linalg.norm(axis_w), -1, 1))
axis_l = Rh.T @ (axis_w / np.linalg.norm(axis_w))
R2 = Rotation.from_rotvec(ang * axis_l).as_matrix()

# knob 3: forward pitch (toward world +Z), pivoting about the grip center
b_after2 = Rh @ (R2 @ R1 @ R_old @ u)
b_fwd = b_after2 + np.tan(np.deg2rad(FWD_DEG)) * np.array([0.0, 0.0, 1.0])
b_fwd /= np.linalg.norm(b_fwd)
axis3_w = np.cross(b_after2, b_fwd)
ang3 = np.arcsin(np.clip(np.linalg.norm(axis3_w), -1, 1))
axis3_l = Rh.T @ (axis3_w / np.linalg.norm(axis3_w))
R3 = Rotation.from_rotvec(ang3 * axis3_l).as_matrix()

R_new = R3 @ R2 @ R1 @ R_old
eul = Rotation.from_matrix(R_new).as_euler("xyz", degrees=True)
off = PALM - R_new @ (SCALE * GRIP_CENTER)
print("rotation_deg:", [round(v, 1) for v in eul])
print("offset:", [round(v, 3) for v in off])

blade_w = Rh @ (R_new @ u)
edge_w = Rh @ (R_new @ np.cross(u, t))
tip_new = hand_pos + GRIP_TO_TIP_LEN * blade_w
print("\nblade world:", np.round(blade_w, 3), " (was", np.round(b_world, 3), ")")
print("tip:", np.round(tip_new, 3), " dist to face:", round(float(np.linalg.norm(tip_new - face_pos)), 3),
      " (was", round(float(np.linalg.norm(tip - face_pos)), 3), ")")
print("edge axis world:", np.round(edge_w, 3), " align w/ forearm:", round(float(abs(edge_w @ fore)), 3))
print("\nEXPECTED_BLADE =", [round(v, 4) for v in blade_w])
print("EXPECTED_EDGE  =", [round(v, 4) for v in edge_w])
print("(paste rotation_deg/offset into spec.toml, EXPECTED_* into prop_fit_verify.py)")

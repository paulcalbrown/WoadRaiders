"""Rest-pose correction, run inside the UniRig env's Blender-as-module.

    python fix_rest_pose.py <in.fbx> <out_base> <bone:pitch_deg> [<bone:pitch_deg>...]

Rotates the named pose bones around their local X axis, applies the pose as
the new rest pose, bakes the deformation into the mesh, and exports
<out_base>.fbx and <out_base>.glb. Positive degrees pitch the head UP
(negative local X rotation is applied, matching mixamorig conventions).

Auto-rigged rest poses inherit the source mesh's posture; a Meshy character
generated with a slightly dropped chin keeps it in every retargeted clip.
This bakes the correction once, so Mixamo (and every other retargeter)
sees a straight-ahead rest.
"""

import math
import sys

import bpy

src, out_base = sys.argv[1], sys.argv[2]
tweaks = []
for arg in sys.argv[3:]:
    bone, deg = arg.rsplit(":", 1)
    tweaks.append((bone, float(deg)))

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=src)

arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
meshes = [o for o in bpy.data.objects if o.type == "MESH"]

# 1) Pose the corrections.
bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="POSE")
applied = []
for bone_name, deg in tweaks:
    pb = arm.pose.bones.get(bone_name)
    if pb is None:
        print(f"[fix_rest_pose] WARNING: bone '{bone_name}' not found; "
              f"available: {[b.name for b in arm.pose.bones][:40]}")
        continue
    pb.rotation_mode = "XYZ"
    pb.rotation_euler.rotate_axis("X", math.radians(-deg))
    applied.append((bone_name, deg))
bpy.ops.object.mode_set(mode="OBJECT")

# 2) Bake the posed shape into each mesh (armature modifier copy, applied),
#    then make the pose the new rest.
for mesh in meshes:
    mod = next((m for m in mesh.modifiers if m.type == "ARMATURE"), None)
    if mod is None:
        continue
    bpy.context.view_layer.objects.active = mesh
    dup = mesh.modifiers.new(name="bake_rest", type="ARMATURE")
    dup.object = mod.object
    # apply the duplicate (bakes current pose), keep the original for skinning
    bpy.ops.object.modifier_apply(modifier=dup.name)

bpy.context.view_layer.objects.active = arm
bpy.ops.object.mode_set(mode="POSE")
bpy.ops.pose.armature_apply(selected=False)
bpy.ops.object.mode_set(mode="OBJECT")

bpy.ops.export_scene.fbx(filepath=out_base + ".fbx", add_leaf_bones=False,
                         path_mode="COPY", embed_textures=True)
bpy.ops.export_scene.gltf(filepath=out_base + ".glb", export_format="GLB")
print(f"[fix_rest_pose] applied {applied} -> {out_base}.fbx/.glb")

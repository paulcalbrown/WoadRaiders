"""Apply the repo clip library to a rigged character; run in the UniRig env.

    python apply_clips.py <rigged.fbx> <clips_dir> <out.glb>

Every .fbx in clips_dir is a mixamo-format motion file (no skin) whose
bone names match the character's mixamorig skeleton, so each imported
action drives the main armature directly. Each clip becomes one NLA track
named after its file stem — and the glTF exporter turns each track into
one named animation. The filename IS the contract clip name.
"""

import sys
from pathlib import Path

import bpy

rigged, clips_dir, out = sys.argv[1], Path(sys.argv[2]), sys.argv[3]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.scene.render.fps = 30
bpy.ops.import_scene.fbx(filepath=rigged)
main_arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
main_bones = {b.name for b in main_arm.data.bones}

clips = sorted(clips_dir.glob("*.fbx"))
if not clips:
    raise SystemExit(f"[apply_clips] no .fbx clips in {clips_dir}")

if main_arm.animation_data is None:
    main_arm.animation_data_create()

applied = []
for clip in clips:
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(clip))
    new_objs = [o for o in bpy.data.objects if o not in before]
    clip_arm = next((o for o in new_objs if o.type == "ARMATURE"), None)
    action = None
    if clip_arm and clip_arm.animation_data and clip_arm.animation_data.action:
        action = clip_arm.animation_data.action
    if action is None:
        print(f"[apply_clips] WARNING: no action in {clip.name}, skipping")
        for o in new_objs:
            bpy.data.objects.remove(o, do_unlink=True)
        continue

    clip_bones = {b.name for b in clip_arm.data.bones}
    missing = clip_bones - main_bones
    if missing:
        print(f"[apply_clips] WARNING: {clip.name} has {len(missing)} unknown "
              f"bones (e.g. {sorted(missing)[:4]}) — applying anyway")

    action.name = clip.stem
    action.use_fake_user = True
    track = main_arm.animation_data.nla_tracks.new()
    track.name = clip.stem
    track.strips.new(clip.stem, 1, action)
    track.mute = True  # tracks are containers here, not a mixed performance

    for o in new_objs:
        bpy.data.objects.remove(o, do_unlink=True)
    applied.append(clip.stem)

bpy.ops.export_scene.gltf(
    filepath=out,
    export_format="GLB",
    export_animation_mode="NLA_TRACKS",
)
print(f"[apply_clips] {applied} -> {out}")

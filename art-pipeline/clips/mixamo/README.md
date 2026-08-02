# The animation clip library (mixamo-format, committed)

Drop Mixamo downloads here: **FBX Binary, Without Skin, 30 fps**, exported
after uploading the character's rigged FBX (so the motion is retargeted to
our proportions).

**The filename IS the contract clip name**: `Idle.fbx`, `Run.fbx`,
`Fall.fbx`, `Attack.fbx` — plus any extras (`Death.fbx`, `Hit.fbx`, ...)
which ride along under their own names. `Run.fbx` must be an in-place clip
(tick "In Place" in Mixamo); ingest rejects root motion.

Because every character rigs to the same mixamorig skeleton, this library
is roster-wide: one set of clips animates everyone.

`uv run art-pipeline <character> --stage animate` applies everything here
to the character's rigged FBX and produces `build/<character>_animated.glb`
for ingest.

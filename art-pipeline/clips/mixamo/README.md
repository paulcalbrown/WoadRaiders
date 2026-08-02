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

## Per-character clips

A character may have its own `characters/<name>/clips/` folder: same
filename-is-contract-name rule, and a file there OVERRIDES the shared one
with the same stem (the Warrior's axe Attack vs the Mage's spellcast
Attack). Extras on either side ride along. A character can opt out of a
shared clip with `[animate] exclude = ["Name"]` in its spec.

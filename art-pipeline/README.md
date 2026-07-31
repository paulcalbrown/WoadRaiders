# art-pipeline

Characters as build artifacts: committed specs + pinned models + these scripts
deterministically produce game-ready GLBs. Full design: `docs/character-pipeline.md`.

## Current scope (M1)

Stage S2 only — input image → textured mesh GLB via local TRELLIS.2:

```bash
uv run art-pipeline torga --image characters/torga/some_test_image.png
```

Requires the local ComfyUI server running (Desktop "ComfyUI" shortcut, or
`run_nvidia_gpu.bat` under the install root in `pipeline.toml`). Output lands
in `characters/<name>/build/<name>_mesh.glb` (gitignored — approved results
get promoted to `WoadRaiders.Client/assets/characters/` by later stages).

Once a character has an approved, committed `style_anchor.png` (stage S1, M2),
the `--image` flag becomes unnecessary.

## Layout

- `characters/<name>/spec.toml` — the character as data: seed, budgets, clips
- `workflows/*.json` — ComfyUI workflows in API format (the execution contract)
- `models.lock.json` — every AI model/node pinned to an exact revision
- `pipeline.toml` — machine settings (server URL, install root)
- `art_pipeline/` — the orchestrator package (`uv run art-pipeline <char>`)

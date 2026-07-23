# Third-party asset provenance

Required by `REALM-C-023` ([docs/realms/CONSTITUTION.md](realms/CONSTITUTION.md)).
One row per vendored asset directory: where it came from, under what licence, and
when. Licences on free-asset sites are mutable and several in this list have
already changed platform or terms; CC0's irrevocability only protects what you
can prove you obtained under it, so each directory keeps the licence text it
shipped with alongside the files.

Nothing in this list requires in-game attribution. The Poly Pizza models are
credited in their own `ATTRIBUTION.md` as a courtesy, not an obligation.

## 3D kits

| Directory | Pack | Author | Licence | Licence text shipped | Obtained |
| --- | --- | --- | --- | --- | --- |
| `WoadRaiders.Client/addons/kaykit_character_pack_adventures` | Adventurers Character Pack 1.0 | Kay Lousberg | CC0¹ | `LICENSE.txt` | before 2026-07-21 |
| `WoadRaiders.Client/addons/kaykit_character_pack_skeletons` | Skeletons Character Pack 1.0 | Kay Lousberg | CC0¹ | `LICENSE.txt` | before 2026-07-21 |
| `WoadRaiders.Client/addons/kaykit_dungeon_remastered` | Dungeon Remastered 1.0 (245 pieces) | Kay Lousberg | CC0¹ | `Assets/LICENSE.txt` | before 2026-07-21 |
| `WoadRaiders.Client/assets/crypt/kaykit_halloween` | Halloween Bits 1.0 | Kay Lousberg | CC0¹ | `LICENSE.txt` | before 2026-07-21 |
| `WoadRaiders.Client/assets/crypt/kenney_graveyard` | Graveyard Kit 5.0 | Kenney | CC0 | `License.txt` | before 2026-07-21 |
| `WoadRaiders.Client/assets/crypt/polypizza` | 5 individual models | Kay Lousberg, Quaternius | CC0 | `ATTRIBUTION.md` (per-model source pages) | before 2026-07-21 |

¹ KayKit pages state "CC0 Licensed — free for personal and commercial use, no
attribution required" but add "please don't resell unmodified copies or claim
them as your own". CC0 1.0 is an unconditional waiver and cannot carry that
rider, so treat it as an extra-legal request rather than a licence term. It does
not bind this project, which ships the assets inside a game rather than reselling
the pack.

## PBR textures

Vendored 2026-07-21 under `WoadRaiders.Client/assets/crypt/pbr/<surface>/`, one
directory per surface in `LOOK-001`'s manifest. Every Poly Haven file was checked
against the md5 its API publishes; the ambientCG sets arrive as zips and only the
four maps the realm uses were kept, so their directories are smaller than the
download. The NIGHT SKY is procedural too, and was not always: a CC0 HDRI panorama
(Poly Haven `qwantani_night_puresky`) was vendored and then removed, because a
photograph of the night sky is mostly airglow and that broad glow leaked
through every crack in the masonry as grey daylight. A generated starfield
separates the stars from the sky behind them, which an image cannot.

Two surfaces in the manifest stay **procedural** on purpose — `bone`,
because no CC0 bone material exists (searched), and `quartz`, which wants faint
emission no photograph carries.

All are imported **VRAM Compressed** with **mipmaps generated**, normals in
**Normal Map** mode (BC5), and `detect_3d/compress_to=0` so Godot does not rewrite
the sidecar the first time a texture is used in 3D.

| Directory | Source | Asset | Res | Maps | On disk | Bound to |
| --- | --- | --- | --- | ---: | ---: | --- |
| `ashlar_wall` | Poly Haven | [`medieval_blocks_03`](https://polyhaven.com/a/medieval_blocks_03) | 2K | arm+diff+nor | 10.7 MB | Era III walls |
| `ashlar_floor` | Poly Haven | [`floor_pattern_01`](https://polyhaven.com/a/floor_pattern_01) | 2K | arm+diff+nor | 1.9 MB | Era III floors |
| `vault_soffit` | Poly Haven | [`castle_brick_07`](https://polyhaven.com/a/castle_brick_07) | 1K | arm+diff+nor | 1.8 MB | Era III groin vaults |
| `dressing` | Poly Haven | [`large_sandstone_blocks_01`](https://polyhaven.com/a/large_sandstone_blocks_01) | 1K | arm+diff+nor | 1.7 MB | — not yet bound |
| `crossslab` | Poly Haven | [`white_sandstone_bricks_03`](https://polyhaven.com/a/white_sandstone_bricks_03) | 1K | arm+diff+nor | 1.3 MB | — not yet bound |
| `drystone` | Poly Haven | [`castle_brick_01`](https://polyhaven.com/a/castle_brick_01) | 2K | arm+diff+nor | 7.2 MB | Era II walls |
| `flagstone` | Poly Haven | [`cobblestone_floor_08`](https://polyhaven.com/a/cobblestone_floor_08) | 2K | arm+diff+nor | 6.7 MB | Era II floors |
| `orthostat` | Poly Haven | [`rock_face_03`](https://polyhaven.com/a/rock_face_03) | 2K | arm+diff+nor | 11.0 MB | Era I walls |
| `iron` | Poly Haven | [`rust_coarse_01`](https://polyhaven.com/a/rust_coarse_01) | 1K | arm+diff+nor | 2.3 MB | — not yet bound |
| `grate` | Poly Haven | [`metal_grate_rusty`](https://polyhaven.com/a/metal_grate_rusty) | 1K | arm+diff+nor | 2.6 MB | — not yet bound |
| `lintel` | ambientCG | [`Rocks025`](https://ambientcg.com/view?id=Rocks025) | 1K | ao+diff+nor+rough | 4.8 MB | — not yet bound |
| `cairn_rubble` | ambientCG | [`Gravel043`](https://ambientcg.com/view?id=Gravel043) | 1K | ao+diff+nor+rough | 5.3 MB | Era I floors |
| `moss` | ambientCG | [`Moss001`](https://ambientcg.com/view?id=Moss001) | 1K | ao+diff+nor+rough | 5.9 MB | — not yet bound |
| `grass` | ambientCG | [`Grass004`](https://ambientcg.com/view?id=Grass004) | 1K | ao+diff+nor+rough | 5.8 MB | the night surface the Crypt is sunk into |
| | | | | **14 sets** | **69.1 MB** | |

Sources, verified 2026-07-21:

| Source | Licence | Verified at | Notes |
| --- | --- | --- | --- |
| [Poly Haven](https://polyhaven.com/license) | CC0, no attribution, redistribution permitted | polyhaven.com/license | Ships an `arm` map that is bit-for-bit Godot's ORM channel order (R=AO, G=Rough, B=Metal). Use the **`nor_gl`** normal — `nor_dx` inverts lighting on every carved edge. **JPG only:** a 2K PNG normal is 25.6 MB against 3.85 MB for the JPG. |
| [ambientCG](https://docs.ambientcg.com/license/) | CC0 1.0, no attribution | docs.ambientcg.com/license | Ships separate `_Color` / `_NormalGL` / `_Roughness` / `_AmbientOcclusion` files. Real-world tile size is published per asset, which sets the triplanar grain. |
| [TextureCan](https://www.texturecan.com/terms/) | CC0 1.0, redistribution with your project explicitly permitted | texturecan.com/terms | Only for the two wall-grime decals; the Decals and Imperfection categories together hold ~14 assets. |

### Excluded, and why

| Source | Reason |
| --- | --- |
| FreePBR | Free tier is explicitly **non-commercial**; commercial use is a paid licence. Several aggregator articles wrongly call it CC0. |
| Poly Pizza (for new assets) | Site-wide licence is not stated; the catalogue mixes CC0 uploads with CC-BY inherited from the Google Poly archive, and the licence is not shown on the results grid. The five models already vendored were individually verified CC0. |
| Sketchfab | Store closed October 2024; Epic is withdrawing free downloadable content. Fine as a one-off source if the file is vendored and the licence page screenshotted, but not a dependency. |
| ShareTextures | Bespoke licence wearing a CC0 label — bars redistribution in collections and adds a "no violent content" clause. |
| 3DTexel | Handcrafted assets are CC0, but AI-generated output from their generators is under a separate proprietary licence. Requires per-asset verification. |

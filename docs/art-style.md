# Art style: the WoadRaiders look

**Painted heroic caricature** — a hand-painted, cel-shaded illustration style
with bold outlines, exaggerated stocky proportions, oversized readable
material textures, and one saturated accent colour per character against an
earthy steel-and-leather palette.

Derived 2026-08-02 from the approved Warrior anchor (the style enforcement
point per [character-pipeline.md](character-pipeline.md) S1: the anchor image
is what the mesh provider propagates into textures, so what this document
describes is what ships). It supersedes the soft-gouache notes and elaborates
the one-line "cel-shaded, bold outlines, flat colour" direction into rules.

## 1. The school

The lineage is heroic-fantasy caricature: Warcraft-poster body language
rendered with mobile-strategy key-art cleanliness. It is **illustration, not
simulation** — every material reads through paint decisions (value blocks,
specular dabs, drawn texture) rather than through physically based response.
Two shading vocabularies coexist deliberately: **hard-edged cel shapes** for
folds, occlusion and material boundaries, and **soft airbrushed gradients**
inside large simple forms (a helmet dome, a cloth panel). Neither photoreal
rendering nor painterly mush: crisp regions, confident edges, honest values.

## 2. Silhouette and proportions

- **About six heads tall** (a real adult is seven and a half), with the loss
  taken almost entirely in the legs. Torso and arms stay near-heroic; legs
  are short, thick and planted wide. This is the non-chibi register: adult
  silhouette, exaggerated bulk — never toddler proportions.
- **Top-heavy trapezoid.** Shoulders roughly two and a half head-widths;
  barrel chest; the figure widens upward. The belt line sits just below
  half-height and cinches the silhouette's one waist point.
- **Hands and feet oversized.** A gloved fist approaches head size; boots
  are wider than the knees above them. Forearms balloon toward the wrist
  before the glove cuff cuts them.
- **No neck to speak of** — the head seats into the collar.
- The silhouette must survive greyscale and thumbnail: one clean readable
  mass, nothing fiddly breaking the contour.

## 3. Shape language

Rounded-blocky. Masses are inflated rectangles and trapezoids with generous
corner radii; limbs taper in smooth curves. Crisp geometry is rationed to
hardware — the nasal helm's cone, the chunky buckle — so it reads as *made
metal* against organic bulk. No spikes, no tatters, no thin dangling straps:
every strap is wide, every prop is chunky, everything looks like it would
survive being dropped.

## 4. Line

- **Bold dark outline around the full exterior silhouette** — near-black
  warm brown (`#2A211B`-ish), never pure black. Heaviest weight on the
  contour, tapering slightly at curves.
- **Medium interior lines** separate materials and major forms (strap over
  mail, glove cuff over sleeve, belt over tabard).
- **Fine texture lines** (chainmail rings, cloth-fold creases) are drawn in
  a darker value of the *local* material colour, not in the outline colour —
  mail rings in deep blue-grey, cloth folds in crimson-brown. This keeps
  texture inside its region and reserves the darkest line for structure.
- Line is part of the texture, not a post-process. On 3D this arrives as
  painted edge-darkening propagated from the anchor — which is exactly how
  it should stay; no inverted-hull or screen-space outline pass.

## 5. Colour

Limited, earthy, value-driven. Saturation is a budget spent in exactly one
place per character.

| Role | Base | Shadow | Highlight |
|---|---|---|---|
| Steel mail | `#9FA9B4` blue-grey | `#55606E` | near-white ring dabs `#E8ECEF` |
| Leather (straps, belt, gloves, boots) | `#6E4B33` chocolate | `#452E1F` | `#93684A` |
| Accent cloth (Warrior: vermilion) | `#C4392A` | `#8C2418` | `#D9503C` |
| Helm / bronze hardware | `#A9A08A` warm steel | `#8A7A5E` | `#C2B69E` + white spec dots |
| Skin | `#D79E76` warm tan | `#B4714F` | ruddy blush on nose/cheeks |
| Hair / moustache | `#5C3D28` chestnut | — | `#7E5A3C` streaks |

Rules the palette encodes:

- **One hot accent per character** (the Warrior's vermilion tabard); every
  other region is desaturated earth or steel. The accent is the class
  identity colour and the eye's landing strip.
- **Value grouping does the silhouette's work**: metal and skin light, mail
  mid, leather dark. The figure reads in greyscale before colour arrives.
- Shadows shift slightly **cool**, highlights slightly **warm** — the same
  cool-dark/warm-light law already in the realm vertex paint.
- Colours are authored in sRGB, mid-saturation; nothing neon, nothing
  grimdark-desaturated.

## 6. Materials

Every material has a three-value recipe (base, shadow, highlight) plus one
signature move:

- **Chainmail** — the star material. Hand-drawn interlocking rings at
  roughly **ten times real scale**, so individual rings are legible at
  gameplay camera distance. Ring rows follow the form — curving around
  arms, compressing at joints — with per-ring white specular dabs scattered
  irregularly. Never a tiling photo pattern; the irregularity of the hand
  is the point.
- **Leather** — matte and simple: flat base, one hard-edged shadow tone,
  one restrained highlight, dark outline. No grain, no stitching detail;
  the outline weight *implies* the edge binding.
- **Plate / hardware** — soft airbrushed gradient across the large form,
  discrete white specular dots on rivets and rim. No environment
  reflection, no metallic shader response — metal-ness is painted.
- **Cloth** — the most saturated flat base on the figure, broken by a few
  **large** hard-edged fold shadows (three or four folds per panel, not
  twenty). Drawn crease lines in deep crimson-brown.
- **Skin** — softest shading on the figure: smooth gradients, warm blush
  accents, painted occlusion under helmet brim and brow.

Painted-in **ambient occlusion under every overlap** — under the belt, under
straps, inside the glove cuff, under the moustache — is what grounds the
paper-doll layers into one body.

## 7. Light and shading model

- **Single soft key from upper-front.** Highlights sit top-centre on domes,
  uniform on mail; no second light, no coloured rim, no cast ground shadow
  in presentation renders.
- Micro-lighting (occlusion, specular dabs, edge darkening) is **painted
  into the albedo**; macro lighting is **left to the engine**. This is the
  character-side expression of "honest light": the texture carries material
  identity and crevice shadow, the realm's dynamic flame and probes carry
  the actual illumination. Nothing that would fight a moving light — no
  baked directional shading across large forms, no painted hotspots that
  imply a sun.
- Engine translation (already in the pipeline's material flattening):
  metallic 0, roughness high, albedo-dominant. No normal maps doing
  photo-work; forms are geometry and paint.

## 8. Detail hierarchy

Detail density is a spotlight, not a wash: **face first** (the densest
painting on the figure), **belt buckle and chest-strap diamond second**
(oversized, ornamented, centred), **mail texture third** (uniform,
mid-density, never competing), **leather last** (near-flat). Ornament goes
where the eye should go — the same law as the realms' one-hero-per-space.

## 9. Texture scale

All surface texture is exaggerated well past realism so it survives the
gameplay camera: mail rings ~10×, fold widths in fingers not millimetres,
rivets the size of thumbs. If a texture element wouldn't read at
five metres, draw it bigger or leave it out. Noise, grunge, weathering
speckle and micro-scratches are banned — wear is expressed as shape (a
chunky repair, a strap notch), never as overlay grime.

## 10. Faces

Full caricature, played straight: heavy brow, small determined eyes with
visible iris colour, oversized nose, enormous expressive facial hair,
gritted or set mouth with drawn teeth. The face carries the character's
whole personality and gets the tightest painting on the model. Expression
defaults to **fierce resolve** — these are raiders, not mascots — but stays
on the warm side of grim: ruddy cheeks, not pallor.

## 11. Anchor presentation format

(Unchanged from the pipeline contract, restated so this doc stands alone.)
Full body, A- or T-pose, empty hands, single front view, flat white
background, no ground shadow.

## 12. What this style is not

- Not photo-PBR: no scanned textures, no metallic/roughness response, no
  environment reflections on characters.
- Not chibi: adult proportions exaggerated, never infantilised.
- Not grimdark: earthy but warm; one saturated accent always present.
- Not painterly-loose: brushwork stays inside crisp regions; no visible
  canvas texture, no impressionistic edges.
- Not busy: no grunge overlays, no micro-detail, no thin cluttered straps,
  no glow or emissive on characters.
- Not outlined in post: the line lives in the texture.

## 13. Prompt-ready summary

For nano banana anchors and any provider styling pass:

> Stylized heroic-caricature medieval character, hand-painted cel-shaded
> illustration, bold clean dark-brown outlines, flat colour blocks with soft
> painted shading, stocky exaggerated proportions (about six heads tall,
> massive shoulders, thick forearms, oversized gloves and boots, short
> sturdy legs), expressive caricatured face with heavy brow and oversized
> facial hair, hand-drawn oversized material textures (chainmail rings at
> exaggerated scale, large simple cloth folds), matte painted materials with
> white specular dabs on metal and painted occlusion under overlaps — no
> photorealism, no PBR reflections, no grunge or noise. Earthy limited
> palette: steel blue-grey, chocolate leather brown, warm bronze, one
> saturated accent colour. Full body, A-pose, empty hands, white
> background, single front view.

Per-character variation goes in the accent colour, the signature garment,
and the face — the palette discipline, proportions, line and material
recipes above are the invariants that make the roster read as one game.

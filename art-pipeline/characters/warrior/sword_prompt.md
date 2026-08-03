# Warrior sword — nano banana prompt

Inputs (attach in this order):

1. `style_anchor.png` — the approved Warrior anchor (style reference)
2. `sword_sketch.jpg` — the marker sketch (design reference; source
   `IMG_6737.heic`, which is JPEG data despite the extension)

Design read from the sketch: broad straight double-edged blade with parallel
edges and an angular peaked tip; a single centre line (rendered as a raised
median ridge); a crossguard that rolls into scroll spirals at both ends
(echoes the belt buckle's scrollwork); short one-handed grip with a
criss-cross cord wrap; fat solid round pommel. The stubby ~4.5:1
blade proportions are kept — they already match the style bible's chunky
shape language ([docs/art-style.md](../../../docs/art-style.md)).

## Prompt

> Using the character illustration (image 1) as the exact style reference
> and the marker sketch (image 2) as the design reference, redraw the
> sketched sword as a finished game-asset illustration in the identical
> hand-painted cel-shaded style as the character.
>
> Keep the sketch's design faithfully: a massive, broad, straight
> double-edged short sword with parallel edges and a thick angular peaked
> tip; a raised ridge running down the centre of the blade; a rolled
> crossguard that curls into scroll spirals at both ends; a short
> one-handed grip wrapped in criss-crossing leather cord; and a fat round
> pommel. Exaggerate the chunkiness — the blade nearly as wide as the
> character's forearm, every part thick and heavy, like it would survive
> being dropped. Keep the sketch's stubby blade proportions; do not
> lengthen it into a slender longsword.
>
> Style rules from the character: bold clean dark-brown outline around the
> silhouette with thinner interior lines in darker local colours; flat
> colour blocks with soft painted shading. The blade in steel blue-grey
> (base #9FA9B4, shadow #55606E), the two faces of the blade split
> light/dark along the centre ridge, with small white specular dabs. The
> crossguard and pommel in warm bronze (#A9A08A, shadow #8A7A5E) with
> white specular dots on the scroll curls, matching the character's helmet
> band and belt buckle. The grip wrap in chocolate leather brown (#6E4B33,
> shadow #452E1F), with one thin vermilion-red cord binding (#C4392A) at
> the collar just below the guard, matching the character's tabard. One or
> two chunky nicks in the blade edge drawn as clean painted shapes — no
> grime, no scratches, no noise, no weathering texture.
>
> Matte painted materials only: no photorealism, no PBR or environment
> reflections, no glow or emissive, no lens effects.
>
> Presentation: the sword alone, upright, tip up, perfectly vertical,
> straight-on side view, the whole object in frame, flat white background,
> no hand, no ground shadow, no text, no watermark.

## Notes

- The output doubles as the Meshy prop input (white background, single
  full view — same contract as character anchors). Weapons are separate
  meshes attached to hand bones per the `CharacterLoadout` contract
  (character-pipeline.md, open decision 3).
- The vermilion cord accent is the Warrior's class colour. For other
  classes' weapons, swap that one accent; steel/bronze/leather recipes
  stay invariant.
- Approved output: commit as `sword_anchor.png` beside `style_anchor.png`
  (reproducibility rule 5).

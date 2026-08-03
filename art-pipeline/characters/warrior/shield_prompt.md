# Warrior shield — nano banana prompt

Inputs (attach in this order):

1. `style_anchor.png` — the approved Warrior anchor (style reference)
2. `shield_sketch.jpg` — the pencil sketch (design reference; source
   `80739143430__4220A735-….heic`, JPEG data despite the extension)

Design read from the sketch: a Norman kite shield — rounded top tapering
to a point at the base; a wide rim band around the full perimeter studded
with a ring of round rivets; a domed round boss set high in the upper
third; a cross radiating from the boss that quarters the face, with
diagonally opposite fields shaded — read as a counterchanged two-colour
quartering. The kite shape pairs with the nasal helm and mail; the
quartered field is the class heraldry canvas
([docs/art-style.md](../../../docs/art-style.md)).

## Prompt

> Using the character illustration (image 1) as the exact style reference
> and the pencil sketch (image 2) as the design reference, redraw the
> sketched shield as a finished game-asset illustration in the identical
> hand-painted cel-shaded style as the character.
>
> Keep the sketch's design faithfully: a Norman kite shield — broad
> rounded top tapering to a point at the bottom; a thick raised rim band
> running around the entire edge, studded with a ring of large round
> rivets; a big domed round boss set high on the face, in the upper third;
> and a cross of flat straps radiating from the boss that divides the face
> into four fields. Paint the fields counterchanged: the two diagonally
> opposite fields in vermilion red, the other two in plain off-white.
> Exaggerate the chunkiness — a thick heavy rim, thumb-sized rivets, a
> fist-sized boss — solid and heavy, like it would survive being dropped.
>
> Style rules from the character: bold clean dark-brown outline around the
> silhouette with thinner interior lines in darker local colours; flat
> colour blocks with soft painted shading from a single soft light above.
> The vermilion fields in the character's tabard red (base #C4392A, shadow
> #8C2418); the off-white fields in warm parchment white (base #EAE3D4,
> shadow #C9BFA8); the boss, cross straps and rivets in warm bronze
> (#A9A08A, shadow #8A7A5E) with a single white specular dab on the boss
> dome and on each rivet, matching the character's helmet band and belt
> buckle; the rim band wrapped in chocolate leather brown (#6E4B33, shadow
> #452E1F). Painted soft occlusion where the face meets the rim and under
> the boss. One or two chunky notches in the rim drawn as clean painted
> shapes — no grime, no scratches, no noise, no weathering texture, no
> wood grain.
>
> Matte painted materials only: no photorealism, no PBR or environment
> reflections, no glow or emissive, no lens effects.
>
> Presentation: the shield alone, upright with the point down, perfectly
> vertical, straight-on front view, the whole object in frame, flat white
> background, no arm, no straps visible behind it, no ground shadow, no
> text, no watermark.

## Notes

- Straight-on front view keeps the anchor/prop contract; the boss dome
  and rim depth read through shading. If Meshy flattens the kite's
  lateral curve, add a slight three-quarter second view via the
  multi-image endpoint rather than changing this anchor.
- Vermilion is the Warrior's class accent; the counterchanged quartering
  is the roster-wide shield formula — other classes swap the accent
  colour, the bronze/leather/parchment recipes stay invariant.
- The output doubles as the Meshy prop input (white background, single
  full view). Weapons/shields are separate meshes attached to hand bones
  per the `CharacterLoadout` contract.
- Approved output: commit as `shield_anchor.png` beside
  `style_anchor.png` (reproducibility rule 5).

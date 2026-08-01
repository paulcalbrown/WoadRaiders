"""Render the fixed T-pose OpenPose keypoint template.

Every character's style anchor is generated against this exact skeleton
(stage S1, image2), so pose compliance is a conditioning constraint rather
than a prompt-engineering hope. The committed PNG is the contract; this
script is how it was made and how it changes:

    uv run python -m art_pipeline.pose_template
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw

SIZE = (768, 1024)  # portrait, matches typical anchor/sketch aspect

# OpenPose BODY-18 keypoints in a strict T-pose, front-facing. The person's
# right side sits on the image's left (mirror convention of a facing figure).
#
# Proportions are anatomical, not decorative: the model draws the figure the
# skeleton prescribes. Every landmark derives from average-human segment
# fractions (Drillis & Contini) for a figure of height H = 612 px standing
# on ground y = 819 (crown ~207, so the figure fills the canvas with margin):
#   eye level 0.936H -> y 246      shoulder height 0.818H -> y 318
#   hip height 0.530H -> y 495     knee 0.285H -> y 645    ankle 0.039H -> y 795
#   biacromial width 0.23H -> +/-70
#   FINGERTIP span = H (the "armspan equals height" rule) minus one hand
#   (0.108H) per side puts OpenPose wrists at +/-240; elbows split the arm
#   0.186:0.146 (upper:forearm) -> +/-165.
# v1 had wrist span 0.75H (rendered stretched); v2 overcorrected to wrist
# span = H, forgetting hands are not wrists (rendered long-armed).
KEYPOINTS = {
    "nose": (384, 258),
    "neck": (384, 318),
    "r_shoulder": (314, 318),
    "r_elbow": (219, 318),
    "r_wrist": (144, 318),
    "l_shoulder": (454, 318),
    "l_elbow": (549, 318),
    "l_wrist": (624, 318),
    "r_hip": (344, 495),
    "r_knee": (344, 645),
    "r_ankle": (340, 795),
    "l_hip": (424, 495),
    "l_knee": (424, 645),
    "l_ankle": (428, 795),
    "r_eye": (369, 246),
    "l_eye": (399, 246),
    "r_ear": (354, 258),
    "l_ear": (414, 258),
}

# (start, end, RGB) — the standard OpenPose limb palette, so any tool (or
# model) trained on OpenPose renders reads this image natively.
LIMBS = [
    ("neck", "r_shoulder", (255, 85, 0)),
    ("neck", "l_shoulder", (255, 170, 0)),
    ("r_shoulder", "r_elbow", (255, 255, 0)),
    ("r_elbow", "r_wrist", (170, 255, 0)),
    ("l_shoulder", "l_elbow", (85, 255, 0)),
    ("l_elbow", "l_wrist", (0, 255, 0)),
    ("neck", "r_hip", (0, 255, 85)),
    ("r_hip", "r_knee", (0, 255, 170)),
    ("r_knee", "r_ankle", (0, 255, 255)),
    ("neck", "l_hip", (0, 170, 255)),
    ("l_hip", "l_knee", (0, 85, 255)),
    ("l_knee", "l_ankle", (0, 0, 255)),
    ("neck", "nose", (255, 0, 0)),
    ("nose", "r_eye", (85, 0, 255)),
    ("r_eye", "r_ear", (170, 0, 255)),
    ("nose", "l_eye", (255, 0, 255)),
    ("l_eye", "l_ear", (255, 0, 170)),
]

JOINT_COLORS = {
    "nose": (255, 0, 0),
    "neck": (255, 85, 0),
    "r_shoulder": (255, 170, 0),
    "r_elbow": (255, 255, 0),
    "r_wrist": (170, 255, 0),
    "l_shoulder": (85, 255, 0),
    "l_elbow": (0, 255, 0),
    "l_wrist": (0, 255, 85),
    "r_hip": (0, 255, 170),
    "r_knee": (0, 255, 255),
    "r_ankle": (0, 170, 255),
    "l_hip": (0, 85, 255),
    "l_knee": (0, 0, 255),
    "l_ankle": (85, 0, 255),
    "r_eye": (170, 0, 255),
    "l_eye": (255, 0, 255),
    "r_ear": (255, 0, 170),
    "l_ear": (255, 0, 85),
}


def render() -> Image.Image:
    img = Image.new("RGB", SIZE, (0, 0, 0))
    draw = ImageDraw.Draw(img)
    for a, b, color in LIMBS:
        draw.line([KEYPOINTS[a], KEYPOINTS[b]], fill=color, width=10)
    for name, (x, y) in KEYPOINTS.items():
        r = 7
        draw.ellipse([x - r, y - r, x + r, y + r], fill=JOINT_COLORS[name])
    return img


def main() -> None:
    dest = Path(__file__).resolve().parent.parent / "workflows" / "tpose_openpose.png"
    render().save(dest)
    print(f"wrote {dest}")


if __name__ == "__main__":
    main()

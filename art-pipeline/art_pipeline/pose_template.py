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
KEYPOINTS = {
    "nose": (384, 170),
    "neck": (384, 250),
    "r_shoulder": (322, 250),
    "r_elbow": (216, 250),
    "r_wrist": (110, 250),
    "l_shoulder": (446, 250),
    "l_elbow": (552, 250),
    "l_wrist": (658, 250),
    "r_hip": (346, 470),
    "r_knee": (346, 650),
    "r_ankle": (346, 830),
    "l_hip": (422, 470),
    "l_knee": (422, 650),
    "l_ankle": (422, 830),
    "r_eye": (369, 158),
    "l_eye": (399, 158),
    "r_ear": (354, 170),
    "l_ear": (414, 170),
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

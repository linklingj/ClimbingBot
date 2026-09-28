"""The image half of the VLM input (docs/04): route holds with their ids, plus the body overlay.

Stands in for the annotated wall photo that Phase 3's CV output will provide.
"""
from __future__ import annotations

import io

from PIL import Image, ImageDraw, ImageFont

from .candidates import Pose
from .scene import Scene

_LABEL = {"left_hand": "LH", "right_hand": "RH", "left_foot": "LF", "right_foot": "RF"}
_FILL = {"green": (90, 190, 90), "red": (220, 80, 60), "white": (225, 225, 230)}


def render(scene: Scene, pose: Pose, candidates: dict[str, list[int]], px_per_m: int = 160) -> bytes:
    w, h = int(scene.width * px_per_m), int(scene.height * px_per_m)
    image = Image.new("RGB", (w, h), (55, 58, 66))
    draw = ImageDraw.Draw(image)
    font = ImageFont.load_default(size=max(11, px_per_m // 11))
    radius = max(6, int(0.07 * px_per_m))
    route = set(scene.route.hold_ids)
    candidate_ids = {hid for ids in candidates.values() for hid in ids}

    def xy(position) -> tuple[float, float]:
        return position[0] * px_per_m, h - position[1] * px_per_m

    for hold in scene.holds:
        cx, cy = xy(hold.position)
        on_route = hold.id in route
        fill = _FILL.get(hold.color, (150, 150, 150)) if on_route else (95, 95, 100)
        draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=fill)
        if hold.id in candidate_ids:  # reachable right now
            r = radius + 6
            draw.ellipse((cx - r, cy - r, cx + r, cy + r), outline=(255, 205, 70), width=3)
        draw.text((cx + radius + 9, cy - radius), str(hold.id), fill=(240, 240, 240), font=font)

    # Hands then feet the long way round, so the four lines read as a torso and not as a bow tie.
    held = [(limb, scene.position(pose[limb]))
            for limb in ("left_hand", "right_hand", "right_foot", "left_foot")]
    for i, (_, position) in enumerate(held):  # torso/limb lines, just enough to read the body
        draw.line([xy(position), xy(held[(i + 1) % 4][1])], fill=(80, 160, 255), width=2)
    labels: dict[int, list[str]] = {}
    for limb, position in held:
        cx, cy = xy(position)
        draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), outline=(80, 160, 255), width=4)
        labels.setdefault(pose[limb], []).append(_LABEL[limb])
    # One label per hold, joined: two hands can match on one hold, and two labels drawn at the same
    # point overlap into something unreadable -- on the top hold, exactly where it matters most.
    for hold_id, names in labels.items():
        cx, cy = xy(scene.position(hold_id))
        draw.text((cx - radius, cy + radius + 2), "+".join(names), fill=(120, 190, 255), font=font)

    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()

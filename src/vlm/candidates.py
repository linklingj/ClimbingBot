"""Candidate Generator (docs/04): "what is possible?", before the VLM picks one.

Distance thresholds off the current body state, which is what docs/04 asks the first version to be.
Reach ellipses, torso orientation and IK feasibility are the documented follow-ups.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

from .scene import FEET, HANDS, LIMBS, Scene

Pose = dict[str, int]  # limb -> hold id, all four limbs always present (docs/07)

OPPOSITE = {"left_hand": "right_hand", "right_hand": "left_hand",
            "left_foot": "right_foot", "right_foot": "left_foot"}


@dataclass(frozen=True)
class ReachModel:
    """Metres, for the Unity ragdoll's proportions -- these are the knobs to turn if the RL
    controller keeps failing moves the generator called reachable, or if it clears moves the
    generator refused. Numbers from a ~1.7 m climber, not measured against the rig yet."""

    hand_step: float = 1.4  # how far a hand may travel in one move
    foot_step: float = 1.0
    torso: float = 0.55  # shoulder line above hip line
    shoulder_half: float = 0.20
    hip_half: float = 0.12
    cross_margin: float = 0.25  # how far a limb may reach past its opposite before it is crossed
    max_span: float = 2.4  # furthest hand-to-foot distance allowed after the move


def anchors(scene: Scene, pose: Pose, model: ReachModel = ReachModel()) -> dict[str, tuple[float, float]]:
    """Where each limb reaches from: shoulders and hips hung off the centre of the four contacts."""
    xs, ys = zip(*(scene.position(pose[limb]) for limb in LIMBS))
    cx, cy = sum(xs) / 4, sum(ys) / 4
    return {
        "left_hand": (cx - model.shoulder_half, cy + model.torso / 2),
        "right_hand": (cx + model.shoulder_half, cy + model.torso / 2),
        "left_foot": (cx - model.hip_half, cy - model.torso / 2),
        "right_foot": (cx + model.hip_half, cy - model.torso / 2),
    }


def candidates(
    scene: Scene,
    pose: Pose,
    model: ReachModel = ReachModel(),
    max_per_limb: int = 6,
) -> dict[str, list[int]]:
    """Reachable route holds per limb. A limb with no candidate is left out."""
    route = set(scene.route.hold_ids)
    held = {pose[limb] for limb in LIMBS}
    anchor = anchors(scene, pose, model)
    hip_y = anchor["left_foot"][1]
    shoulder_y = anchor["left_hand"][1]
    top = scene.position(scene.route.top_hold_id)

    out: dict[str, list[int]] = {}
    for limb in LIMBS:
        step = model.hand_step if limb in HANDS else model.foot_step
        here = scene.position(pose[limb])
        opposite_x = scene.position(pose[OPPOSITE[limb]])[0]
        found = []
        for hold in scene.holds:
            if hold.id not in route or hold.id in held:
                continue
            x, y = hold.position
            if math.dist(here, hold.position) > step:  # one limb, one step
                continue
            if limb in HANDS and y < hip_y:  # hands stay above the hips
                continue
            if limb in FEET and y > shoulder_y - 0.1:  # feet stay below the shoulders
                continue
            if limb.startswith("left") and x > opposite_x + model.cross_margin:
                continue
            if limb.startswith("right") and x < opposite_x - model.cross_margin:
                continue
            if _span(scene, {**pose, limb: hold.id}) > model.max_span:  # don't tear the body apart
                continue
            found.append(hold.id)
        # ponytail: sorted by progress towards the top so truncation keeps the useful ones. A
        # smarter ranking only matters once the prompt is provably too long.
        found.sort(key=lambda hid: math.dist(scene.position(hid), top))
        if found:
            out[limb] = found[:max_per_limb]
    return out


def _span(scene: Scene, pose: Pose) -> float:
    """Furthest hand-to-foot distance in a pose. The body only stretches so far, and the step
    distance alone does not see a hand pulling away from feet that stayed put."""
    return max(math.dist(scene.position(pose[hand]), scene.position(pose[foot]))
               for hand in HANDS for foot in FEET)


def initial_pose(scene: Scene) -> Pose:
    """Feet on the two lowest route holds, hands on the two nearest the start hold's height."""
    route = sorted((scene.hold(hid) for hid in scene.route.hold_ids), key=lambda h: h.position[1])
    if len(route) < 4:
        raise ValueError("a route needs at least four holds to stand on")
    start = scene.hold(scene.route.start_hold_ids[0])
    feet = sorted(route[:2], key=lambda h: h.position[0])
    hands = sorted(sorted(route[2:], key=lambda h: abs(h.position[1] - start.position[1]))[:2],
                   key=lambda h: h.position[0])
    return {
        "left_hand": hands[0].id,
        "right_hand": hands[1].id,
        "left_foot": feet[0].id,
        "right_foot": feet[1].id,
    }

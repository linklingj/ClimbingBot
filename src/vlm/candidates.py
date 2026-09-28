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

    hand_step: float = 1.6  # how far a hand may travel in one move
    foot_step: float = 1.3
    min_rise: float = 0.25  # how far the lowest hand must stay above the highest foot
    # How far a limb may reach past its opposite before the pair counts as crossed. 0 means a left
    # limb never sits right of its right partner (sharing one hold is still fine, x is equal then).
    # It is a tolerance knob, not a technique switch -- a cross-through needs a planner that can plan
    # its way out again, and step by step there is no backtracking.
    cross_margin: float = 0.0
    max_span: float = 2.4  # furthest hand-to-foot distance allowed after the move


def blocked_holds(pose: Pose, limb: str) -> set[int]:
    """Holds this limb may not move to: the one it is already on, and any hold that would leave the
    four limbs on fewer than three holds.

    Matching -- two limbs on one hold -- is allowed between limbs of the same kind: hand/hand (Unity
    clears the route only when *both* hands are on the top hold, ClimberRagdoll.IsToppedOut, so this
    is how a plan finishes) and foot/foot. **Hand/foot is not**, matching the prompt.

    Also forbidden is all four limbs on two holds -- not a position a climber hangs in, and greedy
    went there on 22% of its poses when nothing stopped it.
    """
    blocked = {pose[limb]}
    for other in LIMBS:
        same_kind = (other in HANDS) == (limb in HANDS)
        if other == limb:
            continue
        if not same_kind or len(set({**pose, limb: pose[other]}.values())) < 3:
            blocked.add(pose[other])
    return blocked


def rise(scene: Scene, pose: Pose) -> float:
    """How far the lowest hand sits above the highest foot. Negative means a foot is above a hand.

    This replaced a shoulder/hip line hung off the centre of the four contacts. That line let a foot
    sit level with a hand (it was measured against the centroid, not against the hands) while
    refusing a foot 2 cm over it, which is both halves of the same bug.
    """
    return (min(scene.position(pose[hand])[1] for hand in HANDS)
            - max(scene.position(pose[foot])[1] for foot in FEET))


def rejection(scene: Scene, pose: Pose, limb: str, hold_id: int,
              model: ReachModel = ReachModel()) -> str | None:
    """Why `limb` may not move to `hold_id` from `pose`, in the words of the rule that refused it,
    or None if it may. Occupancy is `blocked_holds`; this is the geometry.

    candidates() and planner.validate() both read the answer here, so a rejected move is explained
    by the filter that actually stopped it. Reporting every refusal as "out of reach" hid a 2 mm
    crossing overshoot on wall 7 behind a distance message, and no amount of `stretch` moved it.
    """
    x, y = scene.position(hold_id)
    here = scene.position(pose[limb])
    hand = limb in HANDS
    step = model.hand_step if hand else model.foot_step
    distance = math.dist(here, (x, y))
    if distance > step:
        return (f"out of reach: {distance:.2f} m from hold {pose[limb]}, and a "
                f"{'hand' if hand else 'foot'} moves at most {step:.2f} m")
    if hand and y < here[1] - 1e-9:
        return f"hands do not climb down: hold {hold_id} is {here[1] - y:.2f} m below hold {pose[limb]}"
    after = rise(scene, {**pose, limb: hold_id})
    # A hand above every foot is the open stance. Not an absolute floor: some start poses are already
    # under it, and refusing every move from there would strand the climber -- so a move that does
    # not make the rise worse is allowed through.
    if after < model.min_rise and after < rise(scene, pose):
        return (f"hands must stay above the feet: after the move the lowest hand is {after:.2f} m "
                f"above the highest foot, under the {model.min_rise:.2f} m the stance needs")
    opposite_x = scene.position(pose[OPPOSITE[limb]])[0]
    crossed = x > opposite_x + model.cross_margin if limb.startswith("left") \
        else x < opposite_x - model.cross_margin
    if crossed:
        side = "left" if limb.startswith("left") else "right"
        return (f"crossed over {OPPOSITE[limb]}: x {x:.2f} m against its {opposite_x:.2f} m, and the "
                f"{side} limb stays on the {side}")
    span = _span(scene, {**pose, limb: hold_id})
    if span > model.max_span:
        return f"body too stretched: hand to foot {span:.2f} m against a {model.max_span:.2f} m limit"
    return None


def candidates(
    scene: Scene,
    pose: Pose,
    model: ReachModel = ReachModel(),
    max_per_limb: int = 6,
) -> dict[str, list[int]]:
    """Reachable route holds per limb. A limb with no candidate is left out."""
    route = set(scene.route.hold_ids)
    top = scene.position(scene.route.top_hold_id)

    out: dict[str, list[int]] = {}
    for limb in LIMBS:
        blocked = blocked_holds(pose, limb)
        found = [hold.id for hold in scene.holds
                 if hold.id in route and hold.id not in blocked
                 and rejection(scene, pose, limb, hold.id, model) is None]
        # ponytail: truncate by progress towards the top so the cut keeps the useful ones. A smarter
        # ranking only matters once the prompt is provably too long.
        found.sort(key=lambda hid: math.dist(scene.position(hid), top))
        if found:
            # Then hand them over nearest-first. The prompt asks for the nearest hold that gains
            # height, and a list that led with the furthest one was arguing the opposite.
            here = scene.position(pose[limb])
            out[limb] = sorted(found[:max_per_limb],
                               key=lambda hid: math.dist(here, scene.position(hid)))
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

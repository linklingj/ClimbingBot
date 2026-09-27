"""Offline check of the planner loop: PYTHONPATH=src python -m vlm.selftest

Covers the exported walls, the reach filter, the validator and the plan loop with the greedy
chooser. The Gemini path is one method behind the same interface, so what is left untested here is
the API call. The walls themselves are Unity's -- these checks assert what this side needs from
them, which is how a bad re-export gets caught.
"""
from __future__ import annotations

import math

from .candidates import LIMBS, ReachModel, anchors, candidates, initial_pose
from .planner import build_payload, move_schema, plan, validate
from .providers import GreedyChooser, strict_schema
from .render import render
from .scene import FEET, HANDS, MAX_REACH, Scene, wall, wall_paths

WALLS = range(len(wall_paths()))


def check_scene():
    for seed in WALLS:
        scene = wall(seed)
        ids = [h.id for h in scene.holds]
        assert len(set(ids)) == len(ids), "hold ids must be unique within a scene"
        assert scene.route.top_hold_id == max(scene.holds, key=lambda h: h.position[1]).id
        assert scene.route.start_hold_ids
        positions = [scene.position(hid) for hid in scene.route.hold_ids]
        # The foot line sits off the hand line, so "next by height" is no longer "adjacent" and the
        # consecutive-gap check stopped meaning anything. What the generator still promises is that
        # the climb connects: start to top in max_reach steps.
        assert _connected(scene, MAX_REACH + 1e-6), f"wall {seed}: top hold unreachable from the start"
        assert any(p[1] < scene.position(scene.route.start_hold_ids[0])[1] for p in positions), \
            f"wall {seed}: nothing below the start hold for the feet"
        for x, y in positions:
            assert 0 <= x <= scene.width and 0 <= y <= scene.height
        assert Scene.from_dict(scene.to_dict()).to_dict() == scene.to_dict(), "scene JSON round-trip"


def _connected(scene: Scene, reach: float) -> bool:
    """Is the top hold reachable from the start in steps of at most reach?"""
    seen, stack = set(), [scene.route.start_hold_ids[0]]
    while stack:
        hold_id = stack.pop()
        if hold_id in seen:
            continue
        seen.add(hold_id)
        here = scene.position(hold_id)
        stack += [h.id for h in scene.holds if h.id not in seen and math.dist(here, h.position) <= reach]
    return scene.route.top_hold_id in seen


def check_candidates():
    model = ReachModel()
    with_feet = 0
    for seed in WALLS:
        scene = wall(seed)
        pose = initial_pose(scene)
        assert len(set(pose.values())) == 4, "one hold per limb"
        cands = candidates(scene, pose, model)
        assert cands, f"wall {seed}: nothing reachable from the initial pose"
        with_feet += any(limb in cands for limb in FEET)
        anchor = anchors(scene, pose, model)
        for limb, ids in cands.items():
            assert ids and len(set(ids)) == len(ids)
            for hold_id in ids:
                assert hold_id not in pose.values(), "a held hold is not a candidate"
                assert hold_id in scene.route.hold_ids, "candidates come from the route"
                step = model.hand_step if limb in HANDS else model.foot_step
                assert math.dist(scene.position(pose[limb]), scene.position(hold_id)) <= step + 1e-9
                y = scene.position(hold_id)[1]
                assert y >= anchor["left_foot"][1] if limb in HANDS else y <= anchor["left_hand"][1]

    # Why the foot line exists: re-exported with footDropY 0, the feet have a move on 0 of these
    # walls -- every hold within a foot's step is one another limb is already on -- and greedy tops
    # out on 6/20 instead of 16/20. The dropped line takes the feet to 11/20. Still not most walls,
    # so this guards the floor rather than the number (docs/05 has why it is only partial).
    assert with_feet >= len(WALLS) // 3, f"feet only had a move on {with_feet}/{len(WALLS)} walls"
    print(f"  feet had a move from the initial pose on {with_feet}/{len(WALLS)} walls")


def check_validator():
    scene = wall(1)
    pose = initial_pose(scene)
    cands = candidates(scene, pose)
    limb, hold_id = next(iter(cands.items()))
    hold_id = hold_id[0]

    assert validate({"moving_limb": limb, "target_hold_id": str(hold_id)}, scene, pose, cands) is None
    assert validate({"moving_limb": "tail", "target_hold_id": "1"}, scene, pose, cands)
    assert validate({"moving_limb": limb, "target_hold_id": "nope"}, scene, pose, cands)
    assert validate({"moving_limb": limb, "target_hold_id": "9999"}, scene, pose, cands)
    assert validate({"moving_limb": limb, "target_hold_id": str(pose[limb])}, scene, pose, cands)
    other = next(l for l in LIMBS if l != limb)
    assert validate({"moving_limb": limb, "target_hold_id": str(pose[other])}, scene, pose, cands)
    off = next(h.id for h in scene.holds if h.id not in cands[limb] and h.id not in pose.values())
    assert validate({"moving_limb": limb, "target_hold_id": str(off)}, scene, pose, cands)

    schema = move_schema(cands)
    assert schema["properties"]["moving_limb"]["enum"] == sorted(cands)
    every = {str(i) for ids in cands.values() for i in ids}
    assert set(schema["properties"]["target_hold_id"]["enum"]) == every
    strict = strict_schema(schema)
    assert "propertyOrdering" not in strict and strict["additionalProperties"] is False
    assert set(strict["required"]) == set(schema["properties"])
    assert strict["properties"] == schema["properties"], "the enums must survive the dialect swap"

    payload = build_payload(scene, pose, cands, error="nope")
    assert payload["goal"]["top_hold_id"] == scene.route.top_hold_id
    assert payload["previous_answer_rejected"] == "nope"


def check_plan():
    solved = 0
    for seed in WALLS:
        scene = wall(seed)
        result = plan(scene, GreedyChooser(scene), with_image=False, max_moves=60)
        assert result.invalid == 0, "the greedy chooser must never emit an invalid move"
        for move in result.moves:
            assert len(set(move.pose.values())) == 4
            targets = move.targets()["targets"]
            assert len(targets) == 4 and sum(t["move"] for t in targets) == 1
        solved += result.reached_top
        if not result.reached_top:
            print(f"  wall {seed}: {result.stopped}")
    assert solved >= len(WALLS) * 0.7, f"greedy baseline only solved {solved}/{len(WALLS)}"
    print(f"  greedy baseline reached the top on {solved}/{len(WALLS)} walls")


def check_render():
    scene = wall(0)
    pose = initial_pose(scene)
    png = render(scene, pose, candidates(scene, pose))
    assert png.startswith(b"\x89PNG") and len(png) > 2000


if __name__ == "__main__":
    for check in (check_scene, check_candidates, check_validator, check_plan, check_render):
        check()
        print(f"ok  {check.__name__}")

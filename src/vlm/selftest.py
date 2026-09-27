"""Offline check of the planner loop: PYTHONPATH=src python -m vlm.selftest

Covers the exported walls, the reach filter, the validator and the plan loop with the greedy
chooser. The Gemini path is one method behind the same interface, so what is left untested here is
the API call. The walls themselves are Unity's -- these checks assert what this side needs from
them, which is how a bad re-export gets caught.
"""
from __future__ import annotations

import math

from .candidates import LIMBS, ReachModel, anchors, blocked_holds, candidates, initial_pose
from .planner import build_payload, plan, plan_schema, validate
from .providers import ALIASES, GEMINI, GPT, GreedyChooser, chooser_for, strict_schema
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
                # A held hold is not a candidate, except the opposite limb's: hands match on one
                # hold, and so do feet.
                assert hold_id not in blocked_holds(pose, limb)
                assert hold_id in scene.route.hold_ids, "candidates come from the route"
                step = model.hand_step if limb in HANDS else model.foot_step
                assert math.dist(scene.position(pose[limb]), scene.position(hold_id)) <= step + 1e-9
                y = scene.position(hold_id)[1]
                assert y >= anchor["left_foot"][1] if limb in HANDS else y <= anchor["left_hand"][1]

    # 20/20 since the feet can match; it was 6/20 when a foot could not take the other foot's hold,
    # which is what the footDropY second pass was working around (docs/05 has why it is partial).
    # Kept as a floor rather than an equality -- it is the wall export this is really watching.
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
    # Matching: any two limbs may share, hand-foot included. Only the three-hold floor stops it, so
    # from a four-hold pose nothing but the limb's own hold is blocked. Checked on the rule rather
    # than through validate(), which would also reject a shared hold for being out of reach.
    assert len(set(pose.values())) == 4
    for limb_ in LIMBS:
        assert blocked_holds(pose, limb_) == {pose[limb_]}, "a four-hold pose blocks nothing else"
    matched = {**pose, "right_hand": pose["left_hand"]}  # hands matched: three holds left
    assert len(set(matched.values())) == 3
    # The two limbs already sharing may still move anywhere -- stepping off a match keeps three
    # holds. Only their own hold is out, and they may not re-take it.
    for limb_ in ("left_hand", "right_hand"):
        assert blocked_holds(matched, limb_) == {matched[limb_]}
    # The two that are NOT sharing are pinned: any hold in use would make it four limbs on two
    # holds. This is where the second match, of any kind, is refused.
    for limb_ in ("left_foot", "right_foot"):
        assert blocked_holds(matched, limb_) == set(matched.values())
    off = next(h.id for h in scene.holds if h.id not in cands[limb] and h.id not in pose.values())
    assert validate({"moving_limb": limb, "target_hold_id": str(off)}, scene, pose, cands)

    schema = plan_schema(scene, 12)
    move = schema["properties"]["moves"]["items"]
    assert schema["properties"]["moves"]["maxItems"] == 12
    assert move["properties"]["moving_limb"]["enum"] == sorted(LIMBS)
    # Every hold on the route, not just what is reachable: there is no candidate list in the prompt.
    assert set(move["properties"]["target_hold_id"]["enum"]) == {str(h) for h in scene.route.hold_ids}
    strict = strict_schema(schema)
    assert "maxItems" not in strict["properties"]["moves"], "strict mode rejects it"
    for obj in (strict, strict["properties"]["moves"]["items"]):
        assert obj["additionalProperties"] is False and set(obj["required"]) == set(obj["properties"])
    assert strict["properties"]["moves"]["items"]["properties"] == move["properties"], \
        "the enums must survive the dialect swap"

    payload = build_payload(scene, pose, ReachModel())
    assert payload["goal"]["top_hold_id"] == scene.route.top_hold_id
    assert len(payload["holds"]) == len(scene.route.hold_ids)
    assert payload["limits"]["hand_step"] > payload["limits"]["foot_step"] > 0
    assert "candidates" not in payload and "history" not in payload, "the model gets neither now"


def check_plan():
    """The greedy baseline, through the same one-shot interface the VLM uses: one request, the whole
    sequence, replayed. Greedy re-derives candidates inside its own rollout, so every move it writes
    must survive the replay -- an invalid one means the two have drifted apart."""
    solved = 0
    for seed in WALLS:
        scene = wall(seed)
        result = plan(scene, GreedyChooser(scene), with_image=False, max_moves=60)
        assert result.valid_move_rate == 1.0, \
            f"wall {seed}: greedy wrote a move its own rollout could not replay: {result.stopped}"
        assert result.proposed == result.examined, "greedy must not write past the top"
        for move in result.moves:
            # Any two limbs may share a hold; three distinct holds is the whole rule.
            assert len(set(move.pose.values())) >= 3, f"all four limbs on two holds: {move.pose}"
            targets = move.targets()["targets"]
            assert len(targets) == 4 and sum(t["move"] for t in targets) == 1
        solved += result.reached_top
        if result.reached_top:
            # What Unity calls topped out: both hands on the top hold (ClimberRagdoll.IsToppedOut).
            assert all(result.moves[-1].pose[hand] == scene.route.top_hold_id for hand in HANDS)
        else:
            print(f"  wall {seed}: {result.stopped}")
    # Measured 2026-09-28: 20/20, mean 21.6 moves. Matching is what lifts it -- hands only takes
    # greedy to 13/20, no matching at all to 10/20, and 0/20 once both hands are required on the top
    # hold. The feet are most of the gain: their step is short, so before matching the hold within
    # reach was usually the one the other foot was already on.
    assert solved >= 18, f"greedy baseline only solved {solved}/{len(WALLS)}"
    print(f"  greedy baseline reached the top on {solved}/{len(WALLS)} walls")


def check_aliases():
    """--model gpt / gemini are the short names the CLI takes; anything else passes through."""
    assert ALIASES == {"gemini": GEMINI, "gpt": GPT}
    assert GEMINI.startswith("gemini") and GPT.startswith("gpt")
    scene = wall(0)
    assert isinstance(chooser_for("greedy", scene), GreedyChooser)
    # The providers need API keys to construct, so this checks the routing, not the clients.
    from .providers import ALIASES as _a
    for name, expect in (("gemini", GEMINI), ("gpt", GPT), ("gpt-6-luna", "gpt-6-luna")):
        assert _a.get(name, name) == expect


class _Canned:
    """A chooser that answers with a sequence decided up front -- the model's job, without the
    model, so the replay half of the planner is testable offline."""

    name = "canned"

    def __init__(self, moves, route):
        self.moves, self.route, self.calls = moves, route, 0

    def choose(self, system, payload, schema, image_png):
        self.calls += 1
        assert "candidates" not in payload, "the payload must not hand over candidates"
        ids = set(schema["properties"]["moves"]["items"]["properties"]["target_hold_id"]["enum"])
        assert ids == {str(hid) for hid in self.route}, "the enum is the whole route"
        return {"moves": self.moves}


def check_replay():
    """Replaying greedy's own solution as a canned sequence must reach the top in one request. Then
    a sequence with an unavailable move must stop there rather than execute it."""
    scene = wall(0)
    route = list(scene.route.hold_ids)
    greedy = plan(scene, GreedyChooser(scene), with_image=False)
    assert greedy.reached_top
    canned = [{"reason": m.reason, "moving_limb": m.moving_limb,
               "target_hold_id": str(m.target_hold_id)} for m in greedy.moves]

    chooser = _Canned(canned, route)
    result = plan(scene, chooser, with_image=False)
    assert chooser.calls == 1, "one request for the whole route"
    assert result.reached_top and result.valid_move_rate == 1.0, result.stopped
    assert [m.target_hold_id for m in result.moves] == [m.target_hold_id for m in greedy.moves]

    # Out of reach: nothing retries and nothing falls back -- the plan stops, keeping the prefix.
    far = max(scene.holds, key=lambda h: math.dist(scene.position(route[0]), h.position))
    bad = canned[:2] + [{"reason": "", "moving_limb": "left_hand", "target_hold_id": str(far.id)}]
    result = plan(scene, _Canned(bad, route), with_image=False)
    assert not result.reached_top and len(result.moves) == 2 and result.examined == 3
    assert result.valid_move_rate == 2 / 3
    assert result.stopped.startswith("move 3 unusable"), result.stopped

    # A sequence that just runs out short of the top says so rather than claiming a stop reason.
    result = plan(scene, _Canned(canned[:2], route), with_image=False)
    assert not result.reached_top and "short of the top" in result.stopped

    # The image is rendered once and kept, so the caller can save exactly what was sent.
    result = plan(scene, _Canned(canned, route), with_image=True)
    assert result.request_png and result.request_png.startswith(b"\x89PNG")
    assert plan(scene, _Canned(canned, route), with_image=False).request_png is None


def check_render():
    scene = wall(0)
    pose = initial_pose(scene)
    png = render(scene, pose, candidates(scene, pose))
    assert png.startswith(b"\x89PNG") and len(png) > 2000


if __name__ == "__main__":
    for check in (check_scene, check_candidates, check_validator, check_aliases,
                  check_plan, check_replay, check_render):
        check()
        print(f"ok  {check.__name__}")

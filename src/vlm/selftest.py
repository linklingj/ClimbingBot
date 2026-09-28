"""Offline check of both planners: PYTHONPATH=src python -m vlm.selftest

Covers the exported walls, the reach filter, the validator, and both the step-by-step loop and the
one-shot replay with the greedy chooser. The Gemini path is one method behind the same interface,
so what is left untested here is the API call. The walls themselves are Unity's -- these checks assert what this side needs from
them, which is how a bad re-export gets caught.
"""
from __future__ import annotations

import math
from collections import deque

from .candidates import (LIMBS, ReachModel, blocked_holds, candidates, initial_pose,
                        rejection, rise)
from .planner import (SYSTEM_ONESHOT, SYSTEM_STEPS, build_payload, move_schema,
                      plan_oneshot, plan_schema, plan_steps, step_payload, validate)
from .providers import ALIASES, GEMINI, GPT, GreedyChooser, chooser_for, strict_schema
from .render import render
from .scene import FEET, HANDS, MAX_REACH, Scene, wall, wall_paths

WALLS = range(len(wall_paths()))

# Exported walls the rules cannot climb, checked as an exact set so a new one is caught. Empty since
# max_span went to 2.05 m: wall 22 needed a hand move with a foot 2.01 m away and was the only wall
# 2.0 m shut out.
UNCLIMBABLE: set[int] = set()


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
        # Two, not one: the climber starts with a foot on each, and initial_pose takes the two
        # lowest non-start holds -- with one hold down there the second foot lands above the hands
        # (seed 10 did exactly that). RandomWallGenerator.EnsureFootHolds is what promises this.
        start_y = min(scene.position(hid)[1] for hid in scene.route.start_hold_ids)
        below = [p for p in positions if p[1] < start_y - 1e-9]
        assert len(below) >= 2, f"wall {seed}: {len(below)} holds below the start line, need two feet"
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
        # The start stance stands, before anything else: both feet under both hands.
        assert rise(scene, pose) > 0, f"wall {seed}: the start pose has a foot above a hand"
        # The hands start on the start holds -- matched on the one hold when the route names one, and
        # left hand on the left one when it names two (initial_pose orders them by x, not by id).
        starts = sorted(scene.route.start_hold_ids[:2], key=lambda hid: scene.position(hid)[0])
        assert [pose[hand] for hand in HANDS] == (starts * 2 if len(starts) == 1 else starts)
        assert _holds_enough(scene, pose), "the start pose stands on at least three holds"
        cands = candidates(scene, pose, model)
        assert cands, f"wall {seed}: nothing reachable from the initial pose"
        with_feet += any(limb in cands for limb in FEET)
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
                if limb in HANDS:  # hands never take a lower hold than the one they are on
                    assert y >= scene.position(pose[limb])[1] - 1e-9
                # The stance: a hand above every foot, unless the pose was already under it.
                after = rise(scene, {**pose, limb: hold_id})
                assert after >= model.min_rise or after >= rise(scene, pose)

    # 5/20 now that both hands start matched on the one start hold: a foot match would put four
    # limbs on two holds, so most walls offer the feet nothing until a hand has moved off. It was
    # 20/20 with the hands on two holds. Kept as a floor -- it is the wall export this watches.
    assert with_feet >= 3, f"feet only had a move on {with_feet}/{len(WALLS)} walls"
    print(f"  feet had a move from the initial pose on {with_feet}/{len(WALLS)} walls")


def check_validator():
    scene = wall(1)
    pose = initial_pose(scene)
    cands = candidates(scene, pose)
    limb, hold_id = next(iter(cands.items()))
    hold_id = hold_id[0]

    assert validate({"moving_limb": limb, "target_hold_id": str(hold_id)}, scene, pose) is None
    assert validate({"moving_limb": "tail", "target_hold_id": "1"}, scene, pose)
    assert validate({"moving_limb": limb, "target_hold_id": "nope"}, scene, pose)
    assert validate({"moving_limb": limb, "target_hold_id": "9999"}, scene, pose)
    assert validate({"moving_limb": limb, "target_hold_id": str(pose[limb])}, scene, pose)
    # Matching is same-kind only: hand/hand and foot/foot, never hand/foot. Checked on the rule
    # rather than through validate(), which would also reject a shared hold the geometry refuses.
    # On four distinct holds -- the start pose has the hands matched, which is the case below.
    spread = {**pose, "right_hand": cands["right_hand"][0]}
    assert len(set(spread.values())) == 4
    for hand in HANDS:  # the other hand's hold is free, both feet's are not
        assert blocked_holds(spread, hand) == {spread[hand], spread["left_foot"], spread["right_foot"]}
    for foot in FEET:
        assert blocked_holds(spread, foot) == {spread[foot], spread["left_hand"], spread["right_hand"]}
    # The finishing match is the exception: the second hand may join the first on the top hold even
    # though that leaves four limbs on two holds. Any other hold, and the rule still refuses it.
    top = scene.route.top_hold_id
    finish = {"left_hand": top, "right_hand": 0, "left_foot": 1, "right_foot": 1}
    assert top not in blocked_holds(finish, "right_hand", top)
    assert top in blocked_holds(finish, "right_hand"), "only the top hold gets the exemption"
    assert finish["left_foot"] in blocked_holds(finish, "right_foot", top), "feet get no exemption"

    matched = {**spread, "right_hand": spread["left_hand"]}  # hands matched: three holds left
    assert len(set(matched.values())) == 3
    # A matched hand may still step off onto a free hold, but every hold in use is out: the feet's
    # by kind, its partner's because it is already standing there.
    for hand in HANDS:
        assert blocked_holds(matched, hand) == set(matched.values())
    # The feet are pinned: the hands' holds by kind, each other's because a second match would
    # leave four limbs on two holds.
    for foot in FEET:
        assert blocked_holds(matched, foot) == set(matched.values())
    off = next(h.id for h in scene.holds if h.id not in cands[limb] and h.id not in pose.values())
    assert validate({"moving_limb": limb, "target_hold_id": str(off)}, scene, pose)
    # The refusal names the rule that refused, not "out of reach" for everything: a hold in range
    # but on the wrong side of the body says so, which is what sent wall 7 chasing a reach bug.
    far = max(scene.holds, key=lambda h: math.dist(scene.position(pose[limb]), h.position))
    assert "out of reach" in rejection(scene, pose, limb, far.id)
    reasons = {rejection(scene, pose, l, h.id).split(":")[0].split(" (")[0]
               for l in LIMBS for h in scene.holds if rejection(scene, pose, l, h.id)}
    assert {"out of reach", "hands must stay above the feet"} <= reasons, reasons

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

    # The prompt states max_span as a number, so the number has to be the one the referee uses.
    assert f"{ReachModel().max_span} m" in SYSTEM_ONESHOT, "the metres in the prompt are not max_span"
    payload = build_payload(scene, pose, ReachModel())
    assert payload["limits"] == {"hand_step": ReachModel().hand_step,
                                 "foot_step": ReachModel().foot_step,
                                 "max_span": ReachModel().max_span}
    assert payload["goal"]["top_hold_id"] == scene.route.top_hold_id
    assert len(payload["holds"]) == len(scene.route.hold_ids)
    assert payload["limits"]["hand_step"] > payload["limits"]["foot_step"] > 0
    assert "candidates" not in payload and "history" not in payload, "the model gets neither now"


def check_oneshot():
    """The greedy baseline, through the same one-shot interface the VLM uses: one request, the whole
    sequence, replayed. Greedy re-derives candidates inside its own rollout, so every move it writes
    must survive the replay -- an invalid one means the two have drifted apart."""
    solved = 0
    for seed in WALLS:
        scene = wall(seed)
        result = plan_oneshot(scene, GreedyChooser(scene), with_image=False, max_moves=60)
        assert result.valid_move_rate == 1.0, \
            f"wall {seed}: greedy wrote a move its own rollout could not replay: {result.stopped}"
        assert result.proposed == result.examined, "greedy must not write past the top"
        for move in result.moves:
            # Any two limbs may share a hold; three distinct holds is the rule, bar the finish.
            assert _holds_enough(scene, move.pose), f"all four limbs on two holds: {move.pose}"
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
    assert solved >= 0.85 * len(WALLS), f"greedy baseline only solved {solved}/{len(WALLS)}"
    print(f"  greedy baseline reached the top on {solved}/{len(WALLS)} walls")


def check_steps():
    """The step-by-step planner, greedy standing in for the model: one request per move, candidates
    in the payload, re-planned from the new pose. The offered candidates are the only thing greedy
    can answer with, so a rejected answer here means the prompt's schema and the referee disagree."""
    solved = 0
    for seed in WALLS:
        scene = wall(seed)
        result = plan_steps(scene, GreedyChooser(scene), with_image=False, max_moves=60)
        assert result.mode == "steps" and result.requests == len(result.moves), \
            f"wall {seed}: {result.invalid} answers rejected -- greedy must only offer valid moves"
        assert result.invalid == 0 and result.valid_move_rate == 1.0
        assert not any(move.fell_back for move in result.moves), "the fallback must not be needed"
        assert result.request_png is None, "the step planner renders per move, not once"
        for move in result.moves:
            assert _holds_enough(scene, move.pose), f"all four limbs on two holds: {move.pose}"
            assert not _crossed(scene, move.pose), f"crossed limbs: {move.pose}"
            targets = move.targets()["targets"]
            assert len(targets) == 4 and sum(t["move"] for t in targets) == 1
        solved += result.reached_top
        if result.reached_top:
            assert all(result.moves[-1].pose[hand] == scene.route.top_hold_id for hand in HANDS)
        else:
            print(f"  wall {seed}: {result.stopped}")
    # A floor on beta quality, not on the rules: `check_solvable` is what says a wall can be climbed.
    # Greedy takes the largest gain it can see and has no way to plan an uncross, so it climbs into
    # dead ends on a few walls (48/50 on 2026-09-28, one of which is UNCLIMBABLE anyway).
    assert solved >= 0.85 * len(WALLS), f"greedy through the loop only solved {solved}/{len(WALLS)}"
    print(f"  greedy through the step-by-step loop reached the top on {solved}/{len(WALLS)} walls")


def _holds_enough(scene, pose) -> bool:
    """Three distinct holds, or the topped-out pose -- both hands on top may leave only two."""
    return (len(set(pose.values())) >= 3
            or all(pose[hand] == scene.route.top_hold_id for hand in HANDS))


def _crossed(scene, pose) -> list[str]:
    """Limb pairs whose left member sits right of its right member. Sharing a hold is not crossed."""
    return [left for left, right in (("left_hand", "right_hand"), ("left_foot", "right_foot"))
            if scene.position(pose[left])[0] > scene.position(pose[right])[0]]


def check_solvable():
    """Every exported wall must still have a way to the top under the current rules.

    This is the check the greedy solve count cannot be: greedy fails walls that are perfectly
    climbable, so its number measures greedy. A breadth-first search over poses measures the rules --
    if a rule change walls off a route, the shortest solution disappears here.
    """
    model = ReachModel()
    lengths: list[int] = []
    unclimbable: set[int] = set()
    for seed in WALLS:
        scene = wall(seed)
        top = scene.route.top_hold_id
        start = initial_pose(scene)
        seen = {tuple(sorted(start.items()))}
        queue = deque([(start, 0)])
        found = None
        while queue and found is None:
            pose, depth = queue.popleft()
            if depth >= 30:  # far past the ~15 moves the walls actually need
                continue
            for limb in LIMBS:
                blocked = blocked_holds(pose, limb)
                for hold_id in scene.route.hold_ids:
                    if hold_id in blocked or rejection(scene, pose, limb, hold_id, model):
                        continue
                    nxt = {**pose, limb: hold_id}
                    if all(nxt[hand] == top for hand in HANDS):
                        found = depth + 1
                        break
                    key = tuple(sorted(nxt.items()))
                    if key not in seen:
                        seen.add(key)
                        queue.append((nxt, depth + 1))
                if found:
                    break
        if found is None:
            unclimbable.add(seed)
        else:
            lengths.append(found)
    assert unclimbable == UNCLIMBABLE, \
        f"unclimbable walls changed: {sorted(unclimbable)} against {sorted(UNCLIMBABLE)}"
    print(f"  {len(lengths)}/{len(WALLS)} walls solvable under the rules, in "
          f"{min(lengths)}-{max(lengths)} moves (unclimbable: {sorted(UNCLIMBABLE)})")


def check_step_prompt():
    """The step-by-step payload and schema: candidates are both the offer and the enum."""
    scene = wall(1)
    pose = initial_pose(scene)
    cands = candidates(scene, pose)

    schema = move_schema(cands)
    assert schema["properties"]["moving_limb"]["enum"] == sorted(cands)
    every = {str(i) for ids in cands.values() for i in ids}
    assert set(schema["properties"]["target_hold_id"]["enum"]) == every, "the enum is the offer"
    strict = strict_schema(schema)
    assert "propertyOrdering" not in strict and strict["additionalProperties"] is False
    assert strict["properties"] == schema["properties"], "the enums must survive the dialect swap"

    payload = step_payload(scene, pose, cands, error="nope")
    assert payload["goal"]["top_hold_id"] == scene.route.top_hold_id
    assert payload["candidates"] == {limb: list(ids) for limb, ids in cands.items()}
    assert payload["history"] == [] and payload["previous_answer_rejected"] == "nope"
    # Both prompts state the same finish, because both planners referee the same one.
    assert "BOTH hands" in SYSTEM_STEPS and "BOTH hands" in SYSTEM_ONESHOT
    assert "candidates" in SYSTEM_STEPS, "the step prompt has to point at the offer it gets"


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
    greedy = plan_oneshot(scene, GreedyChooser(scene), with_image=False)
    assert greedy.reached_top
    canned = [{"moving_limb": m.moving_limb, "target_hold_id": str(m.target_hold_id)}
              for m in greedy.moves]

    chooser = _Canned(canned, route)
    result = plan_oneshot(scene, chooser, with_image=False)
    assert chooser.calls == 1, "one request for the whole route"
    assert result.reached_top and result.valid_move_rate == 1.0, result.stopped
    assert [m.target_hold_id for m in result.moves] == [m.target_hold_id for m in greedy.moves]

    # Out of reach: nothing retries and nothing falls back -- the plan stops, keeping the prefix.
    far = max(scene.holds, key=lambda h: math.dist(scene.position(route[0]), h.position))
    bad = canned[:2] + [{"moving_limb": "left_hand", "target_hold_id": str(far.id)}]
    result = plan_oneshot(scene, _Canned(bad, route), with_image=False)
    assert not result.reached_top and len(result.moves) == 2 and result.examined == 3
    assert result.valid_move_rate == 2 / 3
    assert result.stopped.startswith("move 3 unusable"), result.stopped

    # A sequence that just runs out short of the top says so rather than claiming a stop reason.
    result = plan_oneshot(scene, _Canned(canned[:2], route), with_image=False)
    assert not result.reached_top and "short of the top" in result.stopped

    # The image is rendered once and kept, so the caller can save exactly what was sent.
    result = plan_oneshot(scene, _Canned(canned, route), with_image=True)
    assert result.request_png and result.request_png.startswith(b"\x89PNG")
    assert plan_oneshot(scene, _Canned(canned, route), with_image=False).request_png is None


def check_skip_filters():
    """--skip-filters runs the sequence with the referee off. Greedy rolled out against a reach
    model bigger than the real one stands in for a model that writes moves it cannot make."""
    scene = wall(0)
    route = list(scene.route.hold_ids)
    optimist = GreedyChooser(scene, ReachModel(hand_step=2.1, foot_step=1.5, max_span=3.6))
    written = optimist.choose("", {"goal": {"top_hold_id": scene.route.top_hold_id},
                                   "body": initial_pose(scene)},
                              {"properties": {"moves": {"maxItems": 60}}}, None)["moves"]

    strict = plan_oneshot(scene, _Canned(written, route), with_image=False)
    assert strict.stopped.startswith("move ") and not strict.forced, strict.stopped

    loose = plan_oneshot(scene, _Canned(written, route), with_image=False, skip_filters=True)
    assert len(loose.moves) == len(written), "every move the model wrote must run"
    assert loose.forced, "the moves the rules would have refused must say so"
    # The flags are honest: replaying what it executed with the referee back on stops where the
    # strict run stopped.
    again = plan_oneshot(scene, _Canned([{"moving_limb": m.moving_limb,
                                          "target_hold_id": str(m.target_hold_id)}
                                         for m in loose.moves], route), with_image=False)
    assert len(again.moves) == len(strict.moves)
    print(f"  filters on executed {len(strict.moves)}, off executed {len(loose.moves)} "
          f"({loose.forced} of them forced)")

    # Off means off: reach, posture, occupancy and the route itself. Four limbs stacked on one hold
    # is refused by every rule there is, and still runs.
    stacked = {limb: route[0] for limb in LIMBS}
    move = {"moving_limb": "left_hand", "target_hold_id": str(route[0])}
    assert validate(move, scene, stacked) and validate(move, scene, stacked, skip_filters=True) is None
    off_route = next((h.id for h in scene.holds if h.id not in route), None)
    if off_route is not None:
        wander = {"moving_limb": "left_hand", "target_hold_id": str(off_route)}
        assert validate(wander, scene, stacked) and \
            validate(wander, scene, stacked, skip_filters=True) is None
    # What it does not waive: a limb the replay cannot move, or a hold that is not on the wall.
    assert validate({"moving_limb": "tail", "target_hold_id": str(route[0])},
                    scene, stacked, skip_filters=True)
    assert validate({"moving_limb": "left_hand", "target_hold_id": "9999"},
                    scene, stacked, skip_filters=True)
    # The limits handed to the model are the real ones either way -- this is the referee's switch.
    assert build_payload(scene, initial_pose(scene), ReachModel())["limits"]["hand_step"] \
        == ReachModel().hand_step


def check_render():
    scene = wall(0)
    pose = initial_pose(scene)
    png = render(scene, pose, candidates(scene, pose))
    assert png.startswith(b"\x89PNG") and len(png) > 2000


if __name__ == "__main__":
    for check in (check_scene, check_candidates, check_validator, check_step_prompt,
                  check_aliases, check_solvable, check_steps, check_oneshot, check_replay,
                  check_skip_filters, check_render):
        check()
        print(f"ok  {check.__name__}")

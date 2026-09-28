"""VLM High-Level Planner (docs/04). Two ways to get a plan out of the model:

`plan_steps` (the default) asks for one move at a time -- candidates -> one move -> validate ->
re-plan from the new pose -- with retries and a greedy fallback. The plan survives contact with the
physics because every move is chosen against the pose it is issued from.

`plan_oneshot` asks once for the whole sequence and replays it. No candidate list in the prompt, no
re-planning: the replay stops at the first move that is not physically available, because that is
where the RL controller would stop too.

Both share the referee: `validate()` against `candidates.rejection` / `blocked_holds`, the `Move` /
`Plan` types, and the docs/07 target pose the RL controller consumes.
"""
from __future__ import annotations

from collections import Counter
from dataclasses import dataclass, field

from .candidates import Pose, ReachModel, blocked_holds, candidates, initial_pose, rejection
from .providers import GreedyChooser, MoveChooser
from .render import render
from .scene import HANDS, LIMBS, Scene

SYSTEM_STEPS = """You are an experienced climber working out the beta for a route, one limb move at
a time. You are not searching for a path -- the holds are given. You are choosing the move a strong
climber would actually make. The climber is 1.7 m tall.

INPUT
An image of the wall: hold ids labelled, your four limbs ringed in blue and joined by blue lines,
the holds you can reach right now ringed in yellow. The same state as JSON, plus `history`, your
own last few moves.

RULES (hard)
- Move exactly one limb. The other three stay where they are.
- Only a hold listed in `candidates` for that limb. Nothing else exists for you -- anything out of
  reach, below the hips, or that would tear the body apart is already gone from that list.
- TWO LIMBS MAY OCCUPY THE SAME HOLD, if they are the same kind: both hands on one hold, both feet
  on one hold. Hand/foot matching is not allowed, and `candidates` will not offer it.
- Finish with BOTH hands on `goal.top_hold_id`. The route is cleared only once the second hand
  matches on it, so as the top comes into reach, bring the feet up high enough that the second hand
  can follow.

HOW A CLIMBER CHOOSES (in this order)

1. Keep a triangle. The three limbs that stay put are your support. They are stable when they
   spread into a wide triangle -- two feet apart with a hand above, or two hands apart with a foot
   below -- and unstable when they line up or bunch into one spot. Before you commit, picture the
   triangle you are hanging from.

2. Do not ball up. Four limbs crowded onto neighbouring holds folds the body, pushes the hips off
   the wall and swings your weight backwards off the holds. Stay open: hands roughly a torso above
   the feet, arms straight, hips in close, weight on the feet.

3. Feet first, and alternate.
   Read `history`: do not move the same limb twice in a row, and never put a limb back on the hold
   it just came off

4. Reach from a stance, not from a stretch. A move made with the hips low and the arms locked out
   long is a move you cannot control. If the reach is far, bring a foot up first.

5. Think one move ahead. Prefer the move that leaves the next one available; a hold that strands
   you with nothing in reach is worse than a smaller gain.

`reason`: one short sentence, written before you commit, naming what makes the move stable."""

SYSTEM_ONESHOT = """You are an experienced climber working out the beta for a route. You are not
searching for a path -- the holds are given. Write out the whole sequence of limb moves, in order,
that a strong climber would actually make, from the starting body position to the top.
Moves should be possible for 1.7 m tall climber.

INPUT
An image of the wall: hold ids labelled, your four limbs at the start ringed in blue and joined by
blue lines. The same state as JSON: every hold on the route, the body's starting position, and
`limits`: how far one limb may travel in a single move, and how far the body may spread.

RULES (hard)
- One move = exactly one limb to exactly one hold. The other three stay where they are.
- Only holds listed in `holds`. Nothing else exists for you.
- A limb may travel at most `limits.hand_step` (hands) or `limits.foot_step` (feet) metres from the
  hold it is on at that point in YOUR OWN sequence. Nothing resets between moves, so track where all
  four limbs are as you go -- a move is measured from where that limb actually is by then, not from
  where it started.
- THE BODY ONLY STRETCHES SO FAR. After every move, measure each hand against each foot: the
  furthest of those four distances is how far the body is spread, and it may not exceed
  `limits.max_span` metres -- 2.4 m, a 1.7 m climber at full stretch. This is a separate limit from
  the step above: a hand can move 1.0 m and still tear the body past 2.4 m because the feet stayed
  where they were. If a hold is too far from your feet, bring a foot up first and take it after.
- TWO LIMBS MAY OCCUPY THE SAME HOLD. This is matching: both hands on one
  hold, both feet on one hold. However, foot/hand matching is not allowed.
- Finish with BOTH hands on `goal.top_hold_id`. The route is cleared only once the second hand
  matches on it, so plan the last two moves together: bring the feet up high enough that the second
  hand can follow.

HOW A CLIMBER CHOOSES

1. Keep a triangle. Limbs are stable when they spread into a wide triangle --
   two feet apart with a hand above, or two hands apart with a foot
   below -- and unstable when they line up or bunch into one spot.

2. Do not ball up. Four limbs crowded onto neighbouring holds folds the body, pull your weight backwards off the holds.
   Keep distance between hand and feet: at least 1 metre, ideally 1.5 metres.

3. Move up or sideways. Every move should gain height or set up the next one.

4. Do not move the same limb twice in a row, and never put a limb back on the hold it just came off

5. Reach from a stance, not from a stretch. A move made with the hips low and the arms locked out
   long is a move you cannot control.

6. Think one move ahead. Prefer the move that leaves the next one available; a hold that strands
   you with nothing in reach is worse than a smaller gain.

`reason`: one short sentence, written before you commit, naming what makes the move stable."""



@dataclass
class Move:
    moving_limb: str
    target_hold_id: int
    from_hold_id: int = -1
    reason: str = ""
    pose: Pose = field(default_factory=dict)  # pose after the move
    retries: int = 0  # step-by-step: model answers rejected before this one landed
    fell_back: bool = False  # step-by-step: the greedy fallback chose it, not the model
    forced: bool = False  # would not have been available; ran because plan_oneshot(skip_filters=True)

    def targets(self) -> dict:
        """The docs/07 target pose the RL controller consumes: all four limbs, one of them moving."""
        return {"targets": [
            {"limb": {"left_hand": "LeftHand", "right_hand": "RightHand",
                      "left_foot": "LeftFoot", "right_foot": "RightFoot"}[limb],
             "hold_id": self.pose[limb],
             "move": limb == self.moving_limb}
            for limb in LIMBS
        ]}


@dataclass
class Plan:
    moves: list[Move]  # one-shot: the executable prefix, every move up to the first one that was not
    reached_top: bool
    model: str
    mode: str = "steps"  # "steps" or "oneshot" -- which planner produced this
    stopped: str = ""
    requests: int = 0  # step-by-step: model+fallback calls made
    invalid: int = 0  # step-by-step: answers the validator rejected
    proposed: int = 0  # one-shot: moves the model wrote
    examined: int = 0  # one-shot: moves replayed, including the one that failed
    request_png: bytes | None = None  # one-shot: the exact image the model was sent

    @property
    def valid_move_rate(self) -> float:
        """Share of the model's answers that were usable.

        One-shot: of the replayed sequence, so 1.0 means the whole thing ran and `proposed` >
        `examined` means it kept writing moves after the route was cleared. Step-by-step: of the
        requests made, retries and fallback included.
        """
        if self.mode == "oneshot":
            return len(self.moves) / self.examined if self.examined else 0.0
        return 1.0 - self.invalid / self.requests if self.requests else 0.0

    @property
    def forced(self) -> int:
        """Executed moves the rules would have refused. 0 unless plan_oneshot(skip_filters=True)."""
        return sum(move.forced for move in self.moves)

    @property
    def repeated_limb(self) -> int:
        """Moves that move the limb the previous move already moved. Legal, but weak beta."""
        return sum(a.moving_limb == b.moving_limb for a, b in zip(self.moves, self.moves[1:]))

    @property
    def backtracks(self) -> int:
        """Moves putting a limb back on the hold it last came off -- the previous move undone."""
        left: dict[str, int] = {}
        count = 0
        for move in self.moves:
            count += left.get(move.moving_limb) == move.target_hold_id
            left[move.moving_limb] = move.from_hold_id
        return count

    def to_dict(self) -> dict:
        return {
            "mode": self.mode,
            "model": self.model,
            "reached_top": self.reached_top,
            "stopped": self.stopped,
            "requests": self.requests,
            "invalid": self.invalid,
            "proposed": self.proposed,
            "examined": self.examined,
            "executed": len(self.moves),
            "valid_move_rate": round(self.valid_move_rate, 3),
            "forced": self.forced,
            "repeated_limb": self.repeated_limb,
            "backtracks": self.backtracks,
            "moves": [
                {"moving_limb": m.moving_limb, "from_hold_id": m.from_hold_id,
                 "target_hold_id": m.target_hold_id, "reason": m.reason, "retries": m.retries,
                 "fell_back": m.fell_back, "forced": m.forced, "pose": m.pose, **m.targets()}
                for m in self.moves
            ],
        }


def move_schema(cands: dict[str, list[int]]) -> dict:
    """One move, for the step-by-step planner: enums for both fields, per docs/04. Hold ids are
    strings because the JSON-schema subset Gemini accepts only enumerates strings; the pairing is
    what validate() still has to check."""
    ids = sorted({hold_id for hold_ids in cands.values() for hold_id in hold_ids})
    return {
        "type": "object",
        "properties": {
            "reason": {"type": "string"},
            "moving_limb": {"type": "string", "enum": sorted(cands)},
            "target_hold_id": {"type": "string", "enum": [str(i) for i in ids]},
        },
        "required": ["reason", "moving_limb", "target_hold_id"],
        "propertyOrdering": ["reason", "moving_limb", "target_hold_id"],
    }


def plan_schema(scene: Scene, max_moves: int) -> dict:
    """The whole route as a list of moves. Hold ids are strings because the JSON-schema subset Gemini
    accepts only enumerates strings, and the enum is every hold on the route -- there is no candidate
    list to narrow it to, so validate() is the only thing checking limb and hold against each other.

    `reason` comes first so the model writes a sentence before committing to each move.
    """
    move = {
        "type": "object",
        "properties": {
            "reason": {"type": "string"},
            "moving_limb": {"type": "string", "enum": sorted(LIMBS)},
            "target_hold_id": {"type": "string",
                               "enum": [str(i) for i in sorted(scene.route.hold_ids)]},
        },
        "required": ["reason", "moving_limb", "target_hold_id"],
        "propertyOrdering": ["reason", "moving_limb", "target_hold_id"],
    }
    return {
        "type": "object",
        "properties": {"moves": {"type": "array", "items": move, "maxItems": max_moves}},
        "required": ["moves"],
        "propertyOrdering": ["moves"],
    }


def step_payload(
    scene: Scene,
    pose: Pose,
    cands: dict[str, list[int]],
    error: str | None = None,
    history: list["Move"] | None = None,
) -> dict:
    """One move's world: where the body is, what is in reach right now, and what it just did."""
    payload = {
        "goal": {"top_hold_id": scene.route.top_hold_id},
        "body": dict(pose),
        # Each request is the model's whole world, so without this it cannot tell that it is
        # shuffling one foot back and forth -- it never sees its own last answer.
        "history": [{"moving_limb": m.moving_limb, "from_hold_id": m.from_hold_id,
                     "to_hold_id": m.target_hold_id} for m in (history or [])[-3:]],
        "holds": [{"id": scene.hold(hid).id, "position": list(scene.hold(hid).position)}
                  for hid in scene.route.hold_ids],
        "candidates": {limb: list(ids) for limb, ids in cands.items()},
    }
    if error:
        payload["previous_answer_rejected"] = error
    return payload


def build_payload(scene: Scene, pose: Pose, model: ReachModel) -> dict:
    """The one-shot model's whole world: the route, where the body starts, and the reach budget. No
    candidate list -- working out what is in reach is the model's job there -- and no history,
    because it is writing the history itself."""
    return {
        "goal": {"top_hold_id": scene.route.top_hold_id},
        "body": dict(pose),
        "limits": {"hand_step": model.hand_step, "foot_step": model.foot_step,
                   "max_span": model.max_span},
        "holds": [{"id": scene.hold(hid).id, "position": list(scene.hold(hid).position)}
                  for hid in scene.route.hold_ids],
    }


def validate(move: dict, scene: Scene, pose: Pose, model: ReachModel = ReachModel(),
             skip_filters: bool = False) -> str | None:
    """docs/04's checks against the pose the move is issued from. Returns why it is not available,
    or None. Both planners referee with this.

    Takes the reach model rather than a candidate list: the list is built for a prompt, capped at
    `max_per_limb` and sorted by progress, so checking against it called the seventh-nearest hold
    unreachable and reported every geometric refusal as a distance one.

    `skip_filters` waives every climbing rule -- route membership, occupancy, reach, posture -- and
    keeps only what the replay cannot run without: a limb it can move and a hold that exists on
    this wall.
    """
    limb = move.get("moving_limb")
    if limb not in LIMBS:
        return f"moving_limb must be one of {list(LIMBS)}"
    try:
        hold_id = int(move["target_hold_id"])
    except (KeyError, TypeError, ValueError):
        return "target_hold_id must be an integer hold id"
    if not any(hold.id == hold_id for hold in scene.holds):
        return f"hold {hold_id} is not on this wall"
    if skip_filters:
        return None
    if hold_id not in scene.route.hold_ids:
        return f"hold {hold_id} is not on the route"
    if pose[limb] == hold_id:
        return f"{limb} already holds {hold_id}"
    if hold_id in blocked_holds(pose, limb):
        # Naming the occupant matters: the rule is not one-limb-per-hold, so "already held" alone
        # reads as if it contradicted the prompt's matching rule.
        others = [l for l in LIMBS if l != limb and pose[l] == hold_id]
        return (f"hold {hold_id} is held by {' and '.join(others)}; moving {limb} there would leave "
                f"all four limbs on two holds, which is not a position you can hang in")
    why = rejection(scene, pose, limb, hold_id, model)
    if why:
        return f"{limb} cannot take hold {hold_id} -- {why}"
    return None


def _topped_out(pose: Pose, scene: Scene) -> bool:
    """Both hands on the top hold, which is what ClimberRagdoll.IsToppedOut clears on (docs/07)."""
    return all(pose[hand] == scene.route.top_hold_id for hand in HANDS)


def plan_steps(
    scene: Scene,
    chooser: MoveChooser,
    pose: Pose | None = None,
    model: ReachModel = ReachModel(),
    max_moves: int = 60,
    max_retries: int = 2,
    with_image: bool = True,
    on_step=None,
) -> Plan:
    """One request per move: candidates -> one move -> validate -> re-plan from the new pose.

    Runs until both hands reach the top hold, nothing is reachable, the plan starts going in
    circles, or max_moves. An invalid answer is re-requested with the reason attached; after
    `max_retries` the greedy chooser has the last word (docs/07, "VLM invalid output").
    `on_step(index, move)` is called for each move.
    """
    pose = dict(pose or initial_pose(scene))
    result = Plan(moves=[], reached_top=False, model=getattr(chooser, "name", "unknown"),
                  mode="steps")
    fallback = GreedyChooser(scene, model)
    seen: Counter[tuple] = Counter()

    while len(result.moves) < max_moves:
        key = tuple(sorted(pose.items()))
        seen[key] += 1
        # Revisiting a pose twice is fine (a climber may back off a move); a third time is a loop,
        # and the planner is the only thing that can see it -- the model gets one state at a time.
        if seen[key] > 2:
            result.stopped = "the plan started repeating poses"
            return result

        if _topped_out(pose, scene):
            result.reached_top = True
            return result

        cands = candidates(scene, pose, model)
        if not cands:
            result.stopped = "no reachable candidate for any limb"
            return result

        image = render(scene, pose, cands) if with_image else None
        schema = move_schema(cands)
        error, chosen, retries, fell_back = None, None, 0, False
        # max_retries model attempts after the first, then the greedy fallback as the last word.
        for attempt in range(max_retries + 2):
            active = chooser if attempt <= max_retries else fallback
            fell_back = active is fallback
            result.requests += 1
            payload = step_payload(scene, pose, cands, error, result.moves)
            answer = active.choose(SYSTEM_STEPS, payload, schema, image)
            error = validate(answer, scene, pose, model)
            if error is None:
                chosen = answer
                break
            result.invalid += 1
            retries = attempt + 1

        if chosen is None:  # even the fallback failed: a bug here, not a bad model
            result.stopped = f"no valid move after {max_retries + 2} attempts: {error}"
            return result

        limb = chosen["moving_limb"]
        came_from = pose[limb]
        pose[limb] = int(chosen["target_hold_id"])
        move = Move(limb, pose[limb], came_from, chosen.get("reason", ""), dict(pose),
                    retries=retries, fell_back=fell_back)
        result.moves.append(move)
        if on_step:
            on_step(len(result.moves), move)
        if _topped_out(pose, scene):
            result.reached_top = True
            return result

    result.stopped = f"hit max_moves={max_moves}"
    return result


def plan_oneshot(
    scene: Scene,
    chooser: MoveChooser,
    pose: Pose | None = None,
    model: ReachModel = ReachModel(),
    max_moves: int = 60,
    with_image: bool = True,
    skip_filters: bool = False,
    on_step=None,
) -> Plan:
    """One request for the whole sequence, then replay it move by move.

    The reach filter is still the referee -- it just no longer tells the model the answer. There is
    no retry and no greedy fallback: the first move it rejects ends the plan, which is where the RL
    controller would stop too. `on_step(index, move)` is called for each move that executed.

    `skip_filters` runs the whole sequence with the referee switched off -- no route, occupancy,
    reach or posture check -- so a sequence that dies on move 3 can still be read to the end. The
    moves that only ran because of it are flagged (`Move.forced`, counted by `Plan.forced`) and the
    model is told the real `limits` either way. A plan with the filters off proves nothing about
    what the climber can do: leave it off for anything being measured.
    """
    pose = dict(pose or initial_pose(scene))
    result = Plan(moves=[], reached_top=False, model=getattr(chooser, "name", "unknown"),
                  mode="oneshot")
    result.request_png = render(scene, pose, {}) if with_image else None
    answer = chooser.choose(SYSTEM_ONESHOT, build_payload(scene, pose, model),
                            plan_schema(scene, max_moves), result.request_png)
    proposed = answer.get("moves", [])[:max_moves]
    result.proposed = len(proposed)

    for index, step in enumerate(proposed, start=1):
        error = validate(step, scene, pose, model, skip_filters)
        result.examined += 1
        if error:
            result.stopped = f"move {index} unusable: {error}"
            break
        forced = skip_filters and validate(step, scene, pose, model) is not None
        limb = step["moving_limb"]
        came_from = pose[limb]
        pose[limb] = int(step["target_hold_id"])
        move = Move(limb, pose[limb], came_from, step.get("reason", ""), dict(pose), forced=forced)
        result.moves.append(move)
        if on_step:
            on_step(index, move)
        # Both hands, not one: this mirrors ClimberRagdoll.IsToppedOut, which is what actually clears
        # the route in Unity (docs/07). Anything the model wrote after this is ignored.
        if _topped_out(pose, scene):
            result.reached_top = True
            break

    if not result.reached_top and not result.stopped:
        result.stopped = f"the sequence ended after {len(result.moves)} moves, short of the top"
    return result

"""VLM High-Level Planner (docs/04): candidates -> one move -> validate -> re-plan.

Short horizon by design. One move per request, pose updated, candidates regenerated -- docs/04
prefers this over emitting a whole route in one shot, because the plan has to survive contact with
the physics. The multi-step sequence is what the loop accumulates, so there is no separate
multi-move schema to keep in sync.
"""
from __future__ import annotations

from collections import Counter
from dataclasses import dataclass, field

from .candidates import Pose, ReachModel, candidates, initial_pose
from .providers import GreedyChooser, MoveChooser
from .render import render
from .scene import HANDS, LIMBS, Scene

SYSTEM = """You are an experienced climber working out the beta for a route, one limb move at a
time. You are not searching for a path -- the holds are given. You are choosing the move a strong
climber would actually make.

INPUT
An image of the wall: hold ids labelled, your four limbs ringed in blue and joined by blue lines,
the holds you can reach right now ringed in yellow. The same state as JSON, plus `history`, your
own last few moves.

RULES (hard)
- Move exactly one limb. The other three stay where they are.
- Only a hold listed in `candidates` for that limb. Nothing else exists for you.
- Get a hand to `goal.top_hold_id`.

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


@dataclass
class Move:
    moving_limb: str
    target_hold_id: int
    from_hold_id: int = -1
    reason: str = ""
    pose: Pose = field(default_factory=dict)  # pose after the move
    retries: int = 0
    fell_back: bool = False

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
    moves: list[Move]
    reached_top: bool
    model: str
    stopped: str = ""
    requests: int = 0
    invalid: int = 0

    @property
    def valid_move_rate(self) -> float:
        return 1.0 - self.invalid / self.requests if self.requests else 0.0

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
            "model": self.model,
            "reached_top": self.reached_top,
            "stopped": self.stopped,
            "requests": self.requests,
            "invalid": self.invalid,
            "valid_move_rate": round(self.valid_move_rate, 3),
            "repeated_limb": self.repeated_limb,
            "backtracks": self.backtracks,
            "moves": [
                {"moving_limb": m.moving_limb, "from_hold_id": m.from_hold_id,
                 "target_hold_id": m.target_hold_id, "reason": m.reason, "retries": m.retries,
                 "fell_back": m.fell_back, "pose": m.pose, **m.targets()}
                for m in self.moves
            ],
        }


def move_schema(cands: dict[str, list[int]]) -> dict:
    """Enums for both fields, per docs/04. Hold ids are strings because the JSON-schema subset
    Gemini accepts only enumerates strings; the pairing is what validate() still has to check."""
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


def build_payload(
    scene: Scene,
    pose: Pose,
    cands: dict[str, list[int]],
    error: str | None = None,
    history: list["Move"] | None = None,
) -> dict:
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


def validate(move: dict, scene: Scene, pose: Pose, cands: dict[str, list[int]]) -> str | None:
    """docs/04's checks. Returns the reason it is invalid, or None."""
    limb = move.get("moving_limb")
    if limb not in LIMBS:
        return f"moving_limb must be one of {list(LIMBS)}"
    try:
        hold_id = int(move["target_hold_id"])
    except (KeyError, TypeError, ValueError):
        return "target_hold_id must be an integer hold id"
    if hold_id not in scene.route.hold_ids:
        return f"hold {hold_id} is not on the route"
    if pose[limb] == hold_id:
        return f"{limb} already holds {hold_id}"
    if hold_id in pose.values():
        return f"hold {hold_id} is already held by another limb"
    if hold_id not in cands.get(limb, []):
        return f"hold {hold_id} is not in candidates.{limb} ({cands.get(limb, [])})"
    return None


def plan(
    scene: Scene,
    chooser: MoveChooser,
    pose: Pose | None = None,
    model: ReachModel = ReachModel(),
    max_moves: int = 60,
    max_retries: int = 2,
    with_image: bool = True,
    on_step=None,
) -> Plan:
    """Runs the loop until a hand reaches the top hold, nothing is reachable, or max_moves."""
    pose = dict(pose or initial_pose(scene))
    result = Plan(moves=[], reached_top=False, model=getattr(chooser, "name", "unknown"))
    fallback = GreedyChooser(scene)
    top = scene.route.top_hold_id
    seen: Counter[tuple] = Counter()

    while len(result.moves) < max_moves:
        key = tuple(sorted(pose.items()))
        seen[key] += 1
        # Revisiting a pose twice is fine (a climber may back off a move); a third time is a loop,
        # and the planner is the only thing that can see it -- the model gets one state at a time.
        if seen[key] > 2:
            result.stopped = "the plan started repeating poses"
            return result

        if any(pose[limb] == top for limb in HANDS):
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
            payload = build_payload(scene, pose, cands, error, result.moves)
            answer = active.choose(SYSTEM, payload, schema, image)
            error = validate(answer, scene, pose, cands)
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
        move = Move(limb, pose[limb], came_from, chosen.get("reason", ""), dict(pose), retries, fell_back)
        result.moves.append(move)
        if on_step:
            on_step(len(result.moves), move, image)

    result.stopped = f"hit max_moves={max_moves}"
    return result

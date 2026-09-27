"""VLM High-Level Planner (docs/04): one request, the whole sequence of moves, then replay.

The model gets the route, the starting pose and its reach budget, and writes out every limb move in
order. There is no candidate list in the prompt and no re-planning: the planner replays the sequence
against the reach model and stops at the first move that is not physically available, because that
is where the RL controller would stop too.
"""
from __future__ import annotations

from dataclasses import dataclass, field

from .candidates import Pose, ReachModel, blocked_holds, candidates, initial_pose
from .providers import MoveChooser
from .render import render
from .scene import HANDS, LIMBS, Scene

SYSTEM = """You are an experienced climber working out the beta for a route. You are not searching
for a path -- the holds are given. Write out the whole sequence of limb moves, in order, that a
strong climber would actually make, from the starting body position to the top.

INPUT
An image of the wall: hold ids labelled, your four limbs at the start ringed in blue and joined by
blue lines. The same state as JSON: every hold on the route, the body's starting position, and
`limits`, how far one limb may travel in a single move.

RULES (hard)
- One move = exactly one limb to exactly one hold. The other three stay where they are.
- Only holds listed in `holds`. Nothing else exists for you.
- A limb may travel at most `limits.hand_step` (hands) or `limits.foot_step` (feet) metres from the
  hold it is on at that point in YOUR OWN sequence. Nothing resets between moves, so track where all
  four limbs are as you go -- a move is measured from where that limb actually is by then, not from
  where it started.
- TWO LIMBS MAY OCCUPY THE SAME HOLD. This is matching, and any pair may do it: both hands on one
  hold, both feet on one hold, or a foot on a hold a hand is already on (a hand-foot match, where
  you step up onto your own handhold -- a normal technique). A hold being occupied is never by itself
  a reason to avoid it. The one arrangement forbidden is all four limbs on two holds.
- Finish with BOTH hands on `goal.top_hold_id`. The route is cleared only once the second hand
  matches on it, so plan the last two moves together: bring the feet up high enough that the second
  hand can follow.

HOW A CLIMBER CHOOSES (in this order)

1. Keep a triangle. Limbs are stable when they spread into a wide triangle -- 
   two feet apart with a hand above, or two hands apart with a foot
   below -- and unstable when they line up or bunch into one spot.

2. Do not ball up. Four limbs crowded onto neighbouring holds folds the body, pull your weight backwards off the holds. 
   Keep distance between hand and feet: at least 1 metre, ideally 1.5 metres.

3. Move up or sideways. Every move should gain height or set up the next one.

4. Feet first, and alternate. Do not move the same limb twice in a row, and never put a limb
   back on the hold it just came off

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
    moves: list[Move]  # the executable prefix: every move up to the first one that was not
    reached_top: bool
    model: str
    stopped: str = ""
    proposed: int = 0  # moves the model wrote
    examined: int = 0  # moves replayed, including the one that failed
    request_png: bytes | None = None  # the exact image the model was sent

    @property
    def valid_move_rate(self) -> float:
        """Share of the replayed sequence that was physically available. 1.0 means the whole thing
        ran; `proposed` > `examined` means it kept writing moves after the route was cleared."""
        return len(self.moves) / self.examined if self.examined else 0.0

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
            "proposed": self.proposed,
            "examined": self.examined,
            "executed": len(self.moves),
            "valid_move_rate": round(self.valid_move_rate, 3),
            "repeated_limb": self.repeated_limb,
            "backtracks": self.backtracks,
            "moves": [
                {"moving_limb": m.moving_limb, "from_hold_id": m.from_hold_id,
                 "target_hold_id": m.target_hold_id, "reason": m.reason, "pose": m.pose,
                 **m.targets()}
                for m in self.moves
            ],
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


def build_payload(scene: Scene, pose: Pose, model: ReachModel) -> dict:
    """The model's whole world: the route, where the body starts, and the reach budget. No candidate
    list -- working out what is in reach is the model's job now -- and no history, because it is
    writing the history itself."""
    return {
        "goal": {"top_hold_id": scene.route.top_hold_id},
        "body": dict(pose),
        "limits": {"hand_step": model.hand_step, "foot_step": model.foot_step},
        "holds": [{"id": scene.hold(hid).id, "position": list(scene.hold(hid).position)}
                  for hid in scene.route.hold_ids],
    }


def validate(move: dict, scene: Scene, pose: Pose, cands: dict[str, list[int]]) -> str | None:
    """docs/04's checks against the pose the move is issued from. Returns why it is not available,
    or None."""
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
    if hold_id in blocked_holds(pose, limb):
        # Naming the occupant matters: the rule is not one-limb-per-hold, so "already held" alone
        # reads as if it contradicted the prompt's matching rule.
        others = [l for l in LIMBS if l != limb and pose[l] == hold_id]
        return (f"hold {hold_id} is held by {' and '.join(others)}; moving {limb} there would leave "
                f"all four limbs on two holds, which is not a position you can hang in")
    if hold_id not in cands.get(limb, []):
        return f"hold {hold_id} is out of reach for {limb} from hold {pose[limb]}"
    return None


def plan(
    scene: Scene,
    chooser: MoveChooser,
    pose: Pose | None = None,
    model: ReachModel = ReachModel(),
    max_moves: int = 60,
    with_image: bool = True,
    on_step=None,
) -> Plan:
    """One request for the whole sequence, then replay it move by move.

    The reach filter is still the referee -- it just no longer tells the model the answer. There is
    no retry and no greedy fallback: the first move it rejects ends the plan, which is where the RL
    controller would stop too. `on_step(index, move)` is called for each move that executed.
    """
    pose = dict(pose or initial_pose(scene))
    result = Plan(moves=[], reached_top=False, model=getattr(chooser, "name", "unknown"))
    result.request_png = render(scene, pose, {}) if with_image else None
    answer = chooser.choose(SYSTEM, build_payload(scene, pose, model),
                            plan_schema(scene, max_moves), result.request_png)
    proposed = answer.get("moves", [])[:max_moves]
    result.proposed = len(proposed)

    for index, step in enumerate(proposed, start=1):
        error = validate(step, scene, pose, candidates(scene, pose, model))
        result.examined += 1
        if error:
            result.stopped = f"move {index} unusable: {error}"
            break
        limb = step["moving_limb"]
        came_from = pose[limb]
        pose[limb] = int(step["target_hold_id"])
        move = Move(limb, pose[limb], came_from, step.get("reason", ""), dict(pose))
        result.moves.append(move)
        if on_step:
            on_step(index, move)
        # Both hands, not one: this mirrors ClimberRagdoll.IsToppedOut, which is what actually clears
        # the route in Unity (docs/07). Anything the model wrote after this is ignored.
        if all(pose[hand] == scene.route.top_hold_id for hand in HANDS):
            result.reached_top = True
            break

    if not result.reached_top and not result.stopped:
        result.stopped = f"the sequence ended after {len(result.moves)} moves, short of the top"
    return result

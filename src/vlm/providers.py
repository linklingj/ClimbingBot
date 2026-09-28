"""The model swap point. One method: given the planner's payload, return a move as JSON.

Adding a provider means adding a class here and passing it to plan(). Nothing else in the package
knows which model ran -- planner.py owns the prompt, the schema and the validation.
"""
from __future__ import annotations

import base64
import json
import math
import os
from typing import Protocol

from dotenv import load_dotenv


# Short names for the current default on each side, so the CLI takes `--model gpt` / `--model
# gemini`. Anything else is passed through to the provider untouched.
GEMINI = "gemini-3.8-flash"
GPT = "gpt-6-sol"
ALIASES = {"gemini": GEMINI, "gpt": GPT}


class MoveChooser(Protocol):
    name: str

    def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict:
        """Return a dict matching `schema`. Raises on transport failure; the planner validates."""


class GeminiChooser:
    """Structured output through google-genai: response_schema forces the enums, so a malformed
    move can only come from the schema being satisfied and the choice still being wrong."""

    def __init__(self, model: str = GEMINI, api_key: str | None = None, temperature: float = 0.4):
        from google import genai  # imported late: the offline chooser must not need the SDK

        load_dotenv()
        key = api_key or os.environ.get("GEMINI_API_KEY") or os.environ.get("GOOGLE_API_KEY")
        if not key:
            raise RuntimeError("GEMINI_API_KEY is not set (see .env.example)")
        self.name = model
        self.model = model
        self.temperature = temperature
        self._client = genai.Client(api_key=key)

    def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict:
        from google.genai import types

        parts = [types.Part.from_text(text=json.dumps(payload, indent=2))]
        if image_png:
            parts.insert(0, types.Part.from_bytes(data=image_png, mime_type="image/png"))
        response = self._client.models.generate_content(
            model=self.model,
            contents=[types.Content(role="user", parts=parts)],
            config=types.GenerateContentConfig(
                system_instruction=system,
                temperature=self.temperature,
                response_mime_type="application/json",
                response_schema=schema,
            ),
        )
        return json.loads(response.text)


class OpenAIChooser:
    """Same move, through OpenAI structured outputs (`strict` json_schema, so the enums bind)."""

    def __init__(self, model: str = GPT, api_key: str | None = None, temperature: float | None = None):
        from openai import OpenAI  # imported late, same as the Gemini client

        load_dotenv()
        key = api_key or os.environ.get("OPENAI_API_KEY")
        if not key:
            raise RuntimeError("OPENAI_API_KEY is not set (see .env.example)")
        self.name = model
        self.model = model
        # Left unset by default: the reasoning models reject anything but the default temperature.
        self.temperature = temperature
        self._client = OpenAI(api_key=key)

    def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict:
        content: list[dict] = [{"type": "text", "text": json.dumps(payload, indent=2)}]
        if image_png:
            url = "data:image/png;base64," + base64.b64encode(image_png).decode()
            content.insert(0, {"type": "image_url", "image_url": {"url": url}})
        extra = {} if self.temperature is None else {"temperature": self.temperature}
        response = self._client.chat.completions.create(
            model=self.model,
            messages=[{"role": "system", "content": system}, {"role": "user", "content": content}],
            response_format={"type": "json_schema", "json_schema": {
                "name": "move", "strict": True, "schema": strict_schema(schema)}},
            **extra,
        )
        return json.loads(response.choices[0].message.content)


# propertyOrdering is Gemini's; strict mode rejects the array length keywords outright.
_DROP = ("propertyOrdering", "maxItems", "minItems")


def strict_schema(schema: dict) -> dict:
    """The planner's schema in OpenAI's strict dialect: no vendor keywords, closed objects.

    Recursive, because the sequence schema nests the move object inside an array.
    """
    out = {k: v for k, v in schema.items() if k not in _DROP}
    if "items" in out:
        out["items"] = strict_schema(out["items"])
    if "properties" in out:
        out["properties"] = {k: strict_schema(v) for k, v in out["properties"].items()}
        out["additionalProperties"] = False
        out["required"] = list(out["properties"])  # strict mode requires every property
    return out


class GreedyChooser:
    """No model: takes the candidate that gains most towards the top hold, by the same reach rules
    the planner referees with. The offline baseline the VLM is measured against.

    It answers whichever schema it is handed: the whole sequence for `plan_oneshot`, which it rolls
    out itself, or a single move for `plan_steps`, where it is also the fallback once the model has
    used up its retries (docs/07, "VLM invalid output").

    Rolling out its own sequence means re-deriving candidates at every step, which the one-shot VLM
    does not get to do -- so that sequence is a ceiling on what the reach model allows, not a
    like-for-like opponent.
    """

    name = "greedy"

    def __init__(self, scene, model=None):
        from .candidates import ReachModel

        self.scene = scene
        self.model = model or ReachModel()
        self._seen: set[tuple] = set()

    def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict:
        from .scene import HANDS

        top_hold_id = payload["goal"]["top_hold_id"]
        pose = dict(payload["body"])
        if "moves" not in schema["properties"]:  # one move, from this pose
            move = self._best(pose, top_hold_id)
            if move is None:  # the planner only asks when something is reachable
                raise RuntimeError(f"greedy has no move from {pose}")
            return move

        limit = schema["properties"]["moves"]["maxItems"]
        moves: list[dict] = []
        self._seen.add(_key(pose))
        while len(moves) < limit and not all(pose[hand] == top_hold_id for hand in HANDS):
            move = self._best(pose, top_hold_id)
            if move is None:  # nothing reachable for any limb
                break
            pose[move["moving_limb"]] = int(move["target_hold_id"])
            moves.append(move)
        return {"moves": moves}

    def _best(self, pose: dict, top_hold_id: int) -> dict | None:
        from .candidates import candidates
        from .scene import HANDS

        top = self.scene.position(top_hold_id)
        best = None
        for limb, ids in candidates(self.scene, pose, self.model).items():
            here = self.scene.position(pose[limb])
            for hold_id in ids:
                after = {**pose, limb: hold_id}
                gain = math.dist(here, top) - math.dist(self.scene.position(hold_id), top)
                # Unseen poses first: without this it undoes its own move forever whenever every
                # option loses ground.
                fresh = _key(after) not in self._seen
                # One move of lookahead: do not climb into a pose with nothing left. Since no limb
                # may go below the body, the dead ends are one move deep, and this is the difference
                # between 9/20 walls and 19/20.
                alive = all(after[hand] == top_hold_id for hand in HANDS) or \
                    bool(candidates(self.scene, after, self.model))
                if best is None or (alive, fresh, gain) > best[0]:
                    best = ((alive, fresh, gain), limb, hold_id)
        if best is None:
            return None
        _, limb, hold_id = best
        self._seen.add(_key({**pose, limb: hold_id}))
        return {"moving_limb": limb, "target_hold_id": str(hold_id)}


def _key(pose: dict) -> tuple:
    return tuple(sorted(pose.items()))


def chooser_for(model: str, scene=None) -> MoveChooser:
    """The CLI's model name -> a chooser. `greedy` (or `--offline`) needs the scene; everything else
    picks the provider off the name, after the short aliases are expanded."""
    if model == "greedy":
        return GreedyChooser(scene)
    model = ALIASES.get(model, model)
    return GeminiChooser(model) if model.startswith("gemini") else OpenAIChooser(model)

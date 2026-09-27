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


class MoveChooser(Protocol):
    name: str

    def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict:
        """Return a dict matching `schema`. Raises on transport failure; the planner validates."""


class GeminiChooser:
    """Structured output through google-genai: response_schema forces the enums, so a malformed
    move can only come from the schema being satisfied and the choice still being wrong."""

    def __init__(self, model: str = "gemini-2.5-flash", api_key: str | None = None, temperature: float = 0.4):
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

    def __init__(self, model: str = "gpt-6-luna", api_key: str | None = None, temperature: float | None = None):
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


def strict_schema(schema: dict) -> dict:
    """The planner's schema in OpenAI's strict dialect: no vendor keywords, closed objects."""
    out = {k: v for k, v in schema.items() if k != "propertyOrdering"}
    out["additionalProperties"] = False
    out["required"] = list(schema["properties"])  # strict mode requires every property
    return out


class GreedyChooser:
    """No model: takes the candidate closest to the top hold, preferring whichever limb is lowest.

    Two jobs -- it runs the loop offline in the self-test, and it is the fallback when the model
    returns an invalid move twice (docs/07, "VLM invalid output").
    """

    name = "greedy"

    def __init__(self, scene):
        self.scene = scene
        self._seen: set[tuple] = set()

    def choose(self, system: str, payload: dict, schema: dict, image_png: bytes | None) -> dict:
        top = self.scene.position(payload["goal"]["top_hold_id"])
        body = payload["body"]
        best = None
        for limb, ids in payload["candidates"].items():
            here = self.scene.position(body[limb])
            for hold_id in ids:
                target = self.scene.position(hold_id)
                gain = math.dist(here, top) - math.dist(target, top)
                # Unseen poses first: without this it undoes its own move forever whenever every
                # option loses ground.
                fresh = tuple(sorted({**body, limb: hold_id}.items())) not in self._seen
                score = (fresh, gain)
                if best is None or score > best[0]:
                    best = (score, limb, hold_id)
        _, limb, hold_id = best
        self._seen.add(tuple(sorted({**body, limb: hold_id}.items())))
        return {"reason": "greedy: largest gain towards the top hold",
                "moving_limb": limb, "target_hold_id": str(hold_id)}

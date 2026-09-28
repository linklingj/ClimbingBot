"""Plan one of the exported walls (/walls) and print the moves.

    PYTHONPATH=src python -m vlm --wall 3                      # Gemini (gemini-3.8-flash)
    PYTHONPATH=src python -m vlm --wall 3 --model gpt          # OpenAI (gpt-6-sol)
    PYTHONPATH=src python -m vlm --wall 3 --model gpt-6-luna   # any exact model name
    PYTHONPATH=src python -m vlm --wall 3 --offline            # no API key, greedy baseline
    PYTHONPATH=src python -m vlm --wall 3 --out out/wall3      # + scene.json, plan.json, PNGs
    PYTHONPATH=src python -m vlm --wall 3 --skip-filters       # referee off, read the whole sequence
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from .candidates import candidates, initial_pose
from .planner import plan
from .providers import GEMINI, chooser_for
from .render import render
from .scene import Scene, wall


def main() -> int:
    ap = argparse.ArgumentParser(prog="vlm")
    ap.add_argument("--wall", type=int, default=0, help="index into /walls")
    ap.add_argument("--scene", type=Path, help="scene JSON to plan on instead of an exported wall")
    ap.add_argument("--model", default="gemini",
                    help=f"`gemini` ({GEMINI}), `gpt` (gpt-6-sol), or an exact model name")
    ap.add_argument("--offline", action="store_true", help="greedy chooser, no model call")
    ap.add_argument("--no-image", action="store_true", help="JSON only, to measure the image's worth")
    ap.add_argument("--max-moves", type=int, default=60)
    ap.add_argument("--skip-filters", action="store_true",
                    help="replay with every rule off -- route, occupancy, reach, posture -- so the "
                         "whole sequence runs instead of stopping at the first bad move. The moves "
                         "that would have been refused are marked (!). Never when measuring.")
    ap.add_argument("--out", type=Path, help="scene.json, plan.json, request.png (the exact image "
                                            "sent to the model) and one PNG per pose: step00.png "
                                            "is the start, stepNN.png the pose after move NN")
    args = ap.parse_args()

    scene = Scene.load(args.scene) if args.scene else wall(args.wall)
    chooser = chooser_for("greedy" if args.offline else args.model, scene)
    out = args.out
    if out:
        out.mkdir(parents=True, exist_ok=True)
        scene.save(out / "scene.json")

    def snapshot(index: int, pose) -> None:
        """One PNG per pose, with the candidate rings for that pose -- which is what shows why the
        next move was or was not available. Not what the model saw; that is request.png."""
        (out / f"step{index:02d}.png").write_bytes(render(scene, pose, candidates(scene, pose)))

    def on_step(index, move):
        print(f"  {index:2d}. {move.moving_limb:11s} {move.from_hold_id:3d} -> "
              f"{move.target_hold_id:3d} {'!' if move.forced else ' '} {move.reason}")
        if out:
            snapshot(index, move.pose)

    print(f"wall={args.scene or args.wall} holds={len(scene.holds)} "
          f"top={scene.route.top_hold_id} model={chooser.name}")
    if out:
        snapshot(0, initial_pose(scene))
    result = plan(scene, chooser, max_moves=args.max_moves, with_image=not args.no_image,
                  skip_filters=args.skip_filters, on_step=on_step)
    print(f"reached_top={result.reached_top} executed={len(result.moves)}/{result.proposed} "
          f"valid_move_rate={result.valid_move_rate:.2f} repeated_limb={result.repeated_limb} "
          f"backtracks={result.backtracks} forced={result.forced} {result.stopped}")
    if out:
        (out / "plan.json").write_text(json.dumps(result.to_dict(), indent=2))
        if result.request_png:
            (out / "request.png").write_bytes(result.request_png)
        print(f"wrote {out}")
    return 0 if result.reached_top else 1


if __name__ == "__main__":
    raise SystemExit(main())

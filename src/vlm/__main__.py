"""Plan one of the exported walls (/walls) and print the moves.

    PYTHONPATH=src python -m vlm --wall 3                     # Gemini
    PYTHONPATH=src python -m vlm --wall 3 --model gpt-6-luna   # OpenAI
    PYTHONPATH=src python -m vlm --wall 3 --offline           # no API key, greedy baseline
    PYTHONPATH=src python -m vlm --wall 3 --out out/wall3     # + scene.json, plan.json, step PNGs
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from .candidates import candidates, initial_pose
from .planner import plan
from .providers import GeminiChooser, GreedyChooser, OpenAIChooser
from .render import render
from .scene import Scene, wall


def main() -> int:
    ap = argparse.ArgumentParser(prog="vlm")
    ap.add_argument("--wall", type=int, default=0, help="index into /walls")
    ap.add_argument("--scene", type=Path, help="scene JSON to plan on instead of an exported wall")
    ap.add_argument("--model", default="gemini-3.5-flash-lite")
    ap.add_argument("--offline", action="store_true", help="greedy chooser, no model call")
    ap.add_argument("--no-image", action="store_true", help="JSON only, to measure the image's worth")
    ap.add_argument("--max-moves", type=int, default=60)
    ap.add_argument("--out", type=Path, help="scene.json, plan.json, and the exact PNG sent to the "
                                             "model at each step (stepNN.png) plus final.png")
    args = ap.parse_args()

    scene = Scene.load(args.scene) if args.scene else wall(args.wall)
    # The model name picks the provider; anything else is a class in providers.py you pass to plan().
    if args.offline:
        chooser = GreedyChooser(scene)
    elif args.model.startswith("gemini"):
        chooser = GeminiChooser(args.model)
    else:
        chooser = OpenAIChooser(args.model)
    out = args.out
    if out:
        out.mkdir(parents=True, exist_ok=True)
        scene.save(out / "scene.json")

    def on_step(index, move, image):
        print(f"  {index:2d}. {move.moving_limb:11s} -> {move.target_hold_id:3d}"
              f"{'  [fallback]' if move.fell_back else ''}  {move.reason}")
        if out and image:
            (out / f"step{index:02d}.png").write_bytes(image)

    print(f"wall={args.scene or args.wall} holds={len(scene.holds)} top={scene.route.top_hold_id} model={chooser.name}")
    result = plan(scene, chooser, max_moves=args.max_moves, with_image=not args.no_image, on_step=on_step)
    print(f"reached_top={result.reached_top} moves={len(result.moves)} "
          f"valid_move_rate={result.valid_move_rate:.2f} repeated_limb={result.repeated_limb} "
          f"backtracks={result.backtracks} {result.stopped}")
    if out:
        (out / "plan.json").write_text(json.dumps(result.to_dict(), indent=2))
        # The step PNGs stop at the last move, and a plan that stalls stalls one pose later --
        # which is the pose worth looking at.
        pose = result.moves[-1].pose if result.moves else initial_pose(scene)
        (out / "final.png").write_bytes(render(scene, pose, candidates(scene, pose)))
        print(f"wrote {out}")
    return 0 if result.reached_top else 1


if __name__ == "__main__":
    raise SystemExit(main())

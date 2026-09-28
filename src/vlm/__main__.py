"""Plan one of the exported walls (/walls) and print the moves.

    PYTHONPATH=src python -m vlm --wall 3                      # Gemini (gemini-3.8-flash)
    PYTHONPATH=src python -m vlm --wall 3 --model gpt          # OpenAI (gpt-6-sol)
    PYTHONPATH=src python -m vlm --wall 3 --model gpt-6-luna   # any exact model name
    PYTHONPATH=src python -m vlm --wall 3 --offline            # no API key, greedy baseline
    PYTHONPATH=src python -m vlm --wall 3 --out out/wall3      # + scene.json, plan.json, PNGs
    PYTHONPATH=src python -m vlm --wall 3 --oneshot            # whole sequence in one request
    PYTHONPATH=src python -m vlm --wall 3 --oneshot --skip-filters   # referee off, read it all

Default is the step-by-step planner: one request per move, candidates in the prompt, re-planned from
the new pose. `--oneshot` asks once for the whole sequence and replays it (docs/04).
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from .candidates import candidates, initial_pose
from .planner import plan_oneshot, plan_steps
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
    ap.add_argument("--oneshot", action="store_true",
                    help="one request for the whole sequence, then replay it, instead of one "
                         "request per move")
    ap.add_argument("--skip-filters", action="store_true",
                    help="--oneshot only: replay with every rule off -- route, occupancy, reach, "
                         "posture -- so the whole sequence runs instead of stopping at the first bad "
                         "move. The moves that would have been refused are marked (!). Never when "
                         "measuring.")
    ap.add_argument("--out", type=Path, help="scene.json, plan.json, request.png (--oneshot: the "
                                            "exact image sent to the model) and one PNG per pose: "
                                            "step00.png is the start, stepNN.png the pose after "
                                            "move NN")
    args = ap.parse_args()
    if args.skip_filters and not args.oneshot:
        # Step by step there is nothing to waive: the candidate list the model chooses from *is* the
        # filter, so switching the referee off would leave it choosing from nothing.
        ap.error("--skip-filters only applies to --oneshot")

    scene = Scene.load(args.scene) if args.scene else wall(args.wall)
    chooser = chooser_for("greedy" if args.offline else args.model, scene)
    out = args.out
    if out:
        out.mkdir(parents=True, exist_ok=True)
        scene.save(out / "scene.json")

    def snapshot(index: int, pose) -> None:
        """One PNG per pose, with the candidate rings for that pose -- which is what shows why the
        next move was or was not available. One-shot: not what the model saw; that is request.png."""
        (out / f"step{index:02d}.png").write_bytes(render(scene, pose, candidates(scene, pose)))

    def on_step(index, move):
        flag = "!" if move.forced else "*" if move.fell_back else " "
        print(f"  {index:2d}. {move.moving_limb:11s} {move.from_hold_id:3d} -> "
              f"{move.target_hold_id:3d} {flag}")
        if out:
            snapshot(index, move.pose)

    mode = "oneshot" if args.oneshot else "steps"
    print(f"wall={args.scene or args.wall} holds={len(scene.holds)} "
          f"top={scene.route.top_hold_id} model={chooser.name} mode={mode}")
    if out:
        snapshot(0, initial_pose(scene))
    if args.oneshot:
        result = plan_oneshot(scene, chooser, max_moves=args.max_moves,
                              with_image=not args.no_image, skip_filters=args.skip_filters,
                              on_step=on_step)
        counts = f"executed={len(result.moves)}/{result.proposed} forced={result.forced}"
    else:
        result = plan_steps(scene, chooser, max_moves=args.max_moves,
                            with_image=not args.no_image, on_step=on_step)
        counts = f"moves={len(result.moves)} requests={result.requests} invalid={result.invalid}"
    print(f"reached_top={result.reached_top} {counts} "
          f"valid_move_rate={result.valid_move_rate:.2f} repeated_limb={result.repeated_limb} "
          f"backtracks={result.backtracks} {result.stopped}")
    if out:
        (out / "plan.json").write_text(json.dumps(result.to_dict(), indent=2))
        if result.request_png:
            (out / "request.png").write_bytes(result.request_png)
        print(f"wrote {out}")
    return 0 if result.reached_top else 1


if __name__ == "__main__":
    raise SystemExit(main())

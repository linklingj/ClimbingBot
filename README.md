# ClimbingBot

A system that perceives a real climbing wall, plans high-level climbing poses with a VLM, generates the physical motion with reinforcement learning, and visualizes the result on the actual wall in AR.

## Architecture

Holds are detected from an iPhone camera image via instance segmentation, grouped into routes using color, spatial relations, and reachability, then projected onto an AR plane to produce a Scene JSON in wall-local coordinates. A Candidate Generator restricts the choices to holds reachable from the current body state, the VLM picks which limb to move and to which hold within that set, and a Unity ML-Agents ragdoll executes the resulting target pose as actual joint motion. The generated motion is overlaid on the real wall through AR Foundation.

Each stage is connected only by explicit JSON contracts, so modules can be replaced independently. See [`docs/`](docs/) for the detailed design.

## Status

**In progress.** Only design documents exist so far; implementation has not started. The development order is RL → VLM → Perception → AR → Integration.

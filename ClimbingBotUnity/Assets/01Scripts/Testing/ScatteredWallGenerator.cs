using System.Collections.Generic;
using UnityEngine;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace ClimbingBot.Testing
{
    /// <summary>
    /// Dresses a ClimbingWall with holds scattered over its whole face, with no route through them.
    ///
    /// This is the Stage 1 wall (docs/05). That episode starts from a fixed pose and names one limb
    /// and one target hold, so what matters is that candidate targets exist in every direction
    /// around the climber -- not that the holds form a climbable line. RandomWallGenerator is the
    /// opposite and belongs to Stage 2. Both implement IWallGenerator; enable one.
    ///
    /// No hold is Start or Top. This wall has no route to finish, so topping out is not the episode
    /// end here and ClimbingWall.TopHold is null by design.
    ///
    /// Throwaway, like the other generator: the shipped system takes its holds from CV output.
    /// </summary>
    [RequireComponent(typeof(ClimbingWall))]
    public class ScatteredWallGenerator : MonoBehaviour, IWallGenerator
    {
        [Header("Scatter")]
        [Tooltip("How many holds to aim for. Fewer are placed if the wall runs out of room at this separation.")]
        public int holdCount = 70;

        [Tooltip("No two holds end up closer than this (m). Sets how many holds fall inside a limb's reach, which is what decides whether a reachable target exists at all.")]
        public float minSeparation = 0.38f;

        [Tooltip("Keeps holds off the wall edges (m).")]
        public float margin = 0.3f;

        [Header("Colors")]
        public Color normalColor = new Color(0.85f, 0.85f, 0.87f);

        // Dart throwing gives up eventually rather than spinning: at a tight separation the wall
        // simply cannot hold holdCount holds, and the caller gets however many fit.
        const int k_AttemptsPerHold = 30;

        public ClimbingWall Wall => GetComponent<ClimbingWall>();

#if ODIN_INSPECTOR
        [Button("Generate with random seed")]
#else
        [ContextMenu("Generate with random seed")]
#endif
        public void GenerateWithRandomSeed()
        {
            Generate(Random.Range(int.MinValue, int.MaxValue));
        }

#if ODIN_INSPECTOR
        // No [ContextMenu] fallback: it only binds parameterless methods.
        [Button("Generate with seed")]
#endif
        public void Generate(int seed)
        {
            Generate(seed, null);
        }

        /// <summary>
        /// Scatters as usual, but places <paramref name="anchors"/> first and unconditionally.
        /// Stage 1 uses this to put holds exactly under a fixed start stance -- the stance decides
        /// where they go, so they are exempt from minSeparation against each other (feet sit closer
        /// together than any scattered pair would). Scattered holds still keep their distance from
        /// them, so an anchor is never ambiguous with its neighbours.
        /// </summary>
        public void Generate(int seed, IReadOnlyList<Vector2> anchors)
        {
            var wall = Wall;
            wall.ClearHolds();

            var rng = new System.Random(seed);
            var placed = new List<Vector2>();

            var anchorCount = 0;
            if (anchors != null)
            {
                foreach (var anchor in anchors)
                {
                    placed.Add(anchor);
                }

                anchorCount = placed.Count;
            }

            var attempts = holdCount * k_AttemptsPerHold;
            for (var i = 0; i < attempts && placed.Count < holdCount + anchorCount; i++)
            {
                var candidate = new Vector2(
                    Mathf.Lerp(margin, wall.width - margin, (float)rng.NextDouble()),
                    Mathf.Lerp(margin, wall.height - margin, (float)rng.NextDouble()));

                if (IsCrowded(placed, candidate))
                {
                    continue;
                }

                placed.Add(candidate);
            }

            // Bottom-up ids, so a hold's number says roughly how high it is and the hierarchy reads
            // in the same order as the wall. Anchors sort in with the rest -- the caller finds them
            // by position, not by id, so they need no special place in the ordering.
            placed.Sort((a, b) => a.y.CompareTo(b.y));

            for (var i = 0; i < placed.Count; i++)
            {
                var hold = wall.AddHold(i, placed[i]);
                hold.role = HoldRole.Normal;
                hold.color = normalColor;
                hold.ApplyColor();
            }
        }

        // ponytail: linear scan against everything placed so far. At these counts that is a few
        // thousand comparisons per wall. Bucket into a grid only if hold counts reach the hundreds.
        bool IsCrowded(List<Vector2> placed, Vector2 candidate)
        {
            foreach (var position in placed)
            {
                if (Vector2.Distance(position, candidate) < minSeparation)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

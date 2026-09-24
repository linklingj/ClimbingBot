using UnityEngine;

namespace ClimbingBot.Testing
{
    /// <summary>
    /// Dresses a ClimbingWall with one randomized route.
    ///
    /// Throwaway: the shipped system takes its holds from CV output, not from here. This exists so
    /// Phase 1 can train against random layouts before any perception code runs, so keep it on the
    /// ClimbingWall's public API and out of the wall itself.
    /// </summary>
    [RequireComponent(typeof(ClimbingWall))]
    public class RandomWallGenerator : MonoBehaviour
    {
        [Header("Layout")]
        public float firstRowHeight = 0.7f;
        public float rowSpacing = 0.55f;

        [Tooltip("Upper bound on the distance between consecutive holds. Every generated route stays climbable because the horizontal step is derived from this.")]
        public float maxReach = 0.9f;

        public float sideMargin = 0.5f;

        [Header("Colors")]
        public Color normalColor = new Color(0.85f, 0.85f, 0.87f);
        public Color startColor = new Color(0.35f, 0.75f, 0.35f);
        public Color topColor = new Color(0.9f, 0.3f, 0.25f);

        public ClimbingWall Wall => GetComponent<ClimbingWall>();

        [ContextMenu("Generate")]
        public void GenerateWithRandomSeed()
        {
            Generate(Random.Range(int.MinValue, int.MaxValue));
        }

        public void Generate(int seed)
        {
            var wall = Wall;
            wall.ClearHolds();

            // Consecutive holds are one row apart vertically, so bounding the horizontal step at
            // sqrt(maxReach^2 - rowSpacing^2) bounds the step distance at maxReach by construction.
            var maxHorizontalStep = Mathf.Sqrt(Mathf.Max(0f, maxReach * maxReach - rowSpacing * rowSpacing));
            var minX = sideMargin;
            var maxX = wall.width - sideMargin;

            var rng = new System.Random(seed);
            var x = Mathf.Lerp(minX, maxX, (float)rng.NextDouble());
            var rows = Mathf.FloorToInt((wall.height - sideMargin - firstRowHeight) / rowSpacing) + 1;

            for (var i = 0; i < rows; i++)
            {
                if (i > 0)
                {
                    x = Mathf.Clamp(x + (float)(rng.NextDouble() * 2.0 - 1.0) * maxHorizontalStep, minX, maxX);
                }

                var hold = wall.AddHold(i, new Vector2(x, firstRowHeight + i * rowSpacing));
                hold.role = i == 0 ? HoldRole.Start
                    : i == rows - 1 ? HoldRole.Top
                    : HoldRole.Normal;
                hold.color = hold.role == HoldRole.Start ? startColor
                    : hold.role == HoldRole.Top ? topColor
                    : normalColor;
                hold.ApplyColor();
            }
        }
    }
}

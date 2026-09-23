using System.Collections.Generic;
using UnityEngine;

namespace ClimbingBot
{
    /// <summary>
    /// A vertical wall of fixed size whose hold layout is randomized per episode.
    /// The transform sits at the wall's bottom-left corner: +X right, +Y up, climber on -Z.
    /// </summary>
    public class ClimbingWall : MonoBehaviour
    {
        [Header("Wall (fixed)")]
        public float width = 4f;
        public float height = 6f;
        public float thickness = 0.3f;

        [Header("Hold layout")]
        public float holdRadius = 0.07f;
        public float firstRowHeight = 0.7f;
        public float rowSpacing = 0.55f;

        [Tooltip("Upper bound on the distance between consecutive holds. Every generated wall stays climbable because the horizontal step is derived from this.")]
        public float maxReach = 0.9f;

        public float sideMargin = 0.5f;

        [Header("Materials")]
        public Material wallMaterial;
        public Material holdMaterial;

        [Header("Colors")]
        public Color normalColor = new Color(0.85f, 0.85f, 0.87f);
        public Color startColor = new Color(0.35f, 0.75f, 0.35f);
        public Color topColor = new Color(0.9f, 0.3f, 0.25f);

        [SerializeField] List<Hold> holds = new List<Hold>();

        public IReadOnlyList<Hold> Holds => holds;
        public Hold TopHold => holds.Find(h => h != null && h.role == HoldRole.Top);
        public IEnumerable<Hold> StartHolds => holds.FindAll(h => h != null && h.role == HoldRole.Start);

        /// <summary>Wall-local 2D (m, origin bottom-left) to world, on the climbing surface.</summary>
        public Vector3 WallToWorld(Vector2 wallPosition)
        {
            return transform.TransformPoint(new Vector3(wallPosition.x, wallPosition.y, 0f));
        }

        [ContextMenu("Generate")]
        public void GenerateWithRandomSeed()
        {
            Generate(Random.Range(int.MinValue, int.MaxValue));
        }

        public void Generate(int seed)
        {
            ClearChildren();
            BuildSlab();

            // Consecutive holds are one row apart vertically, so bounding the horizontal step at
            // sqrt(maxReach^2 - rowSpacing^2) bounds the step distance at maxReach by construction.
            var maxHorizontalStep = Mathf.Sqrt(Mathf.Max(0f, maxReach * maxReach - rowSpacing * rowSpacing));
            var minX = sideMargin;
            var maxX = width - sideMargin;

            var rng = new System.Random(seed);
            var x = Mathf.Lerp(minX, maxX, (float)rng.NextDouble());
            var rows = Mathf.FloorToInt((height - sideMargin - firstRowHeight) / rowSpacing) + 1;

            for (var i = 0; i < rows; i++)
            {
                if (i > 0)
                {
                    x = Mathf.Clamp(x + (float)(rng.NextDouble() * 2.0 - 1.0) * maxHorizontalStep, minX, maxX);
                }

                holds.Add(CreateHold(i, new Vector2(x, firstRowHeight + i * rowSpacing)));
            }

            holds[0].role = HoldRole.Start;
            holds[holds.Count - 1].role = HoldRole.Top;

            foreach (var hold in holds)
            {
                hold.color = hold.role == HoldRole.Start ? startColor
                    : hold.role == HoldRole.Top ? topColor
                    : normalColor;
                hold.ApplyColor();
            }
        }

        Hold CreateHold(int id, Vector2 wallPosition)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Hold_" + id;
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(wallPosition.x, wallPosition.y, -holdRadius);
            go.transform.localScale = Vector3.one * (holdRadius * 2f);
            go.GetComponent<MeshRenderer>().sharedMaterial = holdMaterial;

            var hold = go.AddComponent<Hold>();
            hold.id = id;
            hold.wallPosition = wallPosition;
            return hold;
        }

        void BuildSlab()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Slab";
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(width * 0.5f, height * 0.5f, thickness * 0.5f);
            go.transform.localScale = new Vector3(width, height, thickness);
            go.GetComponent<MeshRenderer>().sharedMaterial = wallMaterial;
        }

        void ClearChildren()
        {
            holds.Clear();
            // Immediate, not deferred: a regenerated wall must not leave last episode's colliders
            // standing for the rest of the frame.
            for (var i = transform.childCount - 1; i >= 0; i--)
            {
                DestroyImmediate(transform.GetChild(i).gameObject);
            }
        }
    }
}

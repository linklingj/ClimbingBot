using System.Collections.Generic;
using UnityEngine;

namespace ClimbingBot
{
    /// <summary>
    /// A vertical wall of fixed size and the holds sitting on it.
    /// The transform sits at the wall's bottom-left corner: +X right, +Y up, climber on -Z.
    ///
    /// Where the holds go is somebody else's decision -- random generation during Phase 1 training,
    /// CV output later. This only knows how to build one and how to look them up.
    /// </summary>
    public class ClimbingWall : MonoBehaviour
    {
        [Header("Wall (fixed)")]
        public float width = 4f;
        public float height = 6f;
        public float thickness = 0.3f;

        [Header("Holds")]
        public float holdRadius = 0.07f;

        [Header("Materials")]
        public Material wallMaterial;
        public Material holdMaterial;

        [SerializeField] List<Hold> holds = new List<Hold>();

        public IReadOnlyList<Hold> Holds => holds;
        public Hold TopHold => holds.Find(h => h != null && h.role == HoldRole.Top);
        public IEnumerable<Hold> StartHolds => holds.FindAll(h => h != null && h.role == HoldRole.Start);

        /// <summary>Wall-local 2D (m, origin bottom-left) to world, on the climbing surface.</summary>
        public Vector3 WallToWorld(Vector2 wallPosition)
        {
            return transform.TransformPoint(new Vector3(wallPosition.x, wallPosition.y, 0f));
        }

        [ContextMenu("Build Slab")]
        public void BuildSlab()
        {
            var existing = transform.Find("Slab");
            if (existing != null)
            {
                DestroyImmediate(existing.gameObject);
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "Slab";
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(width * 0.5f, height * 0.5f, thickness * 0.5f);
            go.transform.localScale = new Vector3(width, height, thickness);
            go.GetComponent<MeshRenderer>().sharedMaterial = wallMaterial;
        }

        /// <summary>Adds a hold at a wall-local position. Caller sets role and color.</summary>
        public Hold AddHold(int id, Vector2 wallPosition)
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
            holds.Add(hold);
            return hold;
        }

        [ContextMenu("Clear Holds")]
        public void ClearHolds()
        {
            // Immediate, not deferred: a re-dressed wall must not leave last episode's colliders
            // standing for the rest of the frame. Sweeps children rather than the list so
            // hand-placed holds go too.
            foreach (var hold in GetComponentsInChildren<Hold>(true))
            {
                DestroyImmediate(hold.gameObject);
            }

            holds.Clear();
        }
    }
}

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
        public GameObject holdPrefab;
        public float holdRadius = 0.07f;

        [Header("Materials")]
        public Material wallMaterial;

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

        /// <summary>
        /// Adds a hold at a wall-local position. Caller sets role and color.
        ///
        /// Stage 1 tears down and rebuilds ~70 holds every episode (docs/05 measured this as 45% of
        /// wall-clock at 16 areas). Drawn from ObjectPoolManager when one is configured for
        /// holdPrefab; a plain Instantiate whenever it isn't -- no manager placed, one placed for
        /// something else, or edit mode -- so scenes that never set up pooling behave exactly as
        /// before.
        /// </summary>
        public Hold AddHold(int id, Vector2 wallPosition)
        {
            // Play mode only. The manager fills its pools in Start(), so in edit mode -- the
            // generators' inspector buttons -- there is nothing to draw from and asking would log a
            // miss per hold.
            var pool = Application.isPlaying ? ObjectPoolManager.Instance : null;
            var go = pool != null ? pool.Get(holdPrefab) : null;
            if (go == null)
            {
                go = Instantiate(holdPrefab);
            }

            go.name = "Hold_" + id;
            // A pooled instance may arrive parented under a previous wall (or the pool itself) with
            // whatever local transform it last had; false means don't fight that with a
            // world-position-preserving reparent, since every value gets overwritten next anyway.
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(wallPosition.x, wallPosition.y, -holdRadius);
            go.transform.localRotation = Quaternion.identity;
            // The prefab's own authored scale, not Vector3.one -- Hold_0 is authored at 0.14, and a
            // reused pooled instance needs resetting back to that, not to an unrelated "identity".
            go.transform.localScale = holdPrefab.transform.localScale;

            var hold = go.GetComponent<Hold>();
            hold.id = id;
            hold.wallPosition = wallPosition;
            holds.Add(hold);
            return hold;
        }

        [ContextMenu("Clear Holds")]
        public void ClearHolds()
        {
            // Active only, not includeInactive: a released-to-pool hold sits inactive under
            // whichever wall last owned it (ObjectPoolManager.Release doesn't reparent) until Get()
            // claims it back out. Sweeping inactive children too would re-release the same
            // PoolObject a second time -- enqueuing it twice, so two different holds could later
            // Get() the same instance. Every hold currently in play is active by construction
            // (nothing else in this codebase deactivates one), so this still finds all of them,
            // hand-placed test holds included.
            foreach (var hold in GetComponentsInChildren<Hold>())
            {
#if UNITY_EDITOR
                // Selecting a hold and then regenerating leaves the Inspector holding a destroyed
                // object, which throws on every domain reload until the selection is replaced.
                if (UnityEditor.Selection.activeGameObject == hold.gameObject)
                {
                    UnityEditor.Selection.activeGameObject = gameObject;
                }
#endif
                // Immediate either way: SetActive(false) inside Release() drops the collider this
                // frame same as DestroyImmediate does, so a re-dressed wall never leaves last
                // episode's colliders standing. Only a hold that was never pool-registered (no
                // manager was configured for holdPrefab when it was added) gets destroyed here.
                var poolObject = hold.GetComponent<PoolObject>();
                if (poolObject != null && poolObject.PrefabID != -1)
                {
                    poolObject.Release();
                }
                else
                {
                    DestroyImmediate(hold.gameObject);
                }
            }

            holds.Clear();
        }
    }
}

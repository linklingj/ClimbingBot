using System.Collections.Generic;
using System.Reflection;
using Sirenix.OdinInspector;
using UnityEngine;
using UnityEngine.Splines;

namespace ClimbingBot.Testing
{
    /// <summary>
    /// Dresses a ClimbingWall with one randomized route drawn along a spline.
    ///
    /// Start and top holds are fixed points of the route. Everything between -- and below the start
    /// hold, since the spline runs down past it -- is filled in by SplineInstantiate and then baked
    /// into real holds owned by the wall.
    ///
    /// Throwaway: the shipped system takes its holds from CV output, not from here.
    /// </summary>
    [RequireComponent(typeof(ClimbingWall))]
    public class RandomWallGenerator : MonoBehaviour
    {
        [Header("Fixed holds (wall-local height, m)")]
        [Tooltip("Only the height is fixed. The x is read off the spline, so the start hold sits on the route rather than beside it.")]
        public float startHoldY = 1.3f;

        [Tooltip("Only the height is fixed. The x is random, and the spline ends on it.")]
        public float topHoldY = 5.3f;

        [Header("Route spline")]
        [Tooltip("Height where the spline begins. Below the start hold, so the route also has holds under it.")]
        public float splineBottomY = 0.5f;

        public int minKnots = 2;
        public int maxKnots = 4;
        public float sideMargin = 0.5f;

        [Header("Hold placement")]
        [Tooltip("Spacing along the spline is drawn per hold from this range, so the route is not evenly rungged.")]
        public float minHoldSpacing = 0.35f;

        public float maxHoldSpacing = 0.6f;

        [Tooltip("Sideways jitter per hold, for a less mechanical line. Clamped to (maxReach - maxHoldSpacing) / 2 so no gap can exceed maxReach.")]
        public float positionOffsetX;

        [Tooltip("Upper bound on the distance between consecutive holds.")]
        public float maxReach;

        [Header("Colors")]
        public Color normalColor = new Color(0.85f, 0.85f, 0.87f);
        public Color startColor = new Color(0.35f, 0.75f, 0.35f);
        public Color topColor = new Color(0.9f, 0.3f, 0.25f);

        public ClimbingWall Wall => GetComponent<ClimbingWall>();

        [Button("Generate with random seed")]
        public void GenerateWithRandomSeed()
        {
            Generate(Random.Range(int.MinValue, int.MaxValue));
        }

        [Button("Generate with seed")]
        public void Generate(int seed)
        {
            var wall = Wall;
            wall.ClearHolds();

            var rng = new System.Random(seed);
            var topHold = new Vector2(RandomX(wall, rng), topHoldY);
            var container = BuildSpline(wall, rng, topHold);
            var startHold = new Vector2(SplineXAtHeight(container, startHoldY), startHoldY);
            var placed = BakeAlongSpline(wall, container, seed);

            var route = new List<(Vector2 position, HoldRole role)>();
            foreach (var worldPosition in placed)
            {
                var local = wall.transform.InverseTransformPoint(worldPosition);
                var wallPosition = new Vector2(local.x, local.y);

                // The spline ends on the top hold and passes through the start hold, so drop whatever
                // it drops on them rather than stacking two holds in one spot. Half the *tightest*
                // spacing, so this never eats a hold that is legitimately its own.
                var overlap = minHoldSpacing * 0.5f;
                if (Vector2.Distance(wallPosition, startHold) < overlap) continue;
                if (Vector2.Distance(wallPosition, topHold) < overlap) continue;

                route.Add((wallPosition, HoldRole.Normal));
            }

            route.Add((startHold, HoldRole.Start));
            route.Add((topHold, HoldRole.Top));
            route.Sort((a, b) => a.position.y.CompareTo(b.position.y));
            BridgeGaps(route);

            for (var i = 0; i < route.Count; i++)
            {
                var hold = wall.AddHold(i, route[i].position);
                hold.role = route[i].role;
                hold.color = hold.role == HoldRole.Start ? startColor
                    : hold.role == HoldRole.Top ? topColor
                    : normalColor;
                hold.ApplyColor();
            }
        }

        /// <summary>
        /// Spline spacing and jitter alone keep consecutive holds within reach, but start and top
        /// sit where they are told and can land further from their neighbours than the spline hold
        /// they replaced. Halving any gap that is still too wide bounds every gap at maxReach.
        /// </summary>
        void BridgeGaps(List<(Vector2 position, HoldRole role)> route)
        {
            var i = 1;
            while (i < route.Count)
            {
                if (Vector2.Distance(route[i].position, route[i - 1].position) > maxReach)
                {
                    route.Insert(i, ((route[i].position + route[i - 1].position) * 0.5f, HoldRole.Normal));
                    continue;
                }

                i++;
            }
        }

        float RandomX(ClimbingWall wall, System.Random rng)
        {
            return Mathf.Lerp(sideMargin, wall.width - sideMargin, (float)rng.NextDouble());
        }

        /// <summary>
        /// Walks the spline for the first place it crosses the given height. Knots always climb, so
        /// a crossing exists for any height the spline spans.
        /// </summary>
        float SplineXAtHeight(SplineContainer container, float height)
        {
            const int samples = 256;
            var spline = container.Spline;
            var previous = spline.EvaluatePosition(0f);

            for (var i = 1; i <= samples; i++)
            {
                var current = spline.EvaluatePosition(i / (float)samples);
                var spans = (previous.y - height) * (current.y - height) <= 0f;
                if (spans && !Mathf.Approximately(previous.y, current.y))
                {
                    return Mathf.Lerp(previous.x, current.x, (height - previous.y) / (current.y - previous.y));
                }

                previous = current;
            }

            return previous.x;
        }

        SplineContainer BuildSpline(ClimbingWall wall, System.Random rng, Vector2 topHold)
        {
            var root = transform.Find("RouteSpline");
            if (root == null)
            {
                root = new GameObject("RouteSpline").transform;
                root.SetParent(transform, false);
            }

            root.localPosition = new Vector3(0f, 0f, -wall.holdRadius);
            var container = root.GetComponent<SplineContainer>();
            if (container == null)
            {
                container = root.gameObject.AddComponent<SplineContainer>();
            }

            var spline = container.Spline;
            spline.Clear();

            var knots = minKnots + rng.Next(Mathf.Max(1, maxKnots - minKnots + 1));

            // The bottom knot starts below the start hold's height, so the route keeps going under it.
            spline.Add(new BezierKnot(new Unity.Mathematics.float3(RandomX(wall, rng), splineBottomY, 0f)), TangentMode.AutoSmooth);
            for (var i = 1; i < knots - 1; i++)
            {
                var y = Mathf.Lerp(splineBottomY, topHold.y, i / (float)(knots - 1));
                spline.Add(new BezierKnot(new Unity.Mathematics.float3(RandomX(wall, rng), y, 0f)), TangentMode.AutoSmooth);
            }

            spline.Add(new BezierKnot(new Unity.Mathematics.float3(topHold.x, topHold.y, 0f)), TangentMode.AutoSmooth);
            return container;
        }

        /// <summary>
        /// Runs SplineInstantiate once and returns where it put things. Its instances are
        /// HideAndDontSave and it owns their lifetime, so they are read for position and dropped --
        /// the wall keeps real, serialized holds instead.
        /// </summary>
        List<Vector3> BakeAlongSpline(ClimbingWall wall, SplineContainer container, int seed)
        {
            var instantiate = container.gameObject.AddComponent<SplineInstantiate>();
            instantiate.Container = container;
            instantiate.itemsToInstantiate = new[]
            {
                new SplineInstantiate.InstantiableItem { Prefab = wall.holdPrefab, Probability = 1f }
            };
            instantiate.InstantiateMethod = SplineInstantiate.Method.SpacingDistance;
            instantiate.MinSpacing = Mathf.Min(minHoldSpacing, maxHoldSpacing);
            instantiate.MaxSpacing = Mathf.Max(minHoldSpacing, maxHoldSpacing);

            // The widest spacing is the one that can breach maxReach, so clamp jitter against that.
            var jitter = Mathf.Min(positionOffsetX, Mathf.Max(0f, (maxReach - instantiate.MaxSpacing) * 0.5f));
            instantiate.MinPositionOffset = new Vector3(-jitter, 0f, 0f);
            instantiate.MaxPositionOffset = new Vector3(jitter, 0f, 0f);
            instantiate.PositionSpace = SplineInstantiate.OffsetSpace.Local;
            instantiate.Seed = seed;
            EnableRandomXOffset(instantiate);
            instantiate.UpdateInstances();

            var points = new List<Vector3>();
            foreach (var hold in container.GetComponentsInChildren<Hold>(true))
            {
                points.Add(hold.transform.position);
            }

            instantiate.Clear();
            DestroyImmediate(instantiate);
            return points;
        }

        // SplineInstantiate exposes the min/max position offset but not the per-axis "randomize"
        // toggles behind them, so without this the offset is applied as a constant and Seed does
        // nothing. Private as of com.unity.splines 2.8.4 -- if an upgrade moves it, holds silently
        // stop jittering sideways.
        static void EnableRandomXOffset(SplineInstantiate instantiate)
        {
            var offsetField = typeof(SplineInstantiate).GetField("m_PositionOffset", BindingFlags.NonPublic | BindingFlags.Instance);
            if (offsetField == null)
            {
                Debug.LogWarning("SplineInstantiate.m_PositionOffset not found; holds will not jitter sideways.");
                return;
            }

            var offset = offsetField.GetValue(instantiate);
            var randomX = offset.GetType().GetField("randomX");
            if (randomX == null)
            {
                Debug.LogWarning("Vector3Offset.randomX not found; holds will not jitter sideways.");
                return;
            }

            randomX.SetValue(offset, true);
            offsetField.SetValue(instantiate, offset);
        }
    }
}

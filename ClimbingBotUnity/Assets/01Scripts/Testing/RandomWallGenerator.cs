using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
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
    public class RandomWallGenerator : MonoBehaviour, IWallGenerator
    {
        [Header("Fixed holds (wall-local height, m)")]
        [Tooltip("Only the height is fixed. The x is read off the spline, so the start hold sits on the route rather than beside it.")]
        public float startHoldY = 1.3f;

        [Range(0f, 1f)]
        [Tooltip("Chance that the route starts on two holds side by side instead of one. With one, both hands match on it; with two, the climber leaves the ground with a hand on each.")]
        public float twoStartHoldsChance = 0.5f;

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

        [Tooltip("Lowest a generated foot hold may sit. The wall's floor is at 0, and a hold on the floor is not something to stand on.")]
        public float footHoldFloorY = 0.15f;

        [Header("Foot holds")]
        [Tooltip("Second pass down the same curve, this far lower. One line of holds makes the feet fight the hands for it. 0 turns the pass off.")]
        public float footDropY = 1f;

        [Header("Export")]
        [Tooltip("How many walls the export button writes, seeded 0..count-1.")]
        public int exportCount = 20;

        [Header("Colors")]
        public Color normalColor = new Color(0.85f, 0.85f, 0.87f);
        public Color startColor = new Color(0.35f, 0.75f, 0.35f);
        public Color topColor = new Color(0.9f, 0.3f, 0.25f);

        public ClimbingWall Wall => GetComponent<ClimbingWall>();

        [ContextMenu("Generate with random seed")]
        public void GenerateWithRandomSeed()
        {
            Generate(Random.Range(int.MinValue, int.MaxValue));
        }

        public void Generate(int seed)
        {
            var wall = Wall;
            wall.ClearHolds();

            var rng = new System.Random(seed);
            var topHold = new Vector2(RandomX(wall, rng), topHoldY);
            var container = BuildSpline(wall, rng, topHold);
            var startHolds = StartHolds(wall, container, rng);

            var route = new List<(Vector2 position, HoldRole role)>();
            foreach (var wallPosition in BakeWallPositions(wall, container, seed, 0f))
            {
                // The spline ends on the top hold and passes through the start hold, so drop whatever
                // it drops on them rather than stacking two holds in one spot. Half the *tightest*
                // spacing, so this never eats a hold that is legitimately its own.
                var overlap = minHoldSpacing * 0.5f;
                if (startHolds.Exists(start => Vector2.Distance(wallPosition, start) < overlap)) continue;
                if (Vector2.Distance(wallPosition, topHold) < overlap) continue;

                route.Add((wallPosition, HoldRole.Normal));
            }

            foreach (var start in startHolds) route.Add((start, HoldRole.Start));
            route.Add((topHold, HoldRole.Top));
            route.Sort((a, b) => a.position.y.CompareTo(b.position.y));
            BridgeGaps(route);
            AddFootHolds(wall, container, route, seed);
            EnsureFootHolds(wall, route, rng);
            route.Sort((a, b) => a.position.y.CompareTo(b.position.y));

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
        /// Writes exportCount walls to /walls as Scene JSON (docs/07) and leaves the last one
        /// standing. This is the only route generator in the project -- the planner in src/vlm
        /// reads these files rather than carrying a second copy of the algorithm, so anything that
        /// changes the shape of a wall has to be re-exported and committed.
        /// </summary>
#if UNITY_EDITOR
        [ContextMenu("Export walls")]
        public void ExportWalls()
        {
            // Application.dataPath is <repo>/ClimbingBotUnity/Assets.
            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "walls"));
            Directory.CreateDirectory(directory);

            for (var seed = 0; seed < exportCount; seed++)
            {
                Generate(seed);
                File.WriteAllText(Path.Combine(directory, $"wall_{seed:D3}.json"), SceneJson());
            }

            Debug.Log($"Wrote {exportCount} walls to {directory}");
        }
#endif

        /// <summary>
        /// The wall as Scene JSON (docs/07). Hold.role becomes the route's start_hold_ids and
        /// top_hold_id -- docs/07 calls the JSON the canonical form from Phase 2 on, and role
        /// cannot express a hold that is a start on one route and a foot hold on another.
        /// </summary>
        string SceneJson()
        {
            var wall = Wall;
            var holds = new List<Hold>(wall.Holds);
            holds.Sort((a, b) => a.id.CompareTo(b.id));

            var invariant = CultureInfo.InvariantCulture;
            var ids = new List<string>();
            var starts = new List<string>();
            var top = "null";
            var entries = new List<string>();

            foreach (var hold in holds)
            {
                var id = hold.id.ToString(invariant);
                ids.Add(id);
                if (hold.role == HoldRole.Start) starts.Add(id);
                if (hold.role == HoldRole.Top) top = id;

                // The planner's renderer keys off these names, not off RGB.
                var color = hold.role == HoldRole.Start ? "green" : hold.role == HoldRole.Top ? "red" : "white";
                entries.Add($"    {{\"id\": {id}, \"position\": [{F(hold.wallPosition.x)}, {F(hold.wallPosition.y)}], "
                            + $"\"color\": \"{color}\"}}");
            }

            var json = new StringBuilder();
            json.Append("{\n  \"wall\": {\"coordinate_system\": \"wall_local_2d\", ");
            json.Append($"\"width\": {F(wall.width)}, \"height\": {F(wall.height)}}},\n");
            json.Append("  \"holds\": [\n");
            json.Append(string.Join(",\n", entries));
            json.Append("\n  ],\n  \"routes\": [\n");
            json.Append($"    {{\"id\": 0, \"hold_ids\": [{string.Join(", ", ids)}], ");
            json.Append($"\"start_hold_ids\": [{string.Join(", ", starts)}], \"top_hold_id\": {top}}}\n");
            json.Append("  ]\n}\n");
            return json.ToString();
        }

        // Fixed three decimals, invariant: these files are committed, so the same wall has to
        // produce the same bytes on every machine.
        static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A second pass down the same curve, dropped footDropY. The route is one line of holds, so
        /// hands and feet compete for it and the feet usually lose; this puts something under them
        /// without moving where the route goes.
        ///
        /// Deliberately after BridgeGaps. These holds sit off the hand line, so they are not links
        /// in the chain whose consecutive gaps that bounds -- bridging a hand hold to a foot hold
        /// that merely happens to sit between it and the next one would hang holds in mid-air.
        /// </summary>
        void AddFootHolds(ClimbingWall wall, SplineContainer container, List<(Vector2 position, HoldRole role)> route, int seed)
        {
            if (footDropY <= 0f)
            {
                return;
            }

            // Translating a curve does not change its shape or its arc length, so re-baking and
            // subtracting is the dropped spline. A fresh seed so the spacing and jitter are drawn
            // again rather than copying the hand line's rungs one drop lower.
            foreach (var wallPosition in BakeWallPositions(wall, container, seed + 1, footDropY))
            {
                // The drop runs the bottom of the curve into the floor.
                if (wallPosition.y < wall.holdRadius) continue;

                // Nothing closer than the route's own tightest spacing is a hold of its own. Checked
                // against what is already placed, so this pass does not crowd itself either.
                if (route.Exists(h => Vector2.Distance(h.position, wallPosition) < minHoldSpacing)) continue;

                route.Add((wallPosition, HoldRole.Normal));
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

        /// <summary>
        /// Two holds below the start line, always. The climber leaves the ground with both feet under
        /// the start hold, and src/vlm candidates.initial_pose takes the two lowest holds that are not
        /// starts -- with only one down there it stands a foot *above* the hands, which is what seed 10
        /// did. The spline bake leaves one whenever the spacing draws long, so this fills the gap.
        /// </summary>
        void EnsureFootHolds(ClimbingWall wall, List<(Vector2 position, HoldRole role)> route,
            System.Random rng)
        {
            while (route.FindAll(hold => hold.position.y < startHoldY - 1e-4f).Count < 2)
            {
                var lowest = route[0].position;
                foreach (var hold in route)
                {
                    if (hold.position.y < lowest.y) lowest = hold.position;
                }

                // Stepping down from the start line rather than from the lowest hold when that hold is
                // itself above the line: either way the new hold lands below it, so this terminates.
                var from = Mathf.Min(lowest.y, startHoldY);
                var gap = (float)(minHoldSpacing + rng.NextDouble() * (maxHoldSpacing - minHoldSpacing));
                var jitter = Mathf.Min(positionOffsetX, Mathf.Max(0f, (maxReach - maxHoldSpacing) * 0.5f));
                var x = lowest.x + (float)(rng.NextDouble() * 2.0 - 1.0) * jitter;
                var y = from - gap;
                if (y < footHoldFloorY)
                {
                    // No room underneath: the foot hold goes beside the lowest one instead of under it.
                    y = Mathf.Max(from, footHoldFloorY);
                    x = lowest.x + gap <= wall.width - sideMargin ? lowest.x + gap : lowest.x - gap;
                }

                route.Add((new Vector2(Mathf.Clamp(x, sideMargin, wall.width - sideMargin), y),
                    HoldRole.Normal));
            }
        }

        /// <summary>
        /// Where the route starts: one hold on the spline, or -- with twoStartHoldsChance -- a second
        /// one beside it at the same height, a spacing away. Two holds let the climber leave the
        /// ground with a hand on each; with one, both hands match on it
        /// (src/vlm candidates.initial_pose), and on a route that runs straight up that leaves the
        /// planner's no-crossing rule almost nothing to work with.
        /// </summary>
        List<Vector2> StartHolds(ClimbingWall wall, SplineContainer container, System.Random rng)
        {
            var first = new Vector2(SplineXAtHeight(container, startHoldY), startHoldY);
            var holds = new List<Vector2> { first };
            if (rng.NextDouble() >= twoStartHoldsChance) return holds;

            // A spacing, not a reach: the pair is one gap apart, so BridgeGaps has nothing to add
            // between them and the hands start a shoulder width apart rather than at full stretch.
            var gap = (float)(minHoldSpacing + rng.NextDouble() * (maxHoldSpacing - minHoldSpacing));
            var toTheRight = first.x + gap <= wall.width - sideMargin;
            holds.Add(new Vector2(toTheRight ? first.x + gap : first.x - gap, startHoldY));
            return holds;
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

        /// <summary>One bake, in wall-local 2D, with the whole pass moved down by dropY.</summary>
        List<Vector2> BakeWallPositions(ClimbingWall wall, SplineContainer container, int seed, float dropY)
        {
            var positions = new List<Vector2>();
            foreach (var worldPosition in BakeAlongSpline(wall, container, seed))
            {
                var local = wall.transform.InverseTransformPoint(worldPosition);
                positions.Add(new Vector2(local.x, local.y - dropY));
            }

            return positions;
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

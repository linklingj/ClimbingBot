using System.Collections.Generic;
using UnityEngine;
using ClimbingBot.Testing;
#if ODIN_INSPECTOR
using Sirenix.OdinInspector;
#endif

namespace ClimbingBot.Training
{
    /// <summary>
    /// Stage 1 episode setup (docs/05): the climber starts in one fixed stance with all four limbs
    /// grasping, and one limb is told to move to one reachable hold above or beside it. One move is
    /// the whole episode.
    ///
    /// This is the environment, not the agent. It decides what an episode looks like -- wall, start
    /// pose, which limb, which target -- and says whether the target was reached. Observations,
    /// rewards and actions belong to ClimbingAgent and are deliberately not here. The stance, the
    /// two-bone IK and the debug view are shared with Stage 2 and live in ClimbEnvironment.
    ///
    /// The stance comes first and the holds follow it. Picking four holds and solving for a pose
    /// that reaches all of them is a much harder problem than posing the body and putting a hold
    /// under each limb, and the wall is synthetic during Phase 1, so it costs nothing to let the
    /// body decide. That also makes the four-limb grasp succeed by construction rather than by
    /// landing inside graspRadius by luck.
    /// </summary>
    public class Stage1Environment : ClimbEnvironment
    {
        [Header("Wall")]
        [Tooltip("Stage 1 needs holds all around the climber, so the generator is the scattered one by type. A route wall would leave nothing to reach sideways.")]
        public ScatteredWallGenerator generator;

        [Header("Start stance (wall-local, m)")]
        [Tooltip("Where the hips sit on the wall. Fixed: the stance is meant to be the same every episode.")]
        public Vector2 hipsOnWall = new Vector2(2f, 2.1f);

        [Tooltip("How far the hips stand off the wall face.")]
        public float hipsDistanceFromWall = 0.26f;

        [Tooltip("Hand holds relative to the hips, on the wall face. Above the hips so hands end up above feet, but only to about shoulder height: an arm that starts fully extended overhead has nothing left to reach up to, which leaves the hands with no legal target. Measured -- see the worklog.")]
        public Vector2 handOffsetL = new Vector2(-0.34f, 0.42f);
        public Vector2 handOffsetR = new Vector2(0.34f, 0.42f);

        [Tooltip("Foot holds relative to the hips. Wider than minSeparation so the two foot holds stay distinguishable.")]
        public Vector2 footOffsetL = new Vector2(-0.26f, -0.58f);
        public Vector2 footOffsetR = new Vector2(0.26f, -0.58f);

        [Header("Target selection")]
        [Tooltip("A candidate below the limb by more than this is rejected: the move must go up or sideways.")]
        public float belowTolerance = 0.20f;

        [Tooltip("Candidates closer than this are rejected -- the move would be trivial.")]
        public float minStep = 0.30f;

        [Tooltip("A candidate counts as reachable if it is within this fraction of the limb chain's length, measured from the chain's root joint.")]
        [Range(0.3f, 1f)]
        public float reachFraction = 0.95f;

#if ODIN_INSPECTOR
        [Button("Reset episode (random seed)")]
#else
        [ContextMenu("Reset episode (random seed)")]
#endif
        public void ResetEpisodeWithRandomSeed()
        {
            ResetEpisode(Random.Range(int.MinValue, int.MaxValue));
        }

        /// <summary>
        /// Dresses the wall, plants the climber in the start stance and names the next target.
        /// Returns false if no reachable target existed, which leaves the stance standing -- the
        /// caller should retry with another seed.
        /// </summary>
        public override bool ResetEpisode(int seed)
        {
            // The episode that just ended is still standing here -- OnEpisodeBegin runs inside
            // EndEpisode, and PoseOnWall below is what lets go of the grips. So this is the one
            // place that can see a success after the fact, which is what keeps the whole debug
            // view inside Stage 1 instead of putting a hook in ClimbingAgent.
            if (IsTargetReached)
            {
                FlashSuccess();
            }

            var stance = StanceTargets();

            var anchors = new List<Vector2>();
            foreach (var limb in ClimberRagdoll.Limbs)
            {
                var local = wall.transform.InverseTransformPoint(stance[limb]);
                anchors.Add(new Vector2(local.x, local.y));
            }

            generator.Generate(seed, anchors);
            PoseOnWall(stance);

            if (!PickTarget(new System.Random(seed)))
            {
                return false;
            }

            return true;
        }

        /// <summary>World positions the four limb endpoints should occupy, all on the wall face.</summary>
        Dictionary<Limb, Vector3> StanceTargets()
        {
            // Holds sit a radius proud of the wall face, so that is where a limb has to be to grasp.
            var z = -wall.holdRadius;
            return new Dictionary<Limb, Vector3>
            {
                { Limb.LeftHand, wall.transform.TransformPoint(new Vector3(hipsOnWall.x + handOffsetL.x, hipsOnWall.y + handOffsetL.y, z)) },
                { Limb.RightHand, wall.transform.TransformPoint(new Vector3(hipsOnWall.x + handOffsetR.x, hipsOnWall.y + handOffsetR.y, z)) },
                { Limb.LeftFoot, wall.transform.TransformPoint(new Vector3(hipsOnWall.x + footOffsetL.x, hipsOnWall.y + footOffsetL.y, z)) },
                { Limb.RightFoot, wall.transform.TransformPoint(new Vector3(hipsOnWall.x + footOffsetR.x, hipsOnWall.y + footOffsetR.y, z)) }
            };
        }

        /// <summary>
        /// The generator put a hold on every stance anchor, so the hold nearest each stance point is
        /// exactly that point. PoseOn's return value is ignored on purpose: Stage 1's stance is
        /// reachable by construction and was measured at 150/150 four-limb grasps (docs/05).
        /// </summary>
        void PoseOnWall(Dictionary<Limb, Vector3> stance)
        {
            var holds = new Dictionary<Limb, Hold>();
            foreach (var limb in ClimberRagdoll.Limbs)
            {
                holds[limb] = NearestHold(stance[limb]);
            }

            var hips = wall.transform.TransformPoint(new Vector3(hipsOnWall.x, hipsOnWall.y, -hipsDistanceFromWall));
            PoseOn(hips, holds);
        }

        /// <summary>Root joint of the limb's chain -- the point its reach is measured from.</summary>
        Vector3 ChainRoot(Limb limb)
        {
            var root = Chain(limb)[0];
            return root.TransformPoint(root.GetComponent<ConfigurableJoint>().anchor);
        }

        /// <summary>
        /// How far the limb could reach from its root if fully extended: both bone lengths summed.
        /// Measuring the limb's *current* extension instead would underestimate it badly, because a
        /// climbing stance keeps every limb bent.
        /// </summary>
        float ChainReach(Limb limb)
        {
            var bones = Chain(limb);
            var elbow = bones[1].TransformPoint(bones[1].GetComponent<ConfigurableJoint>().anchor);
            return Vector3.Distance(ChainRoot(limb), elbow)
                + Vector3.Distance(elbow, ragdoll.LimbTransform(limb).position);
        }

        Hold NearestHold(Vector3 worldPosition)
        {
            Hold nearest = null;
            var nearestDistance = float.MaxValue;
            foreach (var hold in wall.Holds)
            {
                var distance = Vector3.Distance(worldPosition, hold.transform.position);
                if (distance >= nearestDistance)
                {
                    continue;
                }

                nearestDistance = distance;
                nearest = hold;
            }

            return nearest;
        }

        /// <summary>
        /// Picks a limb and a hold for it to move to: up or sideways, far enough to be a real move,
        /// and inside the limb's own reach. Tries the limbs in a random order so no limb is
        /// systematically favoured.
        /// </summary>
        bool PickTarget(System.Random rng)
        {
            var order = new List<Limb>(ClimberRagdoll.Limbs);
            for (var i = order.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                var swap = order[i];
                order[i] = order[j];
                order[j] = swap;
            }

            foreach (var limb in order)
            {
                var candidates = Candidates(limb);
                if (candidates.Count == 0)
                {
                    continue;
                }

                // Generate() paints every hold normalColor on the way in, so the target paint that
                // SetTarget applies needs no undo.
                SetTarget(limb, candidates[rng.Next(candidates.Count)]);
                return true;
            }

            SetTarget(Limb.LeftHand, null);
            return false;
        }

        List<Hold> Candidates(Limb limb)
        {
            var from = ragdoll.LimbTransform(limb).position;
            var root = ChainRoot(limb);
            var reach = ChainReach(limb) * reachFraction;

            var candidates = new List<Hold>();
            foreach (var hold in wall.Holds)
            {
                // Never target a hold somebody is already standing on, including this limb's own.
                if (IsGrasped(hold))
                {
                    continue;
                }

                var to = hold.transform.position;
                if (to.y < from.y - belowTolerance)
                {
                    continue;
                }

                if (Vector3.Distance(from, to) < minStep)
                {
                    continue;
                }

                if (Vector3.Distance(root, to) > reach)
                {
                    continue;
                }

                candidates.Add(hold);
            }

            return candidates;
        }

        bool IsGrasped(Hold hold)
        {
            foreach (var limb in ClimberRagdoll.Limbs)
            {
                if (ragdoll.GraspedHold(limb) == hold)
                {
                    return true;
                }
            }

            return false;
        }

        void OnGUI()
        {
            if (TargetHold == null)
            {
                return;
            }

            GUI.Label(new Rect(10, 70, 600, 20), "STAGE 1  move " + TargetLimb + " -> hold " + TargetHold.id
                + (IsTargetReached ? "   REACHED" : ""));
        }
    }
}

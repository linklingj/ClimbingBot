using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace ClimbingBot.Training
{
    /// <summary>
    /// The low-level climbing controller (docs/05). Given a commanded limb and one target hold, it
    /// drives joint targets and grasp/release until that limb is on that hold.
    ///
    /// Observations are expressed in the **wall's** frame. Walker needs an OrientationCube because a
    /// walking ragdoll's own frame swings around; a climber always faces one fixed wall, so the wall
    /// is already the stabilized reference and needs no proxy object.
    ///
    /// The episode -- wall layout, start stance, which limb, which hold -- belongs to
    /// Stage1Environment. This class only perceives, acts and scores.
    /// </summary>
    [RequireComponent(typeof(ClimberRagdoll))]
    public class ClimbingAgent : Agent
    {
        [Header("Environment")]
        public Stage1Environment env;

        [Header("Episode")]
        [Tooltip("Seed for the first episode. Episodes advance it, so a run is reproducible.")]
        public int episodeSeed;

        [Tooltip("How far the hips may fall below their start height before the episode is a failure (m).")]
        public float maxDrop = 1.0f;

        [Header("Reward weights")]
        [Tooltip("Per metre of progress the commanded limb makes toward its target. Potential-based: the total is bounded by the start distance, so it cannot be farmed by oscillating.")]
        public float progressReward = 2f;

        [Tooltip("Paid once when the commanded limb grasps its target hold.")]
        public float successReward = 1f;

        [Tooltip("Paid once when the climber falls.")]
        public float fallPenalty = -1f;

        [Tooltip("Per physics step, to prefer finishing sooner.")]
        public float timePenalty = 0.0005f;

        [Tooltip("Per physics step, for each support limb that is not grasping. Only bites when support limbs are unlocked.")]
        public float supportPenalty = 0.01f;

        [Tooltip("Per physics step, on the squared joint targets. Left at 0: an energy penalty early in training suppresses the exploration that finds the move at all. Turn it up if the learned motion looks twitchy.")]
        public float energyPenalty;

        [Header("Curriculum")]
        [Tooltip("Stage 1 holds the other three limbs on their holds -- docs/05 defines the stage as 'one limb moves, the others are fixed'. Their grasp branches are masked to 'no change'. Turn this off for Stage 2.")]
        public bool lockSupportLimbs = true;

        ClimberRagdoll m_Ragdoll;
        JointDriveController m_Jd;
        System.Random m_Rng;
        // Indexed by (int)Limb throughout, never by action-branch index. The two happen to agree
        // today; relying on that would break the moment a limb is added or reordered.
        readonly Hold[] m_Assigned = new Hold[4];
        float m_PrevDistance;
        float m_StartHipsY;
        float m_StartTime;
        bool m_Ready;

        public override void Initialize()
        {
            // Only cache components here. bodyPartsList is filled by ClimberRagdoll.Awake, and
            // component Awake/OnEnable order on one GameObject is not guaranteed, so anything that
            // reads it waits for OnEpisodeBegin.
            m_Ragdoll = GetComponent<ClimberRagdoll>();
            m_Rng = new System.Random(episodeSeed);

            if (env == null)
            {
                // Without this the first episode dies inside OnEpisodeBegin with a bare
                // NullReferenceException that says nothing about which field was left empty.
                Debug.LogError("ClimbingAgent.env is not set. Assign the Stage1Environment "
                    + "component (it lives on the ClimbingWall) in the inspector.", this);
                enabled = false;
            }
        }

        public override void OnEpisodeBegin()
        {
            m_Jd = m_Ragdoll.JdController;

            // A layout can come out with no reachable target. Redraw rather than run a dead episode.
            m_Ready = false;
            for (var attempt = 0; attempt < 8 && !m_Ready; attempt++)
            {
                m_Ready = env.ResetEpisode(m_Rng.Next());
            }

            if (!m_Ready)
            {
                Debug.LogWarning("Stage1Environment found no reachable target in 8 tries; check its filters.");
                return;
            }

            foreach (var limb in ClimberRagdoll.Limbs)
            {
                m_Assigned[(int)limb] = m_Ragdoll.GraspedHold(limb);
            }

            m_Assigned[(int)env.TargetLimb] = env.TargetHold;
            m_PrevDistance = TargetDistance();
            m_StartHipsY = m_Ragdoll.hips.position.y;
            m_StartTime = Time.fixedTime;
        }

        /// <summary>Distance from the commanded limb to the hold it has to reach.</summary>
        float TargetDistance()
        {
            return Vector3.Distance(m_Ragdoll.LimbTransform(env.TargetLimb).position,
                env.TargetHold.transform.position);
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            var frame = env.wall.transform;

            // Where the body is and which way it is pointing, both in the wall's frame. Without
            // these the climber cannot tell up from sideways.
            sensor.AddObservation(Quaternion.Inverse(frame.rotation) * m_Ragdoll.hips.rotation);
            sensor.AddObservation(Quaternion.Inverse(frame.rotation) * m_Ragdoll.chest.rotation);
            sensor.AddObservation(frame.InverseTransformDirection(AverageVelocity()));

            foreach (var limb in ClimberRagdoll.Limbs)
            {
                sensor.AddObservation(m_Ragdoll.IsGrasping(limb));
            }

            // One target slot per limb, always four, even though Stage 1 moves one. Keeping the shape
            // fixed is what lets Stage 3 reuse these weights (docs/05).
            foreach (var limb in ClimberRagdoll.Limbs)
            {
                var assigned = m_Assigned[(int)limb];
                var commanded = limb == env.TargetLimb;
                var offset = assigned == null
                    ? Vector3.zero
                    : assigned.transform.position - m_Ragdoll.LimbTransform(limb).position;
                sensor.AddObservation(frame.InverseTransformDirection(offset));
                sensor.AddObservation(commanded);
            }

            foreach (var bp in m_Jd.bodyPartsList)
            {
                CollectBodyPart(sensor, bp, frame);
            }
        }

        void CollectBodyPart(VectorSensor sensor, BodyPart bp, Transform frame)
        {
            sensor.AddObservation(bp.groundContact != null && bp.groundContact.touchingGround);
            sensor.AddObservation(frame.InverseTransformDirection(bp.rb.linearVelocity));
            sensor.AddObservation(frame.InverseTransformDirection(bp.rb.angularVelocity));
            sensor.AddObservation(frame.InverseTransformDirection(bp.rb.position - m_Ragdoll.hips.position));

            // Hips has no joint, and the welded wrists have one with every axis locked: their local
            // rotation never changes and no drive acts on them, so reporting either is a constant
            // input. Walker names the hands to skip them; testing the joint says the same thing
            // without hard-coding which body parts happen to be welded.
            if (bp.joint != null && !IsWelded(bp.joint))
            {
                sensor.AddObservation(bp.rb.transform.localRotation);
                sensor.AddObservation(bp.currentStrength / m_Jd.maxJointForceLimit);
            }
        }

        static bool IsWelded(ConfigurableJoint joint)
        {
            return joint.angularXMotion == ConfigurableJointMotion.Locked
                && joint.angularYMotion == ConfigurableJointMotion.Locked
                && joint.angularZMotion == ConfigurableJointMotion.Locked;
        }

        Vector3 AverageVelocity()
        {
            var sum = Vector3.zero;
            foreach (var bp in m_Jd.bodyPartsList)
            {
                sum += bp.rb.linearVelocity;
            }

            return sum / m_Jd.bodyPartsList.Count;
        }

        public override void WriteDiscreteActionMask(IDiscreteActionMask mask)
        {
            for (var branch = 0; branch < ClimberRagdoll.Limbs.Count; branch++)
            {
                var limb = ClimberRagdoll.Limbs[branch];

                // Stage 1: the support limbs stay put, so only "no change" is legal for them.
                if (lockSupportLimbs && limb != env.TargetLimb)
                {
                    mask.SetActionEnabled(branch, 1, false);
                    mask.SetActionEnabled(branch, 2, false);
                    continue;
                }

                // Grasping is only offered within reach of this limb's own hold -- the action never
                // picks a hold, so there is nothing to search and nothing to teleport to.
                mask.SetActionEnabled(branch, 1, m_Ragdoll.CanGrasp(limb, m_Assigned[(int)limb]));
                mask.SetActionEnabled(branch, 2, m_Ragdoll.IsGrasping(limb));
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (!m_Ready)
            {
                return;
            }

            var bp = m_Jd.bodyPartsDict;
            var c = actions.ContinuousActions;
            var i = -1;

            // Joint targets. Locked axes get 0 -- SetJointTargetRotation multiplies them by a zero
            // limit anyway, so spending an action on them would buy nothing. Elbow angZ (forearm
            // pronation) is open but deliberately undriven and held at neutral: measurement showed
            // it changes nothing while the grasp is a ball joint (docs/05).
            bp[m_Ragdoll.spine].SetJointTargetRotation(c[++i], c[++i], c[++i]);
            bp[m_Ragdoll.chest].SetJointTargetRotation(c[++i], c[++i], c[++i]);
            bp[m_Ragdoll.head].SetJointTargetRotation(c[++i], c[++i], 0f);

            // Shoulders take three: humeral axial rotation (angZ) is what makes an overhead reach
            // reachable at all. With it locked, a grid over the other two axes could not get the
            // hand above 0.28 m *below* the shoulder; unlocking it reaches 0.70 m above (docs/05).
            bp[m_Ragdoll.armL].SetJointTargetRotation(c[++i], c[++i], c[++i]);
            bp[m_Ragdoll.armR].SetJointTargetRotation(c[++i], c[++i], c[++i]);
            bp[m_Ragdoll.forearmL].SetJointTargetRotation(c[++i], 0f, 0f);
            bp[m_Ragdoll.forearmR].SetJointTargetRotation(c[++i], 0f, 0f);

            // Thighs take three: we opened femoral axial rotation for climbing (docs/05).
            bp[m_Ragdoll.thighL].SetJointTargetRotation(c[++i], c[++i], c[++i]);
            bp[m_Ragdoll.thighR].SetJointTargetRotation(c[++i], c[++i], c[++i]);
            bp[m_Ragdoll.shinL].SetJointTargetRotation(c[++i], 0f, 0f);
            bp[m_Ragdoll.shinR].SetJointTargetRotation(c[++i], 0f, 0f);
            bp[m_Ragdoll.footL].SetJointTargetRotation(c[++i], c[++i], c[++i]);
            bp[m_Ragdoll.footR].SetJointTargetRotation(c[++i], c[++i], c[++i]);

            bp[m_Ragdoll.spine].SetJointStrength(c[++i]);
            bp[m_Ragdoll.chest].SetJointStrength(c[++i]);
            bp[m_Ragdoll.head].SetJointStrength(c[++i]);
            bp[m_Ragdoll.armL].SetJointStrength(c[++i]);
            bp[m_Ragdoll.armR].SetJointStrength(c[++i]);
            bp[m_Ragdoll.forearmL].SetJointStrength(c[++i]);
            bp[m_Ragdoll.forearmR].SetJointStrength(c[++i]);
            bp[m_Ragdoll.thighL].SetJointStrength(c[++i]);
            bp[m_Ragdoll.thighR].SetJointStrength(c[++i]);
            bp[m_Ragdoll.shinL].SetJointStrength(c[++i]);
            bp[m_Ragdoll.shinR].SetJointStrength(c[++i]);
            bp[m_Ragdoll.footL].SetJointStrength(c[++i]);
            bp[m_Ragdoll.footR].SetJointStrength(c[++i]);

            var d = actions.DiscreteActions;
            for (var branch = 0; branch < ClimberRagdoll.Limbs.Count; branch++)
            {
                var limb = ClimberRagdoll.Limbs[branch];
                if (d[branch] == 1)
                {
                    m_Ragdoll.Grasp(limb, m_Assigned[(int)limb]);
                }
                else if (d[branch] == 2)
                {
                    m_Ragdoll.Release(limb);
                }
            }

            if (energyPenalty > 0f)
            {
                var sum = 0f;
                for (var a = 0; a < c.Length; a++)
                {
                    sum += c[a] * c[a];
                }

                AddReward(-energyPenalty * sum / c.Length);
            }
        }

        /// <summary>
        /// A no-op policy for running the scene without a trainer or a trained model: hold every
        /// joint at the middle of its range and change no grasp. Without this, ML-Agents logs
        /// "Heuristic method called but not implemented" every decision and the climber is driven by
        /// placeholder actions.
        ///
        /// This is NOT a hand-written controller and must not grow into one. Manual driving lives in
        /// ManualClimberControl, deliberately outside the agent, so the test scheme can never
        /// constrain the action space (docs/05).
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var c = actionsOut.ContinuousActions;
            for (var i = 0; i < c.Length; i++)
            {
                c[i] = 0f;
            }

            var d = actionsOut.DiscreteActions;
            for (var i = 0; i < d.Length; i++)
            {
                d[i] = 0;
            }
        }

        // Scored every physics step rather than every decision, so shaping tracks the motion instead
        // of sampling it once per decision period. Same place Walker scores.
        void FixedUpdate()
        {
            if (!m_Ready)
            {
                return;
            }

            var distance = TargetDistance();
            AddReward((m_PrevDistance - distance) * progressReward);
            m_PrevDistance = distance;
            AddReward(-timePenalty);

            if (supportPenalty > 0f)
            {
                foreach (var limb in ClimberRagdoll.Limbs)
                {
                    if (limb != env.TargetLimb && !m_Ragdoll.IsGrasping(limb))
                    {
                        AddReward(-supportPenalty);
                    }
                }
            }

            if (env.IsTargetReached)
            {
                AddReward(successReward);

                // Reach time for this limb, in simulated seconds. StatsRecorder averages over a
                // summary_freq window, so TensorBoard's ClearTime/<limb> is already the mean.
                // Successes only: a fall or a timeout has no reach time, and substituting MaxStep
                // would turn the mean into a success-rate proxy. Read it beside the fall rate.
                Academy.Instance.StatsRecorder.Add("ClearTime/" + env.TargetLimb,
                    Time.fixedTime - m_StartTime);

                EndEpisode();
                return;
            }

            if (m_Ragdoll.hips.position.y < m_StartHipsY - maxDrop)
            {
                AddReward(fallPenalty);
                EndEpisode();
            }
        }
    }
}

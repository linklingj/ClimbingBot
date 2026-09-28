using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ClimbingBot.Training
{
    /// <summary>
    /// What an episode looks like, whichever curriculum stage is running: which wall, which start
    /// stance, and which limb has to reach which hold next.
    ///
    /// <see cref="ClimbingAgent"/> talks to this and never to a stage. Observations, rewards and
    /// actions are its side of the line (docs/05), and the difference between "one move is the
    /// episode" (Stage 1) and "follow a planned sequence" (Stage 2) lives entirely in
    /// <see cref="AdvanceTarget"/>.
    /// </summary>
    public abstract class ClimbEnvironment : MonoBehaviour
    {
        [Header("Scene")]
        public ClimberRagdoll ragdoll;
        public ClimbingWall wall;

        [Header("Debug view")]
        [Tooltip("Colour of the hold the commanded limb has to reach. Nothing observes it -- this is for watching a run, not an input to the agent.")]
        public Color targetHoldColor = new Color(1f, 0.42f, 0.1f);

        [Tooltip("The wall flashes this colour when the commanded limb lands its target.")]
        public Color successFlashColor = new Color(0.25f, 0.85f, 0.35f);

        [Tooltip("How long the success flash lasts, in real seconds. 0 turns it off.")]
        public float successFlashSeconds = 0.5f;

        public Limb TargetLimb { get; private set; }
        public Hold TargetHold { get; private set; }

        /// <summary>True once the commanded limb is grasping the hold it was told to move to.</summary>
        public bool IsTargetReached => TargetHold != null && ragdoll.GraspedHold(TargetLimb) == TargetHold;

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");

        Renderer m_Slab;
        MaterialPropertyBlock m_Block;
        Coroutine m_Flash;

        /// <summary>
        /// Dresses the wall, plants the climber and names the first target. Returns false if the
        /// episode could not be set up, which leaves whatever was standing in place -- the caller
        /// should retry with another seed.
        /// </summary>
        public abstract bool ResetEpisode(int seed);

        /// <summary>
        /// Called when the commanded limb lands its target. True means another target was set and
        /// the episode continues; false ends it. Stage 1 is one move per episode, so false.
        /// </summary>
        public virtual bool AdvanceTarget()
        {
            return false;
        }

        /// <summary>
        /// The hold each limb is meant to be on during this move -- the target for the commanded
        /// limb, and wherever it already is for the others. The agent observes these four and
        /// grasps by them, so overriding this is how a stage says "this limb belongs there" for a
        /// limb that is not the one moving.
        /// </summary>
        public virtual Hold AssignedHold(Limb limb)
        {
            return limb == TargetLimb ? TargetHold : ragdoll.GraspedHold(limb);
        }

        /// <summary>Names the next move and paints its hold. Colour is debug view only.</summary>
        protected void SetTarget(Limb limb, Hold hold)
        {
            TargetLimb = limb;
            TargetHold = hold;
            if (hold != null)
            {
                hold.color = targetHoldColor;
                hold.ApplyColor();
            }
        }

        /// <summary>
        /// Plants the climber with each limb on its hold and the hips at <paramref name="hipsWorld"/>.
        /// False if any limb ended up out of grasp range, which means the stance is not reachable and
        /// the episode should not run.
        /// </summary>
        protected bool PoseOn(Vector3 hipsWorld, Dictionary<Limb, Hold> holds)
        {
            ragdoll.ResetBody();
            ragdoll.transform.position += hipsWorld - ragdoll.hips.position;

            foreach (var limb in ClimberRagdoll.Limbs)
            {
                Reach(limb, holds[limb].transform.position);
            }

            foreach (var bp in ragdoll.JdController.bodyPartsList)
            {
                bp.rb.linearVelocity = Vector3.zero;
                bp.rb.angularVelocity = Vector3.zero;
            }

            Physics.SyncTransforms();

            var posed = true;
            foreach (var limb in ClimberRagdoll.Limbs)
            {
                posed &= ragdoll.Grasp(limb, holds[limb]);
            }

            return posed;
        }

        /// <summary>
        /// Cyclic coordinate descent on the limb's two bones. Cheaper to write than analytic two-link
        /// IK and it does not care how the bones are oriented, which matters because the authored
        /// pose is a T and every joint axis points somewhere different.
        ///
        /// ponytail: ignores joint limits. Stage 1's authored offsets are anatomically ordinary so
        /// the solution lands inside them; a stance taken from a plan may not, and then physics
        /// resolves it on the first step and the limb drifts off its hold. PoseOn's return value is
        /// what catches that.
        /// </summary>
        protected void Reach(Limb limb, Vector3 target)
        {
            var endpoint = ragdoll.LimbTransform(limb);
            var bones = Chain(limb);

            for (var iteration = 0; iteration < 12; iteration++)
            {
                for (var i = bones.Length - 1; i >= 0; i--)
                {
                    var joint = bones[i].GetComponent<ConfigurableJoint>();
                    if (joint == null)
                    {
                        continue;
                    }

                    var pivot = bones[i].TransformPoint(joint.anchor);
                    var from = endpoint.position - pivot;
                    var to = target - pivot;
                    if (from.sqrMagnitude < 1e-8f || to.sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }

                    Quaternion.FromToRotation(from.normalized, to.normalized).ToAngleAxis(out var angle, out var axis);
                    bones[i].RotateAround(pivot, axis, angle);
                }
            }
        }

        protected Transform[] Chain(Limb limb)
        {
            switch (limb)
            {
                case Limb.LeftHand: return new[] { ragdoll.armL, ragdoll.forearmL };
                case Limb.RightHand: return new[] { ragdoll.armR, ragdoll.forearmR };
                case Limb.LeftFoot: return new[] { ragdoll.thighL, ragdoll.shinL };
                default: return new[] { ragdoll.thighR, ragdoll.shinR };
            }
        }

        /// <summary>
        /// Tints the wall slab for a moment so a success is visible while watching a run. Uses a
        /// MaterialPropertyBlock rather than Renderer.material: the 16 training areas share one
        /// material and .material would instantiate a copy per area.
        /// </summary>
        protected void FlashSuccess()
        {
            if (successFlashSeconds <= 0f)
            {
                return;
            }

            if (m_Slab == null)
            {
                var slab = wall.transform.Find("Slab");
                m_Slab = slab == null ? null : slab.GetComponent<Renderer>();
                if (m_Slab == null)
                {
                    return;
                }
            }

            if (m_Flash != null)
            {
                StopCoroutine(m_Flash);
            }

            m_Flash = StartCoroutine(Flash());
        }

        IEnumerator Flash()
        {
            if (m_Block == null)
            {
                m_Block = new MaterialPropertyBlock();
            }

            m_Slab.GetPropertyBlock(m_Block);
            m_Block.SetColor(k_BaseColor, successFlashColor);
            m_Slab.SetPropertyBlock(m_Block);

            // Realtime, not scaled: training runs at time_scale 20, where half a second of game
            // time is 25 ms and the flash would be over before a frame draws it.
            yield return new WaitForSecondsRealtime(successFlashSeconds);

            // null clears the override, so the slab goes back to the material's own colour.
            m_Slab.SetPropertyBlock(null);
            m_Flash = null;
        }
    }
}

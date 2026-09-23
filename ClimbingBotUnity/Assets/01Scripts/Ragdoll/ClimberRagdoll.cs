using System.Collections.Generic;
using UnityEngine;

namespace ClimbingBot
{
    public enum Limb
    {
        LeftHand,
        RightHand,
        LeftFoot,
        RightFoot
    }

    [RequireComponent(typeof(JointDriveController))]
    public class ClimberRagdoll : MonoBehaviour
    {
        [Header("Torso")]
        public Transform hips;
        public Transform spine;
        public Transform chest;
        public Transform head;

        [Header("Arms")]
        public Transform armL;
        public Transform forearmL;
        public Transform handL;
        public Transform armR;
        public Transform forearmR;
        public Transform handR;

        [Header("Legs")]
        public Transform thighL;
        public Transform shinL;
        public Transform footL;
        public Transform thighR;
        public Transform shinR;
        public Transform footR;

        [Header("Grasp")]
        [Tooltip("Max limb-to-hold distance at which a grasp is allowed (m). Tune against hold size.")]
        public float graspRadius = 0.2f;

        public JointDriveController JdController { get; private set; }

        readonly Dictionary<Limb, FixedJoint> m_Grasps = new Dictionary<Limb, FixedJoint>();
        static readonly Limb[] k_Limbs = { Limb.LeftHand, Limb.RightHand, Limb.LeftFoot, Limb.RightFoot };

        void Awake()
        {
            JdController = GetComponent<JointDriveController>();
            foreach (var t in BodyParts())
            {
                JdController.SetupBodyPart(t);
            }
        }

        public IEnumerable<Transform> BodyParts()
        {
            yield return hips;
            yield return spine;
            yield return chest;
            yield return head;
            yield return armL;
            yield return forearmL;
            yield return handL;
            yield return armR;
            yield return forearmR;
            yield return handR;
            yield return thighL;
            yield return shinL;
            yield return footL;
            yield return thighR;
            yield return shinR;
            yield return footR;
        }

        public Transform LimbTransform(Limb limb)
        {
            switch (limb)
            {
                case Limb.LeftHand: return handL;
                case Limb.RightHand: return handR;
                case Limb.LeftFoot: return footL;
                default: return footR;
            }
        }

        public bool IsGrasping(Limb limb)
        {
            return m_Grasps.ContainsKey(limb);
        }

        public bool CanGrasp(Limb limb, Vector3 holdPosition)
        {
            return !IsGrasping(limb) &&
                Vector3.Distance(LimbTransform(limb).position, holdPosition) <= graspRadius;
        }

        /// <summary>Pins the limb where it currently is. Returns false if out of range or already grasping.</summary>
        public bool Grasp(Limb limb, Vector3 holdPosition)
        {
            if (!CanGrasp(limb, holdPosition))
            {
                return false;
            }

            var joint = LimbTransform(limb).gameObject.AddComponent<FixedJoint>();
            // Holds are static geometry, so anchor to the world rather than to a hold Rigidbody.
            joint.connectedBody = null;
            m_Grasps[limb] = joint;
            return true;
        }

        public void Release(Limb limb)
        {
            if (!m_Grasps.TryGetValue(limb, out var joint))
            {
                return;
            }

            // Immediate, not deferred: a grasp surviving into the next physics step would fight an episode reset.
            DestroyImmediate(joint);
            m_Grasps.Remove(limb);
        }

        public void ResetBody()
        {
            foreach (var limb in k_Limbs)
            {
                Release(limb);
            }

            foreach (var bp in JdController.bodyPartsDict.Values)
            {
                bp.Reset(bp);
            }
        }
    }
}

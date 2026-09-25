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

        struct Grip
        {
            public ConfigurableJoint joint;
            public Hold hold;
        }

        readonly Dictionary<Limb, Grip> m_Grips = new Dictionary<Limb, Grip>();
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
            return m_Grips.ContainsKey(limb);
        }

        public Hold GraspedHold(Limb limb)
        {
            return m_Grips.TryGetValue(limb, out var grip) ? grip.hold : null;
        }

        /// <summary>Route is cleared when both hands are on the top hold.</summary>
        public bool IsToppedOut => IsOnTop(Limb.LeftHand) && IsOnTop(Limb.RightHand);

        bool IsOnTop(Limb limb)
        {
            var hold = GraspedHold(limb);
            return hold != null && hold.role == HoldRole.Top;
        }

        public bool CanGrasp(Limb limb, Hold hold)
        {
            return hold != null && !IsGrasping(limb) &&
                Vector3.Distance(LimbTransform(limb).position, hold.transform.position) <= graspRadius;
        }

        /// <summary>Pins the limb where it currently is. Returns false if out of range or already grasping.</summary>
        public bool Grasp(Limb limb, Hold hold)
        {
            if (!CanGrasp(limb, hold))
            {
                return false;
            }

            var joint = LimbTransform(limb).gameObject.AddComponent<ConfigurableJoint>();
            // Holds are static geometry, so anchor to the world rather than to a hold Rigidbody.
            joint.connectedBody = null;
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;

            // Position only, per the grasp abstraction: a hand on a hold pivots, it is not welded.
            // Welding orientation instead leaves the arm chain with no axial freedom at all
            // (shoulder and elbow twist are both locked, the wrist is rigid), so the body could not
            // turn about the arm -- which is most of climbing.
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;

            m_Grips[limb] = new Grip { joint = joint, hold = hold };
            return true;
        }

        public void Release(Limb limb)
        {
            if (!m_Grips.TryGetValue(limb, out var grip))
            {
                return;
            }

            // Immediate, not deferred: a grasp surviving into the next physics step would fight an episode reset.
            DestroyImmediate(grip.joint);
            m_Grips.Remove(limb);
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

        /// <summary>Reset into the start of a route: hanging with both hands on the given hold.</summary>
        public void ResetOnHold(Hold hold)
        {
            ResetBody();
            if (hold == null)
            {
                return;
            }

            var shoulderL = ShoulderOf(armL);
            var shoulderR = ShoulderOf(armR);
            var shoulderMid = (shoulderL + shoulderR) * 0.5f;
            var armLength = Vector3.Distance(shoulderL, handL.position);
            var halfShoulders = Vector3.Distance(shoulderL, shoulderR) * 0.5f;

            // Both hands reach one hold only when each shoulder is an arm's length from it. The
            // shoulder line is perpendicular to the offset below, so the midpoint sits this far out.
            var reach = Mathf.Sqrt(Mathf.Max(0.01f, armLength * armLength - halfShoulders * halfShoulders));

            var lowest = float.MaxValue;
            foreach (var bp in JdController.bodyPartsList)
            {
                lowest = Mathf.Min(lowest, bp.rb.position.y);
            }

            // Hang under a hold that is out of standing reach; otherwise stand back from it, so a
            // low start hold does not bury the feet in the floor.
            var rise = hold.transform.position.y - (shoulderMid.y - lowest);
            var offset = rise >= reach
                ? Vector3.down * reach
                : new Vector3(0f, -rise, -Mathf.Sqrt(Mathf.Max(0f, reach * reach - rise * rise)));

            transform.position += hold.transform.position + offset - shoulderMid;

            AimArmAt(armL, handL, hold.transform.position);
            AimArmAt(armR, handR, hold.transform.position);

            foreach (var bp in JdController.bodyPartsList)
            {
                bp.rb.linearVelocity = Vector3.zero;
                bp.rb.angularVelocity = Vector3.zero;
            }

            Physics.SyncTransforms();
            Grasp(Limb.LeftHand, hold);
            Grasp(Limb.RightHand, hold);
        }

        static Vector3 ShoulderOf(Transform upperArm)
        {
            return upperArm.TransformPoint(upperArm.GetComponent<ConfigurableJoint>().anchor);
        }

        static void AimArmAt(Transform upperArm, Transform hand, Vector3 target)
        {
            var shoulder = ShoulderOf(upperArm);
            var from = hand.position - shoulder;
            var to = target - shoulder;
            if (from.sqrMagnitude < 1e-6f || to.sqrMagnitude < 1e-6f)
            {
                return;
            }

            Quaternion.FromToRotation(from.normalized, to.normalized).ToAngleAxis(out var angle, out var axis);
            upperArm.RotateAround(shoulder, axis, angle);
        }
    }
}

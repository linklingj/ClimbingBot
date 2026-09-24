using UnityEngine;
using UnityEngine.InputSystem;

namespace ClimbingBot.Testing
{
    /// <summary>
    /// Manual keyboard/mouse driving of the ragdoll, for eyeballing the wall, the holds and the
    /// grasp abstraction before a controller exists.
    ///
    /// This is NOT the agent's action space and must not become it. It pulls a limb around with a
    /// raw force, which is nothing like the joint targets ClimbingAgent will output. Disable this
    /// component once an Agent drives the same ragdoll, or the two will fight each other.
    ///
    /// Q/W/A/S select a limb, the mouse aims it, left click grasps, right click releases,
    /// R resets the body.
    /// </summary>
    public class ManualClimberControl : MonoBehaviour
    {
        public ClimberRagdoll ragdoll;
        public ClimbingWall wall;
        public Camera view;

        [Tooltip("Force used to drag the selected limb toward the cursor. Test feel only.")]
        public float reachForce = 400f;

        public Limb Selected { get; set; } = Limb.LeftHand;

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.qKey.wasPressedThisFrame) Selected = Limb.LeftHand;
                if (keyboard.wKey.wasPressedThisFrame) Selected = Limb.RightHand;
                if (keyboard.aKey.wasPressedThisFrame) Selected = Limb.LeftFoot;
                if (keyboard.sKey.wasPressedThisFrame) Selected = Limb.RightFoot;
                if (keyboard.rKey.wasPressedThisFrame) ragdoll.ResetBody();
            }

            var mouse = Mouse.current;
            if (mouse == null) return;
            if (mouse.leftButton.wasPressedThisFrame) GraspNearestHold();
            if (mouse.rightButton.wasPressedThisFrame) ragdoll.Release(Selected);
        }

        void FixedUpdate()
        {
            if (ragdoll.IsGrasping(Selected)) return;
            if (Mouse.current == null) return;
            if (!TryGetReachTarget(Mouse.current.position.ReadValue(), out var target)) return;

            var limb = ragdoll.LimbTransform(Selected);
            var toTarget = target - limb.position;
            if (toTarget.sqrMagnitude < 1e-6f) return;

            limb.GetComponent<Rigidbody>().AddForce(toTarget.normalized * reachForce);
        }

        public void GraspNearestHold()
        {
            var limbPosition = ragdoll.LimbTransform(Selected).position;
            Hold nearest = null;
            var nearestDistance = float.MaxValue;

            foreach (var hold in wall.Holds)
            {
                var distance = Vector3.Distance(limbPosition, hold.transform.position);
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = hold;
            }

            ragdoll.Grasp(Selected, nearest);
        }

        // The cursor aims on a plane parallel to the wall through the limb, so screen position maps
        // to a reach target at the limb's own depth.
        public bool TryGetReachTarget(Vector2 screenPosition, out Vector3 target)
        {
            target = default;
            if (view == null) return false;

            var plane = new Plane(Vector3.forward, ragdoll.LimbTransform(Selected).position);
            var ray = view.ScreenPointToRay(screenPosition);
            if (!plane.Raycast(ray, out var distance)) return false;

            target = ray.GetPoint(distance);
            return true;
        }

        void OnGUI()
        {
            var grasped = ragdoll.GraspedHold(Selected);
            GUI.Label(new Rect(10, 10, 600, 20), "Q/W/A/S limb   mouse aim   LMB grasp   RMB release   R reset");
            GUI.Label(new Rect(10, 30, 600, 20), "selected: " + Selected
                + (grasped != null ? "   holding hold " + grasped.id + " (" + grasped.role + ")" : "   free"));
            if (ragdoll.IsToppedOut) GUI.Label(new Rect(10, 50, 600, 20), "TOPPED OUT");
        }
    }
}

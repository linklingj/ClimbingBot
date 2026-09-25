using UnityEngine;

namespace ClimbingBot.Testing
{
    /// <summary>
    /// On-screen bars for per-limb grip load and the combined stability score, for eyeballing how
    /// hard a posture is while driving the ragdoll by hand.
    ///
    /// Kept apart from ManualClimberControl on purpose: that component gets disabled once an Agent
    /// drives the ragdoll, and this readout is wanted exactly then.
    /// </summary>
    public class GripLoadHud : MonoBehaviour
    {
        public ClimberRagdoll ragdoll;

        [Tooltip("Top-left corner of the panel, in pixels. Sits below ManualClimberControl's labels.")]
        public Vector2 origin = new Vector2(10f, 80f);

        [Tooltip("Display smoothing (s). The raw per-step solver force is too noisy to read.")]
        public float smoothing = 0.15f;

        [Tooltip("Load that fills a bar, in bodyweights.")]
        public float barMax = 3f;

        readonly float[] m_Shown = new float[4];

        // Sampled on the physics tick, not in OnGUI: OnGUI runs several times per frame (layout and
        // repaint), so smoothing there would advance at the whim of the render rate.
        void FixedUpdate()
        {
            if (ragdoll == null)
            {
                return;
            }

            var k = smoothing > 0f ? 1f - Mathf.Exp(-Time.fixedDeltaTime / smoothing) : 1f;
            foreach (var limb in ClimberRagdoll.Limbs)
            {
                m_Shown[(int)limb] = Mathf.Lerp(m_Shown[(int)limb], ragdoll.GripLoad(limb), k);
            }
        }

        void OnGUI()
        {
            if (ragdoll == null)
            {
                return;
            }

            // IMGUI lays out in screen pixels, so the panel shrinks to nothing on a 4K game view.
            var matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * Mathf.Max(1f, Screen.height / 1080f));

            var y = origin.y;
            var total = 0f;
            var worst = 0f;

            foreach (var limb in ClimberRagdoll.Limbs)
            {
                var load = m_Shown[(int)limb];
                total += load;
                var grasping = ragdoll.IsGrasping(limb);
                var ratio = load / Mathf.Max(1e-4f, ragdoll.Capacity(limb));
                if (grasping)
                {
                    worst = Mathf.Max(worst, ratio);
                }
                Bar(y, limb.ToString(), load / barMax,
                    grasping ? Color.Lerp(Color.green, Color.red, ratio) : Color.gray,
                    grasping ? load.ToString("0.00") + " BW" : "free");
                y += 18f;
            }

            // Total is at least 1.00 BW while hanging still; the excess is internal load, limbs
            // pulling against each other. With no controller driving the joints the slerpDrives pull
            // toward the T-pose, which alone reads 2-4 BW total, so expect a low score until
            // ClimbingAgent holds the joints.
            y += 6f;
            Bar(y, "total", total / barMax, Color.cyan, total.ToString("0.00") + " BW");

            y += 18f;
            // The score floors at 0, so the worst grip's ratio rides along -- that is where the
            // resolution is once a limb is over capacity.
            var score = ragdoll.StabilityScore;
            Bar(y, "score", score, Color.Lerp(Color.red, Color.green, score),
                score.ToString("0.00") + "  worst " + worst.ToString("0.00") + "x");

            GUI.matrix = matrix;
        }

        void Bar(float y, string label, float fill, Color color, string value)
        {
            GUI.Label(new Rect(origin.x, y, 70f, 18f), label);

            var track = new Rect(origin.x + 70f, y + 4f, 160f, 10f);
            var previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.35f);
            GUI.DrawTexture(track, Texture2D.whiteTexture);
            GUI.color = color;
            GUI.DrawTexture(new Rect(track.x, track.y, track.width * Mathf.Clamp01(fill), track.height),
                Texture2D.whiteTexture);
            GUI.color = previous;

            GUI.Label(new Rect(track.xMax + 6f, y, 140f, 18f), value);
        }
    }
}

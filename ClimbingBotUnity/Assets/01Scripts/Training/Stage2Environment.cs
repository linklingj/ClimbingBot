using System;
using System.Collections.Generic;
using System.IO;
using Unity.MLAgents;
using UnityEngine;

namespace ClimbingBot.Training
{
    /// <summary>
    /// Stage 2 episode setup (docs/05): the climber follows a move sequence the VLM planner already
    /// produced, one move at a time, on the wall that plan was made for. One episode is one whole
    /// route -- the target advances as each move lands, and the episode only ends on a fall or a
    /// move that runs out of time.
    ///
    /// **No wall generator and no random seed.** The walls and the plans are files on disk
    /// (`walls/wall_NNN.json` == the plan run's `scene.json`, and `plan.json` beside it), so a
    /// training run climbs exactly the routes that were evaluated offline and nothing calls a model
    /// API while training. Areas round-robin through the sequences from their own
    /// <see cref="firstSequence"/> so the 16 training areas are not all on the same wall at once.
    /// </summary>
    public class Stage2Environment : ClimbEnvironment
    {
        [Header("Sequences")]
        [Tooltip("Directory prefix of the planner run, relative to the repo root. The sequence index is appended, so out/test5-seed is out/test5-seed0 .. out/test5-seed49.")]
        public string sequencePrefix = "out/test5-seed";

        [Tooltip("How many sequences exist. Episodes cycle 0..count-1. Sequences whose plan never topped out are skipped, so the usable count can be lower (test5: 48 of 50).")]
        public int sequenceCount = 50;

        [Tooltip("Which sequence this area starts on. Set it differently per area (0..15, like ClimbingAgent.episodeSeed) or every area climbs the same wall at the same time and the samples are correlated.")]
        public int firstSequence;

        [Tooltip("Skip plans that never reached the top hold. On means Route/Completed reads as a top-out rate; off means it reads as 'finished whatever the planner managed'.")]
        public bool requireReachedTop = true;

        [Tooltip("Chance that an episode starts at a move drawn uniformly from the whole route instead of at move 0. Episodes always die a few moves in, so starting only at the bottom means the upper two thirds of every route are never seen (stage2-02: 20M steps, mean 3.8 of 23.9 moves, Route/Completed 0). 0 climbs from the bottom every time, which is what an inference scene wants.")]
        [Range(0f, 1f)]
        public float randomStartMoveChance = 0.75f;

        [Header("Start stance")]
        [Tooltip("How far the hips stand off the wall face. Same as Stage 1's stance.")]
        public float hipsDistanceFromWall = 0.26f;

        [Tooltip("Where the hips sit between the feet (0) and the hands (1). 0.58 reproduces Stage 1's measured stance, which puts the hips 0.42 m under the hands and 0.58 m over the feet across a 1 m span. Calibration knob: the planner's stances are not that span, and a stance the two-bone IK cannot fold into is what makes a start pose fail.")]
        [Range(0f, 1f)]
        public float hipsBias = 0.58f;

        [Header("Debug view")]
        [Tooltip("Draw the route readout. Off by default: a training scene has 16 areas and they would all draw into the same corner. Inference.unity turns it on.")]
        public bool showHud;

        [Header("Colors")]
        public Color normalColor = new Color(0.85f, 0.85f, 0.87f);
        public Color startColor = new Color(0.35f, 0.75f, 0.35f);
        public Color topColor = new Color(0.9f, 0.3f, 0.25f);

        /// <summary>One planner run for one wall: the wall it was planned on and the moves.</summary>
        class Sequence
        {
            public SceneJson scene;
            public PlanJson plan;
        }

        // Parsed once per sequence for the whole editor session, not once per area per episode: 16
        // areas cycling 20 sequences would otherwise re-read and re-parse 320 file pairs every
        // couple of minutes. A null entry is a sequence that failed to load or validate -- kept so
        // the failure is logged once rather than every episode.
        static readonly Dictionary<string, Sequence> k_Cache = new Dictionary<string, Sequence>();

        // Hips placements to fall back to. The plan's mid-route stances are wider and more asymmetric
        // than Stage 1's authored one, and one hips point leaves 28% of them outside the two-bone IK's
        // reach (measured: 180 of 248 moves over 10 test5 routes posed at hipsBias alone, 243 with
        // these). Sitting lower is what rescues them -- 0.70 and 0.82 never once helped.
        static readonly float[] k_HipsBiasFallbacks = { 0.45f, 0.33f };

        readonly Dictionary<int, Hold> m_HoldsById = new Dictionary<int, Hold>();
        Sequence m_Sequence;
        int m_Index;
        int m_Current = -1;
        int m_Move;
        int m_StartMove;
        bool m_Completed;
        int m_Attempts;
        int m_Completions;
        GUIStyle m_HudStyle;

        void Awake()
        {
            m_Index = firstSequence;
        }

        public override bool ResetEpisode(int seed)
        {
            // The route that just ended is still standing here (OnEpisodeBegin runs inside
            // EndEpisode), so this is the one place that can score it and the one place that can see
            // the last move land.
            if (IsTargetReached)
            {
                FlashSuccess();
            }

            RecordRoute();

            // The seed is ignored on purpose -- the walls are the 20 planned ones, not a draw.
            //
            // Walking the list here rather than leaning on the agent's eight retries: a plan that is
            // missing (a planner run still being generated) or one the planner never finished is a
            // permanent hole in the list, and whether a fixed number of retries happens to straddle
            // the hole is not something to leave to luck -- it leaves the agent sitting out the run.
            var count = Mathf.Max(1, sequenceCount);
            for (var attempt = 0; attempt < count; attempt++)
            {
                var index = m_Index;
                m_Index = (m_Index + 1) % count;

                var sequence = Load(index);
                if (sequence == null)
                {
                    continue;
                }

                Dress(sequence);

                var startMove = PickStartMove(sequence.plan.moves.Length);
                var posed = PoseStance(sequence, startMove);
                if (!posed && startMove != 0)
                {
                    // One mid-route stance the IK cannot fold into says nothing about the rest of the
                    // route, so fall back to the bottom rather than throwing the sequence away.
                    startMove = 0;
                    posed = PoseStance(sequence, 0);
                }

                if (!posed)
                {
                    // Move 0 is deterministic -- the stance is posed from a T every time -- so drop it
                    // from the list instead of failing on it again every time it comes round.
                    Debug.LogWarning($"Stage2Environment: start stance of {SequencePath(index)} is out of "
                        + "reach, dropping it. Look at hipsBias, or at the plan's first pose.", this);
                    k_Cache[SequencePath(index)] = null;
                    continue;
                }

                m_Sequence = sequence;
                m_Current = index;
                m_Move = startMove;
                m_StartMove = startMove;
                m_Completed = false;
                SetMoveTarget();
                return true;
            }

            Debug.LogError($"Stage2Environment: none of the {count} sequences at {sequencePrefix}N "
                + "could be used. Has the planner run finished writing them?", this);
            return false;
        }

        /// <summary>The next move in the plan, or false when the route is finished.</summary>
        public override bool AdvanceTarget()
        {
            m_Move++;
            if (m_Move >= m_Sequence.plan.moves.Length)
            {
                m_Completed = true;
                return false;
            }

            // Back to its role colour, or the whole route ends up orange behind the climber.
            TargetHold.color = ColorFor(TargetHold.role);
            TargetHold.ApplyColor();
            SetMoveTarget();
            return true;
        }

        /// <summary>
        /// The plan says where all four limbs belong, so the support limbs keep their assignment even
        /// if one has let go. The base class reads the grasp instead, which in Stage 2 would leave a
        /// limb that slipped with no hold to grasp for the rest of the route.
        /// </summary>
        public override Hold AssignedHold(Limb limb)
        {
            if (m_Sequence == null)
            {
                return base.AssignedHold(limb);
            }

            var move = m_Sequence.plan.moves[Mathf.Min(m_Move, m_Sequence.plan.moves.Length - 1)];
            return limb == TargetLimb ? TargetHold : HoldOf(move.pose.HoldId(limb));
        }

        /// <summary>
        /// Which move of the plan this episode starts on. Uniform over the route, so the moves near
        /// the top get sampled as often as the ones at the bottom -- which is the point, since
        /// reaching them by climbing is what the policy cannot do yet.
        /// </summary>
        int PickStartMove(int moves)
        {
            return UnityEngine.Random.value < randomStartMoveChance ? UnityEngine.Random.Range(0, moves) : 0;
        }

        /// <summary>
        /// Plants the climber in the stance the plan has before <paramref name="move"/>, trying the
        /// lower hips placements if the first one does not fold.
        /// </summary>
        bool PoseStance(Sequence sequence, int move)
        {
            var stance = StanceBefore(sequence.plan.moves[move]);
            if (PoseOn(StartHips(stance, hipsBias), stance))
            {
                return true;
            }

            foreach (var bias in k_HipsBiasFallbacks)
            {
                if (PoseOn(StartHips(stance, bias), stance))
                {
                    return true;
                }
            }

            return false;
        }

        void SetMoveTarget()
        {
            var move = m_Sequence.plan.moves[m_Move];
            SetTarget(LimbOf(move.moving_limb), HoldOf(move.target_hold_id));
        }

        /// <summary>
        /// Closes out one route in TensorBoard. Route/Completed averaged over a summary_freq window
        /// is the top-out rate (완등률); Route/Progress is how far the failed attempts got, which is
        /// what moves first while the rate is still near zero.
        /// </summary>
        void RecordRoute()
        {
            if (m_Sequence == null)
            {
                return;
            }

            var stats = Academy.Instance.StatsRecorder;

            // Moves landed, which is the one number every episode can report: the route it was handed
            // is 24 moves long whether it started at the bottom or halfway up.
            stats.Add("Route/Moves", m_Move - m_StartMove);

            // Completion and progress are only about *the whole route*, so only an episode that
            // started at move 0 gets to vote. Letting a mid-route start count would read as a top-out
            // rate that went up because the episodes got shorter.
            if (m_StartMove == 0)
            {
                stats.Add("Route/Completed", m_Completed ? 1f : 0f);
                stats.Add("Route/Progress", m_Move / (float)m_Sequence.plan.moves.Length);
                m_Attempts++;
                if (m_Completed)
                {
                    m_Completions++;
                }
            }

            m_Sequence = null;
        }

        /// <summary>Rebuilds the wall from the sequence's scene JSON, ids and roles included.</summary>
        void Dress(Sequence sequence)
        {
            var route = sequence.scene.routes[0];
            wall.ClearHolds();
            m_HoldsById.Clear();

            foreach (var entry in sequence.scene.holds)
            {
                var hold = wall.AddHold(entry.id, new Vector2(entry.position[0], entry.position[1]));
                hold.role = entry.id == route.top_hold_id ? HoldRole.Top
                    : Array.IndexOf(route.start_hold_ids, entry.id) >= 0 ? HoldRole.Start
                    : HoldRole.Normal;
                hold.color = ColorFor(hold.role);
                hold.ApplyColor();
                m_HoldsById[entry.id] = hold;
            }
        }

        /// <summary>
        /// Where the four limbs are before a move: the pose the plan records *after* it, with the
        /// moving limb put back where it came from.
        /// </summary>
        Dictionary<Limb, Hold> StanceBefore(MoveJson move)
        {
            var stance = new Dictionary<Limb, Hold>();
            foreach (var limb in ClimberRagdoll.Limbs)
            {
                stance[limb] = HoldOf(move.pose.HoldId(limb));
            }

            stance[LimbOf(move.moving_limb)] = HoldOf(move.from_hold_id);
            return stance;
        }

        /// <summary>
        /// Hips for a stance taken from a plan. Stage 1 can put the hips at a fixed point because it
        /// authors the stance; here the holds come first, so the hips go between them and the IK
        /// folds the limbs out to reach.
        /// </summary>
        Vector3 StartHips(Dictionary<Limb, Hold> stance, float bias)
        {
            var hands = (stance[Limb.LeftHand].wallPosition + stance[Limb.RightHand].wallPosition) * 0.5f;
            var feet = (stance[Limb.LeftFoot].wallPosition + stance[Limb.RightFoot].wallPosition) * 0.5f;
            return wall.transform.TransformPoint(new Vector3((hands.x + feet.x) * 0.5f,
                Mathf.Lerp(feet.y, hands.y, bias), -hipsDistanceFromWall));
        }

        Hold HoldOf(int id)
        {
            return m_HoldsById.TryGetValue(id, out var hold) ? hold : null;
        }

        Color ColorFor(HoldRole role)
        {
            return role == HoldRole.Start ? startColor : role == HoldRole.Top ? topColor : normalColor;
        }

        static Limb LimbOf(string name)
        {
            switch (name)
            {
                case "left_hand": return Limb.LeftHand;
                case "right_hand": return Limb.RightHand;
                case "left_foot": return Limb.LeftFoot;
                case "right_foot": return Limb.RightFoot;
                default: throw new ArgumentException("unknown limb '" + name + "'");
            }
        }

        string SequencePath(int index)
        {
            // Application.dataPath is <repo>/ClimbingBotUnity/Assets. ponytail: reads the repo
            // directly, so this is an editor-only environment -- a player build has no repo beside
            // it. Training runs in the editor (docs/05), so nothing yet needs StreamingAssets.
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", sequencePrefix + index));
        }

        Sequence Load(int index)
        {
            var directory = SequencePath(index);
            if (k_Cache.TryGetValue(directory, out var cached))
            {
                return cached;
            }

            var sequence = Read(directory);
            k_Cache[directory] = sequence;
            return sequence;
        }

        /// <summary>
        /// Reads and validates one sequence, or logs why it is unusable and returns null. Called once
        /// per sequence per session, so it is allowed to be picky: a plan that does not line up with
        /// its wall would otherwise show up as an unexplained stall halfway up.
        /// </summary>
        Sequence Read(string directory)
        {
            var scenePath = Path.Combine(directory, "scene.json");
            var planPath = Path.Combine(directory, "plan.json");
            if (!File.Exists(scenePath) || !File.Exists(planPath))
            {
                Debug.LogWarning($"Stage2Environment: no scene.json/plan.json in {directory}.", this);
                return null;
            }

            var scene = JsonUtility.FromJson<SceneJson>(File.ReadAllText(scenePath));
            var plan = JsonUtility.FromJson<PlanJson>(File.ReadAllText(planPath));

            if (scene?.holds == null || scene.holds.Length == 0 || scene.routes == null || scene.routes.Length == 0)
            {
                Debug.LogWarning($"Stage2Environment: {scenePath} has no holds or no route.", this);
                return null;
            }

            if (plan?.moves == null || plan.moves.Length == 0)
            {
                Debug.LogWarning($"Stage2Environment: {planPath} has no moves.", this);
                return null;
            }

            if (requireReachedTop && !plan.reached_top)
            {
                Debug.Log($"Stage2Environment: skipping {directory}, the plan never reached the top.", this);
                return null;
            }

            var ids = new HashSet<int>();
            foreach (var hold in scene.holds)
            {
                if (hold.position == null || hold.position.Length < 2)
                {
                    Debug.LogWarning($"Stage2Environment: hold {hold.id} in {scenePath} has no position.", this);
                    return null;
                }

                ids.Add(hold.id);
            }

            foreach (var move in plan.moves)
            {
                if (!ids.Contains(move.from_hold_id) || !ids.Contains(move.target_hold_id)
                    || !ids.Contains(move.pose.left_hand) || !ids.Contains(move.pose.right_hand)
                    || !ids.Contains(move.pose.left_foot) || !ids.Contains(move.pose.right_foot))
                {
                    Debug.LogWarning($"Stage2Environment: {planPath} moves {move.moving_limb} between holds "
                        + "its wall does not have. Was the plan made against a different wall?", this);
                    return null;
                }
            }

            // Route/Completed is only a top-out rate if finishing the plan *is* a top-out, which
            // means both hands on the top hold (ClimberRagdoll.IsToppedOut).
            var last = plan.moves[plan.moves.Length - 1].pose;
            if (requireReachedTop && (last.left_hand != scene.routes[0].top_hold_id
                || last.right_hand != scene.routes[0].top_hold_id))
            {
                Debug.LogWarning($"Stage2Environment: {planPath} claims the top but does not end with "
                    + "both hands on it; skipping so Route/Completed stays a top-out rate.", this);
                return null;
            }

            return new Sequence { scene = scene, plan = plan };
        }

        void OnGUI()
        {
            if (!showHud || m_Sequence == null || TargetHold == null)
            {
                return;
            }

            if (m_HudStyle == null)
            {
                // The default label is 12 px and white on a pale sky, which is not a readout.
                m_HudStyle = new GUIStyle(GUI.skin.label) { fontSize = 18 };
                m_HudStyle.normal.textColor = Color.white;
            }

            // Same trick as GripLoadHud: IMGUI lays out in screen pixels, so scale it or the readout
            // shrinks to nothing on a large game view.
            var matrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(Vector3.one * Mathf.Max(1f, Screen.height / 1080f));

            var moves = m_Sequence.plan.moves.Length;
            GUI.Box(new Rect(6, 6, 640, 76), GUIContent.none);
            GUI.Label(new Rect(14, 8, 620, 24), $"STAGE 2   {sequencePrefix}{m_Current}"
                + $"   move {m_Move + 1}/{moves}" + (m_StartMove > 0 ? $" (from {m_StartMove + 1})" : "")
                + $"   {TargetLimb} -> hold {TargetHold.id}", m_HudStyle);
            GUI.Label(new Rect(14, 30, 620, 24), m_Completed ? "TOPPED OUT"
                : $"route {100f * (m_Move - m_StartMove) / moves:0}%"
                    + (IsTargetReached ? "   MOVE REACHED" : ""), m_HudStyle);
            GUI.Label(new Rect(14, 52, 620, 24),
                $"full routes topped out: {m_Completions}/{m_Attempts}", m_HudStyle);

            GUI.matrix = matrix;
        }

        // Scene JSON (docs/07) and the planner's plan.json, as much of them as Stage 2 needs.
        // JsonUtility ignores the fields left out.
        [Serializable]
        class SceneJson
        {
            public HoldJson[] holds;
            public RouteJson[] routes;
        }

        [Serializable]
        class HoldJson
        {
            public int id;
            public float[] position;
        }

        [Serializable]
        class RouteJson
        {
            public int[] start_hold_ids;
            public int top_hold_id;
        }

        [Serializable]
        class PlanJson
        {
            public bool reached_top;
            public MoveJson[] moves;
        }

        [Serializable]
        class MoveJson
        {
            public string moving_limb;
            public int from_hold_id;
            public int target_hold_id;
            public PoseJson pose;
        }

        [Serializable]
        class PoseJson
        {
            public int left_hand;
            public int right_hand;
            public int left_foot;
            public int right_foot;

            public int HoldId(Limb limb)
            {
                switch (limb)
                {
                    case Limb.LeftHand: return left_hand;
                    case Limb.RightHand: return right_hand;
                    case Limb.LeftFoot: return left_foot;
                    default: return right_foot;
                }
            }
        }
    }
}

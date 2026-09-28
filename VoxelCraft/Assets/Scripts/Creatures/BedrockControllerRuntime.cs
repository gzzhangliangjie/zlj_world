using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// Bedrock animation-controller runtime (B-plan, SPEC.md §6/§7).
    ///
    /// Semantics follow eyelib's BrControllerExecutor (source-read evidence):
    ///  - tick: evaluate the current state's transitions in order; the FIRST
    ///    true condition switches state (evalAsBool).
    ///  - a state's animations list plays SIMULTANEOUSLY each frame, each with
    ///    its own blend-weight expression (string => weight, default 1).
    ///  - animations referenced by SHORT NAME; resolved through the entity's
    ///    animations map (short -> animation.X.Y / controller.X.Y).
    ///  - a controller entry in the map resolves to a NESTED controller.
    ///  - scripts.animate (entity json) is the root driver: entries run every
    ///    frame, entries may carry weight expressions too.
    ///
    /// This runtime only SCHEDULES clips on the BedrockAnimationPlayer (Play /
    /// Stop / gaitWeight); pose math stays in the player. Behaviour triggers
    /// (sit/roll events) still flow through BlockyAnimal.behaviours until they
    /// migrate; the controller output feeds them via ControllerEvent.
    /// </summary>
    public class BedrockControllerRuntime
    {
        // ---- parsed model ----
        public class AnimRef { public string shortName; public string weightExpr; }
        public class CState
        {
            public string name;
            public List<AnimRef> animations = new List<AnimRef>();
            public List<KeyValuePair<string, string>> transitions = new List<KeyValuePair<string, string>>();
        }
        public class Controller
        {
            public string name;
            public string initialState;
            public Dictionary<string, CState> states = new Dictionary<string, CState>();
        }

        // ---- runtime instance ----
        class CInst { public Controller def; public string current; public float stateTime; }
        class ClipInst { public string clip; public float weight; }

        readonly Dictionary<string, Controller> controllers = new Dictionary<string, Controller>();
        // entity animations map: short -> full name (animation.* or controller.*)
        readonly Dictionary<string, string> entityAnims = new Dictionary<string, string>();
        // root driver: scripts.animate entries (shortName -> weight expr or null)
        readonly List<AnimRef> rootAnimate = new List<AnimRef>();
        readonly List<CInst> active = new List<CInst>();
        readonly HashSet<string> playing = new HashSet<string>();
        readonly BedrockAnimationPlayer player;
        public System.Action<string> ControllerEvent;

        // Clips the IMPORTER already applied (1.8 geo "bind_pose_rotation"
        // conversion: *.setup / wolf_setup-style "-this" re-root keys, or
        // default_leg_pose baked into REST at Bind). Scheduling them again
        // double-applies the conversion. Derived from the entity animations
        // map / clip names - pure data, no registry field.
        // Evidence: pig/llama/mooshroom .setup = body ["-this",0,0];
        // wolf .setup = position family re-rooting legs/tail/upperbody;
        // spider .default_leg_pose baked via BakeDefaultLegPose (player.cs).
        readonly HashSet<string> skipClips = new HashSet<string>();

        public BedrockControllerRuntime(BedrockAnimationPlayer player,
            Dictionary<string, object> controllersJson,
            Dictionary<string, object> entityDescription)
        {
            this.player = player;
            if (controllersJson != null)
                foreach (var kv in controllersJson)
                    controllers[kv.Key] = ParseController(kv.Key, kv.Value as Dictionary<string, object>);
            if (entityDescription != null &&
                entityDescription.TryGetValue("animations", out object am) &&
                am is Dictionary<string, object> animMap)
                foreach (var kv in animMap)
                    if (kv.Value is string s) entityAnims[kv.Key] = s;
            if (entityDescription != null &&
                entityDescription.TryGetValue("scripts", out object sc) &&
                sc is Dictionary<string, object> scripts &&
                scripts.TryGetValue("animate", out object an) && an is List<object> animate)
                foreach (var e in animate)
                {
                    if (e is string sn) rootAnimate.Add(new AnimRef { shortName = sn });
                    else if (e is Dictionary<string, object> eo)
                        foreach (var kv in eo)
                            rootAnimate.Add(new AnimRef { shortName = kv.Key, weightExpr = kv.Value as string });
                }
            // Legacy driver for entities WITHOUT scripts.animate: the
            // vanilla client plays ALL controllers in the species' ac file
            // (they self-gate via transitions) - e.g. hoglin/wolf/parrot.
            if (rootAnimate.Count == 0)
            {
                foreach (var c in controllers.Keys)
                    rootAnimate.Add(new AnimRef { shortName = c });
                // Engine role (vanilla C++ base-class renderer): if the anims
                // map declares "move"/"look_at_target*" but NO controller in
                // the ac file references them (zombie/skeleton: only attack/
                // swimming controllers exist), the vanilla client drives them
                // from the humanoid base renderer - move weighted by
                // modified_move_speed, look_at always resident. Add them as
                // root entries so the generic path owns this too.
                bool anyRef = false;
                foreach (var c in controllers.Values)
                    foreach (var st in c.states.Values)
                        foreach (var a in st.animations)
                            if (a.shortName == "move" || a.shortName.StartsWith("look_at"))
                            { anyRef = true; break; }
                if (!anyRef)
                {
                    if (entityAnims.ContainsKey("move"))
                        rootAnimate.Add(new AnimRef { shortName = "move", weightExpr = "query.modified_move_speed" });
                    foreach (var kv in entityAnims)
                        if (kv.Key.StartsWith("look_at"))
                        { rootAnimate.Add(new AnimRef { shortName = kv.Key }); break; }
                }
            }

            // Derive skipClips: full names ending ".setup" mapped in the
            // entity animations table are 1.8 bind-conversion clips the
            // importer already applied; ".default_leg_pose" clips are baked
            // into REST at Bind (BakeDefaultLegPose).
            foreach (var kv in entityAnims)
            {
                if (kv.Value != null && (kv.Value.EndsWith(".setup") || kv.Value.EndsWith(".default_leg_pose")))
                    skipClips.Add(kv.Value);
            }
            foreach (var cn in player.ClipNames)
                if (cn != null && cn.EndsWith(".default_leg_pose"))
                    skipClips.Add(cn);
            // Registry-declared rest-baked clips (bakedSetupClips, e.g.
            // polarbear.move "-9 - 2*standing_scale - this" family): baked
            // into REST at Bind, so runtime playback would double-apply.
            if (player.bakedSetupClips != null)
                foreach (var cn in player.bakedSetupClips)
                    skipClips.Add(cn);
        }

        static Controller ParseController(string name, Dictionary<string, object> def)
        {
            var c = new Controller { name = name };
            if (def == null) return c;
            if (def.TryGetValue("initial_state", out object ist) && ist is string iss) c.initialState = iss;
            if (!(def.TryGetValue("states", out object sv) && sv is Dictionary<string, object> states)) return c;
            foreach (var kv in states)
            {
                var st = new CState { name = kv.Key };
                if (kv.Value is Dictionary<string, object> sd)
                {
                    if (sd.TryGetValue("animations", out object av) && av is List<object> anims)
                        foreach (var a in anims)
                        {
                            if (a is string s) st.animations.Add(new AnimRef { shortName = s });
                            else if (a is Dictionary<string, object> ao)
                                foreach (var kv2 in ao)
                                    st.animations.Add(new AnimRef { shortName = kv2.Key, weightExpr = kv2.Value as string });
                        }
                    if (sd.TryGetValue("transitions", out object tv) && tv is List<object> trans)
                        foreach (var t in trans)
                            if (t is Dictionary<string, object> to)
                                foreach (var kv2 in to)
                                    st.transitions.Add(new KeyValuePair<string, string>(kv2.Key, kv2.Value as string));
                }
                c.states[kv.Key] = st;
            }
            return c;
        }

        /// <summary>Entity state fed by the game layer each tick (0/1 flags,
        /// counters). Values flow into Molang Ctx for conditions/weights.</summary>
        public class EntityState
        {
            public float isBaby, isSitting, isSleeping, isOnGround = 1f, isRiding, isJumping,
                isDancing, hasTarget, isStalking, isInterested, isStunned,
                isShakingWetness, isResting, isGrazing, isInWater, sitAmount, lieAmount,
                rollCounter, allAnimationsFinished, modifiedMoveSpeed;
            public Dictionary<string, float> properties = new Dictionary<string, float>();
            public Dictionary<string, float> variables = new Dictionary<string, float>();
        }

        readonly EntityState state = new EntityState();
        public EntityState State => state;

        BedrockAnimationPlayer.Molang.Ctx BuildCtx()
        {
            var c = new BedrockAnimationPlayer.Molang.Ctx
            {
                isBaby = state.isBaby,
                isSitting = state.isSitting,
                isSleeping = state.isSleeping,
                isOnGround = state.isOnGround,
                isRiding = state.isRiding,
                isJumping = state.isJumping,
                isDancing = state.isDancing,
                hasTarget = state.hasTarget,
                isStalking = state.isStalking,
                isInterested = state.isInterested,
                isStunned = state.isStunned,
                isShakingWetness = state.isShakingWetness,
                isResting = state.isResting,
                isGrazing = state.isGrazing,
                isInWater = state.isInWater,
                sitAmount = state.sitAmount,
                lieAmount = state.lieAmount,
                rollCounter = state.rollCounter,
                allAnimationsFinished = state.allAnimationsFinished,
                modifiedMoveSpeed = state.modifiedMoveSpeed,
                vars = state.variables,
                propertyLookup = k => state.properties.TryGetValue(k, out var v) ? v : 0f,
            };
            return c;
        }

        /// <summary>Advance the state machine one tick; schedule clips on the
        /// player. Call AFTER player-side pre_animation vars are set.</summary>
        public void Tick(float dt)
        {
            var ctx = BuildCtx();
            var want = new List<ClipInst>();

            // 1. root driver
            foreach (var e in rootAnimate)
            {
                float w = 1f;
                if (e.weightExpr != null) w = BedrockAnimationPlayer.Molang.Eval(e.weightExpr, ctx);
                if (w <= 0.01f) continue;
                Drive(e.shortName, w, ctx, want, 0);
            }

            // 2. diff against currently playing: stop removed, start added;
            //    re-push weights every tick (expressions are re-evaluated,
            //    eyelib ticks updateAnimations with blendValue.eval(scope))
            var wantSet = new HashSet<string>(want.Select(w => w.clip));
            foreach (var clip in playing.ToList())
                if (!wantSet.Contains(clip)) { player.Stop(clip); playing.Remove(clip); }
            foreach (var w in want)
            {
                if (!playing.Contains(w.clip)) { player.Play(w.clip); playing.Add(w.clip); }
                player.SetClipWeight(w.clip, w.weight);
            }
        }

        void Drive(string shortName, float weight, BedrockAnimationPlayer.Molang.Ctx ctx, List<ClipInst> want, int depth)
        {
            if (depth > 4) return;
            if (!entityAnims.TryGetValue(shortName, out string full)) full = shortName;
            if (full.StartsWith("controller.", StringComparison.Ordinal))
            {
                if (!controllers.TryGetValue(full, out Controller c)) return;
                // find / create instance
                CInst inst = null;
                foreach (var a in active) if (a.def == c) { inst = a; break; }
                if (inst == null)
                {
                    inst = new CInst { def = c, current = c.initialState };
                    if (inst.current == null && c.states.Count > 0) inst.current = c.states.Keys.First();
                    active.Add(inst);
                }
                inst.stateTime += dt_cache;
                var st = c.states.TryGetValue(inst.current, out var s) ? s : null;
                if (st == null) return;
                // transitions: first true wins
                foreach (var t in st.transitions)
                {
                    if (string.IsNullOrEmpty(t.Value)) continue;
                    if (BedrockAnimationPlayer.Molang.Eval(t.Value, ctx) != 0f)
                    {
                        if (c.states.ContainsKey(t.Key))
                        {
                            inst.current = t.Key;
                            inst.stateTime = 0f;
                            ControllerEvent?.Invoke($"{c.name}:{t.Key}");
                            st = c.states[t.Key];
                        }
                        break;
                    }
                }
                foreach (var a in st.animations)
                {
                    float w = weight;
                    if (a.weightExpr != null) w *= BedrockAnimationPlayer.Molang.Eval(a.weightExpr, ctx);
                    if (w > 0.01f) Drive(a.shortName, w, ctx, want, depth + 1);
                }
            }
            else
            {
                // animation.* - schedule on the player (full name IS the clip
                // name). Skip clips the importer/bind already applied.
                if (skipClips.Contains(full)) return;
                want.Add(new ClipInst { clip = full, weight = weight });
            }
        }

        static float dt_cache;
        public static void SetDt(float dt) { dt_cache = dt; }

        /// <summary>Diagnostics: clips scheduled by the last Tick with
        /// weights (probe/harness debugging only).</summary>
        public string DebugScheduled()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in playing)
                sb.Append(p).Append("(w=").Append(player.GetClipWeight(p).ToString("0.00")).Append(") ");
            return sb.ToString();
        }
    }
}

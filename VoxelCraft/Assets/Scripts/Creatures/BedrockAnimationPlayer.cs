using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// Plays official Mojang bedrock animation JSON (resource_pack/animations/
    /// *.animation.json) on a transform tree built by BedrockGeoImporter.
    ///
    /// Coordinate convention (matches BedrockGeoImporter): bedrock rotation
    /// [rx,ry,rz] -> Unity Euler(rx,-ry,-rz) applied AFTER the bone's rest
    /// (X identity: pixel-verified by BakeSetup wolf body +90 (wolfdump7) and
    /// hd1 fox-sit direction probe; Y/Z negated under the importer's mirrored
    /// frame x'=-x, z'=-z. Blockbench import inverts x,y in ITS frame;
    /// mirrored through M=diag(-1,1,1... ours keeps z negated, y negated.)
    /// rotation; bedrock position [x,y,z] -> Unity (-x, y, -z) * (1/16).
    ///
    /// Molang-lite subset (covers the vanilla animal clips):
    ///   numbers, + - * / ( ), comparisons, cond ? a : b,
    ///   math.cos/sin/abs/mod (radians), query.anim_time,
    ///   query.modified_distance_moved, query.head_yaw, variable.*, this.
    /// Unknown identifiers evaluate to 0.
    /// </summary>
    public class BedrockAnimationPlayer : MonoBehaviour
    {
        // ---------------- data model ----------------
        public class Keyframe
        {
            public float time;
            public Vector3 post;
            public string[] expr;          // non-null entries are molang
        }

        public class Track
        {
            public string bone;
            public string channel;        // position | rotation | scale
            // "relative_to":{"rotation":"entity"} in the animation file: the
            // channel's values are FINAL entity-space angles (vanilla wiki:
            // "makes the bone rotation relative to the entity instead of the
            // bone's parent"). Numerically identical to the "-this" absolute
            // convention when no parent rotates, which holds for body->head.
            public bool entitySpace;
            // expressions containing "-this" read the live accumulated value
            public bool finalByThis;
            public List<Keyframe> frames = new List<Keyframe>();
        }

        public class Clip
        {
            public string name;
            public float length = -1f;    // -1 = derive from max keyframe time
            public bool loop = true;
            public bool holdOnLast;       // loop:"hold_on_last_frame" (roll_up family)
            public bool timeFromDistance; // anim_time_update = modified_distance_moved
            public bool timeFromWalkVar;  // anim_time_update = variable.walk_anim_time_update (armadillo)
            // Generic anim_time_update expression (e.g. goat ram_attack
            // "Math.max(query.anim_time + (variable.should_bow_head ?
            // query.delta_time : -query.delta_time * 4), 0)"): the clip
            // clock is driven by this molang each tick instead of realtime.
            public string timeExpr;
            public List<Track> tracks = new List<Track>();
        }

        private class Playing
        {
            internal Clip clip;
            internal float time;
            internal bool absolute; // setup/sitting-style: values are targets
            // Per-clip blend weight from the controller runtime (eyelib
            // BrClipExecutor: multiplier chains controller entry weight into
            // the clip; the delta is scaled, not a lerp toward rest).
            internal float weight = 1f;
            // Frozen `this` per rotation track, captured at Play() time.
            // Bedrock reads the channel's value when the clip STARTS; a live
            // per-frame reading oscillates (45-this -> -45, next frame reads
            // -45 -> +90 ... the pose never settles).
            internal Dictionary<string, Vector3> thisVals;
        }

        // ---------------- public API ----------------
        [Tooltip("Bedrock animation JSONs (resource_pack/animations)")]
        public List<TextAsset> clipsJson = new List<TextAsset>();
        public readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>();

        // entity.json scripts.pre_animation lines (evaluated every tick
        // BEFORE clips sample; eyelib EntityRenderOrchestrator.java:259,623:
        // scripts.pre_animation().eval(scope) runs each animation frame).
        // Assignment targets live in `variables` - same store the clips read.
        public List<string> preAnimation = new List<string>();
        public readonly Dictionary<string, float> variables = new Dictionary<string, float>();

        private readonly List<Playing> playing = new List<Playing>();
        private readonly Dictionary<string, Transform> boneIndex = new Dictionary<string, Transform>();
        private readonly Dictionary<Transform, Vector3> restPos = new Dictionary<Transform, Vector3>();
        private readonly Dictionary<Transform, Quaternion> restRot = new Dictionary<Transform, Quaternion>();

        private Vector3 prevPos;
        private bool hasPrevPos;
        private float distanceMoved;
        /// <summary>Tick() integrates distance while this is true (batch tests
        /// and snapshots don't run Update, so Tick owns the clock).</summary>
        public bool moving = true;

        /// <summary>Set externally (e.g. by look-at AI) in degrees.</summary>
        public float headYawDeg;
        /// <summary>Owning species (set by BlockyAnimal.BuildModel); the
        /// property_eq engine-role lookup reads the registry's BP property
        /// table for this species.</summary>
        public string species = "";

        /// <summary>Goat: compute tcos gait vars from the entity-layer
        /// pre_animation script (goat.entity.json) each tick.</summary>
        public bool goatGait;
        /// <summary>Creeper legs clip reads variable.leg_rot (entity layer
        /// pre_animation); Tick computes it from the locomotion clock.</summary>
        public bool creeperGait;
        /// <summary>Horse v3 walk reads variable.leg_x_rot_anim /
        /// leg_stand_factor (horse_v3.entity.json pre_animation); Tick
        /// computes them from the locomotion clock. stand_anim stays 0
        /// (walking), rear/eat behaviours would drive it.</summary>
        public bool horseGait;
        /// <summary>Rabbit move clip reads variable.jump_rotation
        /// (engine skip-hop variable, behavior layer drives it in vanilla);
        /// Tick synthesizes the hop cycle 0..1 from the locomotion clock.</summary>
        public bool rabbitGait;
        /// <summary>Player/steve move clips read variable.tcos0
        /// (player.entity.json pre_animation); Tick computes it from the
        /// locomotion clock. gliding_speed_value engine default = 1.0
        /// (Java-equivalent limbSwingAmount saturation: mms/gsv &lt;= 1,
        /// gsv=0.6 produced 133.7° legs, 67% over Java's own cap).</summary>
        public bool steveGait;

        /// <summary>Locomotion speed (m/s) feeding gait variable math.</summary>
        public float walkSpeedRef = 1.4f;

        /// <summary>0 = rest pose, 1 = full gait. Set by the AI each frame;
        /// idle animals return their legs to the rest pose instead of
        /// freezing mid-swing (vanilla lerps limbSwing the same way).</summary>
        public float gaitWeight = 1f;
        // Clips authored with small swing amplitudes (spider walk: 23 deg)
        // are gated by query.move_speed in vanilla, not scaled to 30%; the
        // 0.3 default was calibrated on quadruped clips that author 80 deg.
        // Registry 'gait' may override this per species.
        public float gaitWeightTarget = 0.3f;
        // Clips baked into the REST pose at Bind (vanilla "setup" controller
        // plays them once; keeping them as looping extraClips would overwrite
        // the locomotion clip's channels every tick - parrot base zeroed the
        // walk clip's wing flap).
        public List<string> bakedSetupClips;

        // Subset of bakedSetupClips whose POSITION keys are genuine vanilla
        // pose constants (polarbear.move body -9px), not engine-compensation
        // junk (parrot base legs -6px).
        public List<string> bakedSetupPos;

        // Per-bone constant offsets contributed by the species ".setup" clip.
        // Our bind pose already bakes the setup result in (geo pivots/bpr), so
        // absolute clips ("x - this") must SUBTRACT these to get their delta.
        private readonly Dictionary<string, Vector3> setupPos =
            new Dictionary<string, Vector3>();

        private static float SetupConst(object v)
        {
            if (v is double d) return (float)d;
            if (v is long l) return l;
            if (v is string str)
            {
                str = str.Trim();
                if (float.TryParse(str, out var f)) return f;
                if (str == "-this" || str == "this") return 0f;
                // "X - this" / "X-this": the constant part
                if (str.EndsWith("- this") || str.EndsWith("-this"))
                {
                    var head = str.Substring(0, str.LastIndexOf('-')).Trim();
                    if (float.TryParse(head, out var h)) return h;
                }
            }
            return 0f; // complex expressions: assume 0
        }

        private void IndexSetupClips()
        {
            setupPos.Clear();
            foreach (var kv in clips)
            {
                if (!kv.Key.Contains(".setup")) continue;
                foreach (var tr in kv.Value.tracks)
                {
                    if (tr.channel != "position" || tr.frames.Count == 0) continue;
                    var kf = tr.frames[0];
                    Vector3 v = kf.post;
                    if (kf.expr != null)
                    {
                        // "-14 - this" style: per-component constant part
                        for (int i = 0; i < 3; i++)
                            if (kf.expr[i] != null) v[i] = SetupConst(kf.expr[i]);
                    }
                    string key = tr.bone.ToLowerInvariant();
                    if (!setupPos.ContainsKey(key)) setupPos[key] = v;
                }
            }
        }

        public void LoadClips()
        {
            clips.Clear();
            foreach (var ta in clipsJson)
            {
                if (ta == null) continue;
                ParseFile(ta.text);
            }
            IndexSetupClips();
        }

        public void Bind(Transform modelRoot)
        {
            boneIndex.Clear();
            restPos.Clear();
            restRot.Clear();
            IndexRec(modelRoot);
            BakeDefaultLegPose();
        }

        /// <summary>Bedrock plays "*.default_leg_pose" as a permanent state
        /// clip UNDER the locomotion clip (spider: legs bent 45 deg at rest).
        /// Our runtime applies Playing clips in list order, so a constantly
        /// re-applied single-keyframe pose would overwrite the walk clip's
        /// leg channels every tick. Bake it into the REST pose instead —
        /// exactly what the vanilla runtime achieves with controller
        /// priorities — so locomotion composes on top of it.</summary>
        private void BakeDefaultLegPose()
        {
            foreach (var kv in clips)
            {
                bool bake = kv.Key.EndsWith(".default_leg_pose") ||
                            (bakedSetupClips != null && bakedSetupClips.Contains(kv.Key));
                if (!bake) continue;
                bool bakePos = bakedSetupPos != null && bakedSetupPos.Contains(kv.Key);
                foreach (var tr in kv.Value.tracks)
                {
                    // ROTATION ONLY by default. The base clip's position keys
                    // (legs -6px, wings +/-1.5px) exist to compensate the
                    // ENGINE's bind convention (children re-placed under
                    // bind_pose_rotation) - same role as the pig/wolf ".setup"
                    // position keys we already skip: our importer re-roots
                    // parts geo-faithfully so they are flush at bind. Baking
                    // them here floated the parrot's legs 6px below the belly
                    // (user report). EXCEPTION (bakedSetupPos, e.g. polarbear
                    // .move body y "-9 - ..."): a CONSTANT position key that is
                    // genuine vanilla pose data - bedrock's additive runtime
                    // keeps applying it under walk (body sunk 9px, flush with
                    // the legs), so it belongs in the REST pose. Expression
                    // keys with query/variable refs stay out (not constant).
                    if (tr.frames.Count == 0) continue;
                    bool isPos = tr.channel == "position";
                    if (tr.channel != "rotation" && !isPos) continue;
                    if (isPos && !bakePos) continue;
                    Transform bone = null;
                    if (!boneIndex.TryGetValue(tr.bone, out bone))
                        foreach (var b2 in boneIndex)
                            if (string.Compare(b2.Key, tr.bone, true) == 0) { bone = b2.Value; break; }
                    if (bone == null) continue;
                    var kf = tr.frames[0];
                    Vector3 v = kf.post;
                    if (kf.expr != null)
                        for (int i = 0; i < 3; i++)
                            if (kf.expr[i] != null) v[i] = SetupConst(kf.expr[i]);
                    if (isPos)
                    {
                        // bedrock position channel = px/16 in MODEL space;
                        // Unity local = metres, x mirrored (importer convention).
                        // Sample() applies position as ADD px, so bake the same
                        // delta into restPos (restPos is metres).
                        Vector3 delta = new Vector3(-v.x / 16f, v.y / 16f, v.z / 16f);
                        bone.localPosition += delta;
                        restPos[bone] = bone.localPosition;
                        continue;
                    }
                    // bedrock "X - this" absolute target. These are ANIMATION
                    // rotation values (not geo bone rotations), so they follow
                    // the clip convention Unity Euler = (bx, -by, bz). The geo
                    // rest convention (-bx,-by,-bz) flipped the spider's legs
                    // up over the body instead of down to the ground.
                    bone.localRotation = Quaternion.Euler(v.x, -v.y, v.z) * bone.localRotation;
                    restRot[bone] = bone.localRotation;
                }
            }
        }

        /// <summary>Start a clip; no-op if it is already playing.</summary>
        public int ClipCount => clips.Count;

        /// <summary>All loaded clip full names (controller runtime derives
        /// its bind-conversion skip list from these).</summary>
        public IEnumerable<string> ClipNames => clips.Keys;

        public bool Play(string clipName)
        {
            return Play(clipName, false);
        }

        /// <summary>Controller-scheduled play: blend weight (0..1) chains in
        /// per eyelib BrClipExecutor (multiplier *= blendWeight).</summary>
        public bool Play(string clipName, bool absolute, float weight)
        {
            bool ok = Play(clipName, absolute);
            if (ok)
                for (int i = 0; i < playing.Count; i++)
                    if (playing[i].clip.name == clipName) playing[i].weight = weight;
            return ok;
        }

        /// <summary>Update the blend weight of an already-playing clip
        /// (controller weights are re-evaluated every tick).</summary>
        public void SetClipWeight(string clipName, float weight)
        {
            for (int i = 0; i < playing.Count; i++)
                if (playing[i].clip.name == clipName) playing[i].weight = weight;
        }

        /// <summary>Blend weight the controller currently assigns (1 if the
        /// clip is not controller-driven).</summary>
        public float GetClipWeight(string clipName)
        {
            foreach (var p in playing)
                if (p.clip.name == clipName) return p.weight;
            return 1f;
        }

        /// <summary>Name of the most recently played clip (null if none) -
        /// lets batch harnesses detect a chained-segment switch.</summary>
        public string CurrentClipName => playing.Count > 0 ? playing[playing.Count - 1].clip.name : null;

        /// <summary>Play a pose-style clip (setup/sitting): channel values
        /// are ABSOLUTE targets (bedrock "x - this" convention), applied
        /// directly instead of as offsets from the bind pose. Exclusive per
        /// clip; blends in over blendTime so the transition is not a snap.</summary>
        public bool Play(string clipName, bool absolute)
        {
            if (!clips.TryGetValue(clipName, out var clip)) return false;
            // stop any other absolute clip (pose states are mutually exclusive)
            for (int i = playing.Count - 1; i >= 0; i--)
                if (playing[i].absolute && playing[i].clip != clip)
                    Stop(playing[i].clip.name);
            // roll-family pose states are ALSO mutually exclusive, but they
            // chain (roll_up ends tucked = rolled_up starts tucked = unroll
            // starts tucked). Stop WITHOUT the rest-restore so the next clip
            // drives the same bones from the tucked pose.
            if (IsRollUpClip(clipName))
                for (int i = playing.Count - 1; i >= 0; i--)
                    if (IsRollUpClip(playing[i].clip.name) && playing[i].clip != clip)
                    {
                        rollUpStates.Remove(playing[i].clip.name);
                        playing.RemoveAt(i);
                    }
            for (int i = 0; i < playing.Count; i++)
                if (playing[i].clip == clip) return true;
            // Freeze `this` per rotation track at clip start (bedrock reads
            // the channel's pre-clip value once; live reads oscillate).
            var tv = new Dictionary<string, Vector3>();
            foreach (var tr in clip.tracks)
            {
                if (tr.channel != "rotation" && tr.channel != "position") continue;
                Transform bone = null;
                if (boneIndex.TryGetValue(tr.bone, out var b0)) bone = b0;
                else
                    foreach (var kv in boneIndex)
                        if (string.Compare(kv.Key, tr.bone, true) == 0) { bone = kv.Value; break; }
                if (bone == null) continue;
                tv[tr.bone.ToLowerInvariant() + "|" + tr.channel] = new Vector3(
                    ThisValue(bone, tr.channel, 0),
                    ThisValue(bone, tr.channel, 1),
                    ThisValue(bone, tr.channel, 2));
            }
            playing.Add(new Playing { clip = clip, time = 0f, absolute = absolute, thisVals = tv });
            if (IsSleepClip(clipName)) ApplySleepVisibility(true);
            if (IsRollUpClip(clipName)) rollUpStates[clipName] = new RollUpState { t = 0f, shellShown = false };
            return true;
        }

        public void Stop(string clipName)
        {
            Clip c = null;
            for (int i = playing.Count - 1; i >= 0; i--)
            {
                if (playing[i].clip.name == clipName)
                {
                    c = playing[i].clip;
                    playing.RemoveAt(i);
                }
            }
            if (c == null) return;
            if (IsSleepClip(c.name)) ApplySleepVisibility(false);
            // leaving a roll-family clip: restore the normal body unless
            // another roll-family clip is still playing (roll_up -> rolled_up)
            rollUpStates.Remove(clipName);
            if (!StillRolledUp())
                ApplyRolledUpVisibility(false);
            // restore rest pose on the bones this clip drove
            foreach (var tr in c.tracks)
            {
                if (!boneIndex.TryGetValue(tr.bone, out var b)) continue;
                if (restPos.TryGetValue(b, out var p)) b.localPosition = p;
                if (restRot.TryGetValue(b, out var r)) b.localRotation = r;
            }
        }

        public void StopAll()
        {
            bool hadSleep = false;
            foreach (var p in playing) if (IsSleepClip(p.clip.name)) hadSleep = true;
            playing.Clear();
            if (hadSleep) ApplySleepVisibility(false);
            foreach (var kv in restPos) kv.Key.localPosition = kv.Value;
            foreach (var kv in restRot) kv.Key.localRotation = kv.Value;
        }

        // ---- render_controller part_visibility (fox.render_controllers.json):
        //   leg* = !query.is_sleeping, head = !query.is_sleeping,
        //   head_sleeping = query.is_sleeping.
        // We map "a *.sleep clip is playing" to query.is_sleeping=true.
        // Cube-level only: head_sleeping is a CHILD of head, so bone-level
        // SetActive would kill the sleeping head along with the awake one.
        private static bool IsSleepClip(string name) => name.EndsWith(".sleep");

        // armadillo.render_controllers part_visibility:
        //   body/tail/hind legs = !variable.use_rolled_up_model,
        //   body_rolled_up      =  variable.use_rolled_up_model.
        // The entity layer flips use_rolled_up_model 0.2083s into roll_up
        // (rolled_up_time >= 5 frames at 24fps). We map "a .roll_up/.rolled_up
        // clip is playing past that point" to the same swap; unroll restores
        // as soon as its clip starts (vanilla: unrolling_time > 1.25 OR
        // rolled_up_time < 0.2083 shows the normal body).
        private static bool IsRollUpClip(string name) =>
            name.EndsWith(".roll_up") || name.EndsWith(".rolled_up") || name.EndsWith(".unroll") ||
            name.EndsWith(".unroll_fast") || name.EndsWith(".peek");
        private const float RollUpShellDelay = 0.2083f;

        private class RollUpState { public float t; public bool shellShown; }
        private readonly Dictionary<string, RollUpState> rollUpStates = new Dictionary<string, RollUpState>();

        private void ApplyRolledUpVisibility(bool rolled)
        {
            foreach (var kv in boneIndex)
            {
                string n = kv.Key.ToLowerInvariant();
                bool? vis = null;
                if (n == "body" || n == "tail" || n.EndsWith("_hind_leg")) vis = !rolled;
                else if (n == "body_rolled_up") vis = rolled;
                if (vis == null) continue;
                if (n == "body_rolled_up") kv.Value.gameObject.SetActive(rolled);
                SetCubesVisible(kv.Value, vis.Value);
            }
            // head/front legs stay visible either way (vanilla tucks them
            // with keyframes, not visibility).
            Debug.Log($"[PartVis] rolled_up={rolled} applied");
        }

        private void TickRollUpVisibility(string clipName, float dt)
        {
            if (!rollUpStates.TryGetValue(clipName, out var st)) return;
            st.t += dt;
            bool want = clipName.EndsWith(".rolled_up") || clipName.EndsWith(".peek") ||
                        (st.t >= RollUpShellDelay && !clipName.EndsWith(".unroll") && !clipName.EndsWith(".unroll_fast"));
            if (want != st.shellShown) { st.shellShown = want; ApplyRolledUpVisibility(want); }
        }

        private bool StillRolledUp()
        {
            foreach (var kv in rollUpStates)
            {
                if (kv.Key.EndsWith(".rolled_up") || kv.Key.EndsWith(".peek")) return true;
                if (!kv.Key.EndsWith(".unroll") && !kv.Key.EndsWith(".unroll_fast") && kv.Value.t >= RollUpShellDelay) return true;
            }
            return false;
        }

        private void ApplySleepVisibility(bool sleeping)
        {
            foreach (var kv in boneIndex)
            {
                string n = kv.Key.ToLowerInvariant();
                bool? vis = null;
                if (n == "head") vis = !sleeping;
                else if (n == "head_sleeping") vis = sleeping;
                else if (n.Length > 3 && n.StartsWith("leg")) vis = !sleeping;
                if (vis == null) continue;
                if (n == "head_sleeping") kv.Value.gameObject.SetActive(sleeping);
                SetCubesVisible(kv.Value, vis.Value);
            }
            Debug.Log($"[PartVis] sleeping={sleeping} applied");
        }

        /// Toggle only cube GameObjects ("cube_*" / "CubePivot"), never child
        /// bones, so hiding "head" leaves head_sleeping's cubes reachable.
        /// Descends through nested pivots but stops at child BONES (they own
        /// their own visibility in this pass).</summary>
        private static void SetCubesVisible(Transform node, bool vis, bool root = true)
        {
            foreach (Transform c in node)
            {
                if (c.name.StartsWith("cube_"))
                {
                    var r = c.GetComponent<Renderer>();
                    if (r != null) r.enabled = vis;
                }
                else if (c.name == "CubePivot") SetCubesVisible(c, vis, false);
                // child bones: skipped - visibility is per-bone
            }
        }

        // ---------------- parsing ----------------
        private void ParseFile(string text)
        {
            string clean = Regex.Replace(text, @"^\s*//[^\n]*", "", RegexOptions.Multiline);
            object o;
            try { o = MiniJson.Deserialize(clean); } catch { return; }
            if (!(o is Dictionary<string, object> root)) return;
            if (!(root.TryGetValue("animations", out object av) &&
                  av is Dictionary<string, object> anims)) return;

            foreach (var kv in anims)
            {
                if (!(kv.Value is Dictionary<string, object> a)) continue;
                var clip = new Clip { name = kv.Key };
                if (a.TryGetValue("animation_length", out object al))
                {
                    string ls = al is double ad ? ad.ToString(CultureInfo.InvariantCulture) : al as string;
                    if (ls != null && float.TryParse(ls, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var len)) clip.length = len;
                }
                if (a.TryGetValue("loop", out object lp))
                {
                    // bedrock: true | false | "hold_on_last_frame"
                    if (lp is bool lb) clip.loop = lb;
                    else if (lp is string ls)
                    {
                        clip.loop = false;
                        clip.holdOnLast = ls.Contains("hold");
                    }
                }
                if (a.TryGetValue("anim_time_update", out object atu) &&
                    atu is string atus && atus.Contains("modified_distance_moved"))
                    clip.timeFromDistance = true;
                // Armadillo-family: anim_time_update = variable.walk_anim_time_update
                // (entity pre_anim advances t by lerp(2,5,speed)*dt while WALKING;
                // the walk controller stops it when idle). Vanilla time rate at
                // mid speed = 3x realtime; our dist clock * 0.25 matches the
                // 38.17rad/m cos gaits, so use 0.75 = 0.25 * 3 for the
                // keyframed armadillo loop (1.4583s cycle at 1.4 m/s).
                if (a.TryGetValue("anim_time_update", out object atu2) &&
                    atu2 is string atus2 && atus2.Contains("variable.walk_anim_time_update"))
                    clip.timeFromWalkVar = true;
                // Generic form: any other string expression drives the clock
                // via molang (ram_attack-style gated rewind/fast-forward).
                else if (a.TryGetValue("anim_time_update", out object atu3) &&
                         atu3 is string atus3 && !clip.timeFromDistance && !clip.timeFromWalkVar)
                    clip.timeExpr = atus3;

                if (!(a.TryGetValue("bones", out object bv) &&
                      bv is Dictionary<string, object> bones)) continue;
                foreach (var bk in bones)
                {
                    if (!(bk.Value is Dictionary<string, object> bd)) continue;
                    // relative_to:{rotation:"entity"} is a property of the BONE
                    // ENTRY (sibling of the channels), not of each channel.
                    bool entitySpace = false;
                    if (bd.TryGetValue("relative_to", out object rtv) &&
                        rtv is Dictionary<string, object> rt &&
                        rt.TryGetValue("rotation", out object rtr) && rtr is string rts)
                        entitySpace = rts == "entity";
                    foreach (var ch in bd)
                    {
                        if (ch.Key != "position" && ch.Key != "rotation" && ch.Key != "scale") continue;
                        var track = new Track { bone = bk.Key, channel = ch.Key, entitySpace = entitySpace };
                        ParseChannel(track, ch.Value);
                        // "x - this" reads the LIVE accumulated channel
                        // value (eyelib reads this from the render entry
                        // during the clip pass). Track it so the additive
                        // pass can supply the current accumulation as `this`.
                        foreach (var kf in track.frames)
                            if (kf.expr != null)
                                foreach (var ex in kf.expr)
                                    if (ex != null && ex.Replace(" ", "").Contains("-this"))
                                    { track.finalByThis = true; break; }
                        if (track.frames.Count > 0) clip.tracks.Add(track);
                    }
                }
                clips[kv.Key] = clip;
            }
        }

        private void ParseChannel(Track track, object channel)
        {
            if (channel is List<object> arr && arr.Count == 3)
            {
                var kf = new Keyframe { time = 0f };
                Fill(kf, arr);
                track.frames.Add(kf);
                return;
            }
            if (!(channel is Dictionary<string, object> map)) return;
            var entries = new List<KeyValuePair<float, object>>();
            foreach (var mk in map)
            {
                if (float.TryParse(mk.Key, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var t))
                    entries.Add(new KeyValuePair<float, object>(t, mk.Value));
            }
            entries.Sort((x, y) => x.Key.CompareTo(y.Key));
            foreach (var e in entries)
            {
                var kf = new Keyframe { time = e.Key };
                if (e.Value is Dictionary<string, object> pp && pp.TryGetValue("post", out object postObj))
                {
                    if (postObj is List<object> pl) Fill(kf, pl);
                }
                else if (e.Value is List<object> plain) Fill(kf, plain);
                track.frames.Add(kf);
            }
        }

        private static void Fill(Keyframe kf, List<object> arr)
        {
            var vals = new float[3];
            var exprs = new string[3];
            bool anyExpr = false;
            for (int i = 0; i < 3 && i < arr.Count; i++)
            {
                if (arr[i] is double d) vals[i] = (float)d;
                else if (arr[i] is string s)
                {
                    if (float.TryParse(s, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var n)) vals[i] = n;
                    else { exprs[i] = s; anyExpr = true; }
                }
            }
            kf.post = new Vector3(vals[0], vals[1], vals[2]);
            if (anyExpr) kf.expr = exprs;
        }

        // ---------------- runtime ----------------
        // Hand-built models use legacy bone names (Hip0..3, Head); official
        // bedrock clips address bedrock names (leg0..3, head, body). Alias
        // them at bind time so the same clips drive both build paths.
        private static readonly string[] AliasPairs =
            { "Hip0", "leg0", "Hip1", "leg1", "Hip2", "leg2", "Hip3", "leg3" };

        private void IndexRec(Transform t)
        {
            if (!boneIndex.ContainsKey(t.name))
            {
                boneIndex[t.name] = t;
                restPos[t] = t.localPosition;
                restRot[t] = t.localRotation;
                // legacy-name -> bedrock-name alias (first occurrence wins)
                for (int i = 0; i < AliasPairs.Length; i += 2)
                    if (t.name == AliasPairs[i] && !boneIndex.ContainsKey(AliasPairs[i + 1]))
                        boneIndex[AliasPairs[i + 1]] = t;
            }
            foreach (Transform c in t) IndexRec(c);
        }

        private void Update()
        {
            // distanceMoved is owned by Tick() (batch verification can't run
            // Update); Update only feeds real transform motion in play mode.
            Vector3 pos = transform.position;
            Tick();
        }

        /// <summary>Advance clocks and apply poses (exposed for tests).</summary>
        public void Tick() { Tick(Time.deltaTime > 0f ? Time.deltaTime : 1f / 60f); }

        /// <summary>Batch verification drives time manually: Time.deltaTime
        /// is ZERO in editor batch mode (no frames), so dt must be passed in
        /// there or the gait clock never advances.</summary>
        public void Tick(float dt)
        {
            // Advance the locomotion clock inside Tick (NOT Update): batch
            // verification and editor snapshots never run MonoBehaviour
            // Update, and the gait froze at cos(0) in the last regression.
            if (moving) distanceMoved += walkSpeedRef * dt;
            lifeTime += dt;
            // Engine role: hopper wing-flap phase advances while airborne
            // (parrot flying state). On the ground vanilla holds it still
            // (folded wings, wing_flap evaluates to sin(pos)*speed with the
            // held phase). airborneWings is set by the game layer/harness.
            if (airborneWings) wingFlapPos += dt * wingFlapSpd * 57.3f;
            // Generic entity pre_animation (B-plan): eval each line, then
            // store assignments into `variables`. Runs BEFORE the flag-based
            // synthesizers below so migrated species leave the flags off and
            // this is the single source (eyelib evals pre_animation every
            // animation frame, EntityRenderOrchestrator.java:259,623).
            if (preAnimation.Count > 0)
            {
                // Engine-default variables referenced by vanilla pre_anim
                // (goat/hoglin/zombie divide by gliding_speed_value;
                // attack_time=-1 means "no attack"). TryAdd so entity
                // initialize lines / registry extraVariables can override.
                // gsv=1.0: Java limbSwingAmount saturation equivalent
                // (mms/gsv <= 1); 0.6 was an unsourced guess that gave
                // 133.7° humanoid leg swing (vanilla ≈ 80° max).
                variables.TryAdd("gliding_speed_value", 1.0f);
                variables.TryAdd("attack_time", -1f);
                var pc = BuildCtx(lifeTime);
                pc.deltaTime = dt;
                pc.moveSpeed = moving ? Mathf.Clamp01(walkSpeedRef) : 0f;
                foreach (var line in preAnimation)
                    EvalAssign(line, pc);
            }
            if (goatGait)
            {
                // goat.entity.json pre_animation (gliding_speed_value ~ 0.6):
                // tcos_right_side = cos(dist * 38.17) * move_speed / 0.6 * 57.3
                float speed = Mathf.Clamp01(walkSpeedRef);
                float tcos = Mathf.Cos(distanceMoved * 38.17f * 0.25f) *
                             (speed / 0.6f) * 57.3f;
                variables["tcos_right_side"] = tcos;
                variables["tcos_left_side"] = -tcos;
            }
            // creeper.entity.json pre_animation:
            // variable.leg_rot = cos(dist * 38.17326) * 80.22 * move_speed
            if (creeperGait)
            {
                float cspeed = Mathf.Clamp01(walkSpeedRef);
                variables["leg_rot"] = Mathf.Cos(distanceMoved * 38.17326f * 0.25f) *
                                       80.22f * cspeed;
            }
            // horse_v3.entity.json pre_animation (walking: stand_anim=0):
            //   leg_stand_factor = cos(dist * 38.38 + 180)
            //   leg_x_rot_anim   = leg_stand_factor * 45.8 * move_speed
            if (horseGait)
            {
                float hspeed = Mathf.Clamp01(walkSpeedRef);
                float lsf = Mathf.Cos(distanceMoved * 38.38f * 0.25f + Mathf.PI);
                variables["leg_stand_factor"] = lsf;
                variables["leg_x_rot_anim"] = lsf * 45.8f * hspeed;
                variables["stand_anim"] = 0f;
                // horse_v1 (donkey_v1): leg_walk_factor replaces
                // leg_stand_factor in the walk clip; legs use
                // leg_walk_factor*28.6*speed.
                variables["leg_walk_factor"] = lsf;
            }
            // rabbit.entity.json: variable.jump_rotation is the engine hop
            // variable (0 grounded .. 1 tucked mid-hop; movement.skip). Legs
            // fold as it rises. Synthesize a hop cycle at the same cadence
            // as the cos gaits (dist*38.38*0.25), gated by walk speed so idle
            // returns to the grounded pose (jump_rotation = 0).
            if (rabbitGait)
            {
                float rspeed = Mathf.Clamp01(walkSpeedRef);
                variables["jump_rotation"] =
                    Mathf.Max(0f, Mathf.Cos(distanceMoved * 38.38f * 0.25f)) * rspeed;
            }
            // player.entity.json pre_animation (walking):
            //   tcos0 = cos(dist * 38.17) * move_speed / gliding_speed_value(1.0) * 57.3
            if (steveGait)
            {
                // query.modified_move_speed is engine-normalised (~0.25
                // walking, see BlockyAnimal gait note); feeding raw 1.4 made
                // legs swing 134 deg - vanilla Java biped walk peaks ~40 deg
                // (1.4 rad * limbSwingAmount ~0.6 at sprint).
                float ms = Mathf.Clamp01(walkSpeedRef) * 0.25f;
                variables["tcos0"] = Mathf.Cos(distanceMoved * 38.17f * 0.25f) *
                                     (ms / 0.6f) * 57.3f;
            }
            Dictionary<string, Vector4> acc = null;
            Dictionary<string, Vector3> absFinal = null;
            for (int i = 0; i < playing.Count; i++)
            {
                var p = playing[i];
                var c = p.clip;
                // Gait weight eases here with the SAME dt the batch harness
                // passes in (BlockyAnimal.Update's Time.deltaTime-based lerp
                // never runs in editor batch mode - the weight froze at 1.0
                // and walk swung legs at the full authored 80 degrees).
                gaitWeight = Mathf.MoveTowards(
                    gaitWeight, moving ? gaitWeightTarget : 0f, dt * 2.5f);
                float maxT = c.length > 0f ? c.length : MaxTrackTime(c);
                if (c.timeFromDistance)
                    // anim_time feeds DEGREE-trig clips (cos(anim_time*38.17)).
                    // Vanilla modified_distance_moved counts internal units of
                    // ~14.325 per metre, so phase = 38.17*14.325 = 546.9 deg/m
                    // (period 0.658 m) - identical to the tuned legacy rad
                    // clock cos_rad(d*9.54). The old *0.25 was rad-scale.
                    p.time = distanceMoved * 14.325f;
                else if (c.timeFromWalkVar)
                    // armadillo: vanilla advances t by lerp(2,5,speed)*dt
                    // (~3x realtime at mid speed) - 0.75 = 0.25 * 3.
                    p.time = distanceMoved * 0.75f;
                else if (c.timeExpr != null)
                {
                    // Generic anim_time_update: molang returns the NEW clip
                    // time (goat ram_attack rewinds at -4x when the gate
                    // variable is 0, floors at Math.max(...,0)).
                    var tc = BuildCtx(p.time);
                    tc.deltaTime = dt;
                    float nt = Molang.Eval(c.timeExpr, tc);
                    p.time = Mathf.Max(0f, nt);
                }
                else p.time += dt;
                TickRollUpVisibility(c.name, dt);
                if (maxT > 0f && p.time > maxT)
                {
                    if (c.loop) p.time = p.time % maxT;
                    else if (c.holdOnLast || p.absolute) p.time = maxT; // hold final pose
                    else { playing.RemoveAt(i); continue; }
                }
                // ADDITIVE pass 1: accumulate relative deltas + absolute
                // finals per bone|channel; a lone clip keeps the exact
                // legacy Apply path (bit-identical golden poses).
                if (playing.Count > 1 && !p.absolute)
                {
                    if (acc == null) acc = new Dictionary<string, Vector4>();
                    if (absFinal == null) absFinal = new Dictionary<string, Vector3>();
                    Sample(c, p.time, p.absolute, p.thisVals, p.weight, acc, absFinal);
                }
                else Sample(c, p.time, p.absolute, p.thisVals, p.weight);
            }
            if (acc != null)
            {
                // absolute-only bones (no relative contributions) must
                // still flush: merge absFinal keys into the acc iteration
                // (creeper/wolf "-this" legs are absolute-only tracks).
                if (absFinal != null)
                    foreach (var ak2 in absFinal.Keys)
                        if (!acc.ContainsKey(ak2))
                            acc[ak2] = new Vector4(0f, 0f, 0f, 0f);
                foreach (var kv in acc)
                {
                    var parts = kv.Key.Split('|');
                    if (!boneIndex.TryGetValue(parts[0], out var bone))
                        foreach (var bkv in boneIndex)
                            if (string.Compare(bkv.Key, parts[0], true) == 0) { bone = bkv.Value; break; }
                    if (bone == null) continue;
                    Vector3 av = default;
                    bool isAbs = absFinal != null && absFinal.TryGetValue(kv.Key, out av);
                    Vector3 v = isAbs ? av : new Vector3(kv.Value.x, kv.Value.y, kv.Value.z);
                    Apply(bone, parts[1], v, isAbs);
                }
            }
        }

        private static float MaxTrackTime(Clip c)
        {
            float m = 0f;
            foreach (var tr in c.tracks)
                foreach (var kf in tr.frames)
                    if (kf.time > m) m = kf.time;
            return m;
        }

        private void Sample(Clip c, float time, bool absolute = false, Dictionary<string, Vector3> thisVals = null, float ctlWeight = 1f,
            Dictionary<string, Vector4> acc = null, Dictionary<string, Vector3> absFinal = null)
        {
            // Gait weight scales ONLY locomotion clips (identified the vanilla
            // way: anim_time_update = modified_distance_moved). Behaviour pose
            // clips (graze/sit/sleep) must hold full weight - blending them
            // toward rest washed the poses out while idle.
            // Controller blend weight chains in multiplicatively on EVERY clip
            // it schedules (eyelib BrClipExecutor.java:21,46: multiplier *=
            // blendWeight; the sampled delta is scaled).
            float w = absolute ? 1f : ((c.timeFromDistance || c.timeFromWalkVar)
                ? Mathf.Clamp01(gaitWeight) * ctlWeight : ctlWeight);
            foreach (var tr in c.tracks)
            {
                if (!boneIndex.TryGetValue(tr.bone, out var bone))
                {
                    // Bedrock clip bone names are lowercase ("upperbody") but
                    // geo bone names keep camelCase ("upperBody"): fall back
                    // to a case-insensitive match so official clips bind.
                    foreach (var kv in boneIndex)
                        if (string.Compare(kv.Key, tr.bone, true) == 0) { bone = kv.Value; break; }
                    if (bone == null) continue;
                }
                Vector3 v;
                if (tr.frames.Count == 1)
                {
                    var kf = tr.frames[0];
                    v = kf.expr != null ? EvalExpr(kf.expr, bone, tr.channel, time, tr.bone.ToLowerInvariant() + "|" + tr.channel, thisVals, kf.post)
                                        : kf.post;
                }
                else v = SampleKeyframes(tr, time, thisVals, tr.bone.ToLowerInvariant() + "|" + tr.channel);
                // entitySpace (relative_to:"entity") tracks: per the bedrock
                // runtime (eyelib BrClipExecutor: renderInfoEntry.rotation
                // .add(sampled), with `this` read back as bind+accumulated),
                // channel values are ADDITIVE DELTAS over the bind in entity
                // space, NOT final replacements. A constant 0 component
                // (hoglin look_at_target [0, yaw-this, 0]) contributes a
                // zero delta so the 50-deg head bind SURVIVES (Java
                // HoglinModel.DEFAULT_HEAD_X_ROT confirms), while "-this"
                // expressions converge the channel to the target on top of
                // bind. The old absolute-replacement path flattened the bind
                // to 0 during walk (M23 fix).
                bool absApply = absolute;
                if (!absApply && w < 1f)
                {
                    // Blend toward the BIND pose captured at Bind() time - a
                    // fixed target. Blending toward the bone's CURRENT value
                    // (previous frame's output) is a feedback loop: the pose
                    // never settles and the legs visibly jitter when idle.
                    // ROTATION: rest is an angle, Apply composes rest*delta,
                    // so Lerp(restEuler, v, w) is correct.
                    // POSITION: Apply() ADDS v to restPos (v is a delta in
                    // px). Blending toward RestValue (= bind pos in px)
                    // double-counts the rest and drags legs from (±2,3,±4)px
                    // toward the clip's near-zero deltas = legs squeezed to
                    // the centerline (armadillo 2026-09-27). Blend the DELTA
                    // toward zero instead.
                    if (tr.channel == "position") v = v * w;
                    else
                    {
                        Vector3 rest = RestValue(bone, tr.channel);
                        v = Vector3.Lerp(rest, v, w);
                    }
                }
                if (acc != null && !absApply)
                {
                    // Bedrock multi-clip semantics are ADDITIVE (eyelib
                    // BrClipExecutor.java: rotation.add(sampled)): every
                    // playing clip contributes its delta to the same bone
                    // channel, and a later clip's constant 0 component must
                    // NOT override an earlier clip's swing (steve: bob x=0
                    // erasing move.arms tcos0). Accumulate per bone|channel.
                    string ak = tr.bone.ToLowerInvariant() + "|" + tr.channel;
                    // "-this" tracks: the expression reads the channel's
                    // CURRENT accumulated value (net effect: replace). The
                    // pre-sampled v above used the frozen Play()-time this -
                    // resample with the live accumulation as this.
                    if (tr.finalByThis && acc.TryGetValue(ak, out var curAcc))
                    {
                        var live = new Dictionary<string, Vector3>();
                        live[tr.bone.ToLowerInvariant() + "|" + tr.channel] =
                            new Vector3(curAcc.x, curAcc.y, curAcc.z);
                        if (tr.frames.Count == 1 && tr.frames[0].expr != null)
                            v = EvalExpr(tr.frames[0].expr, bone, tr.channel, time,
                                tr.bone.ToLowerInvariant() + "|" + tr.channel, live, tr.frames[0].post);
                    }
                    if (acc.TryGetValue(ak, out var prev)) v += new Vector3(prev.x, prev.y, prev.z);
                    acc[ak] = new Vector4(v.x, v.y, v.z, 1f);
                }
                else if (absFinal != null && absApply)
                {
                    string ak = tr.bone.ToLowerInvariant() + "|" + tr.channel;
                    absFinal[ak] = v; // last absolute wins
                }
                else Apply(bone, tr.channel, v, absApply);
            }
        }

        private Vector3 RestValue(Transform bone, string channel)
        {
            switch (channel)
            {
                // Euler of the bind quaternion (Apply() re-composes with the
                // same convention: rest * Euler(-v)), so Lerp(rest, v, 1) == v.
                case "rotation":
                    return (restRot.TryGetValue(bone, out var r) ? r : bone.localRotation).eulerAngles;
                case "position":
                    return restPos.TryGetValue(bone, out var p)
                        ? p * 16f  // restPos is in metres; channel values are px
                        : bone.localPosition * 16f;
                default: return Vector3.zero;
            }
        }

        private Vector3 EvalExpr(string[] exprs, Transform bone, string channel, float time,
            string boneName = null, Dictionary<string, Vector3> frozenThis = null, Vector3 fallback = default)
        {
            // 38.17 rad/m raw is ~8Hz at 1.4 m/s (comically fast); all vanilla
                // gait clips in this project use a 0.25 beat scale on the distance
                // clock (calibrated on sheep/goat, applied species-wide).
                var ctx = BuildCtx(time);
            // Start from the PARSED CONSTANTS (kf.post) - Fill() stores plain
            // numbers there even when sibling components are expressions, so
            // "0, -115, expr" keeps its -115 constant y while z evaluates.
            var v = fallback;
            Vector3? frozen = null;
            if (frozenThis != null && boneName != null && frozenThis.TryGetValue(boneName.ToLowerInvariant(), out var fv))
                frozen = fv;
            for (int i = 0; i < 3; i++)
            {
                if (exprs[i] == null) continue;
                ctx.thisVal = frozen != null ? frozen.Value[i] : ThisValue(bone, channel, i);
                v[i] = Molang.Eval(exprs[i], ctx);
            }
            return v;
        }

        private Vector3 SampleKeyframes(Track tr, float t, Dictionary<string, Vector3> frozenThis = null, string boneName = null)
        {
            Vector3 At(Keyframe kf) => EvalKf(kf, t, frozenThis, boneName);
            var f = tr.frames;
            if (t <= f[0].time) return At(f[0]);
            if (t >= f[f.Count - 1].time) return At(f[f.Count - 1]);
            for (int i = 0; i < f.Count - 1; i++)
            {
                if (t >= f[i].time && t <= f[i + 1].time)
                {
                    float u = (t - f[i].time) / Mathf.Max(1e-5f, f[i + 1].time - f[i].time);
                    return Vector3.Lerp(At(f[i]), At(f[i + 1]), u);
                }
            }
            return At(f[f.Count - 1]);
        }

        private Vector3 EvalKf(Keyframe kf, float time, Dictionary<string, Vector3> frozenThis = null, string boneName = null)
        {
            if (kf.expr == null) return kf.post;
            var v = kf.post;
            // 38.17 rad/m raw is ~8Hz at 1.4 m/s (comically fast); all vanilla
                // gait clips in this project use a 0.25 beat scale on the distance
                // clock (calibrated on sheep/goat, applied species-wide).
                var ctx = BuildCtx(time);
            Vector3? frozen = null;
            if (frozenThis != null && boneName != null && frozenThis.TryGetValue(boneName.ToLowerInvariant(), out var fv))
                frozen = fv;
            for (int i = 0; i < 3; i++)
                if (kf.expr[i] != null)
                {
                    ctx.thisVal = frozen != null ? frozen.Value[i] : ThisValue(null, "rotation", i);
                    v[i] = Molang.Eval(kf.expr[i], ctx);
                }
            return v;
        }

        private float ThisValue(Transform bone, string channel, int comp)
        {
            if (bone == null) return 0f;
            if (channel == "position")
            {
                // Bedrock `this` for the position channel is the channel's
                // own PRE-CLIP value - for a fresh bind that is the setup
                // constant (setupPos), NOT the bind pivot: subtracting the
                // full rest over-shifted wolf sitting off-screen.
                string key = bone.name.ToLowerInvariant();
                if (setupPos.TryGetValue(key, out var sv))
                    return sv[comp];
                return 0f;
            }
            if (channel != "rotation") return 0f;
            if (!restRot.TryGetValue(bone, out var rest)) return 0f;
            // Bedrock `this` = the channel's CURRENT TOTAL bedrock angle
            // (e.g. wolf setup drove body to 90; sitting's "45 - this" must
            // see 90 so the pose lands at total -45 = chest-up sit). Total =
            // angle baked into rest (setup) + delta currently applied.
            Quaternion dq = Quaternion.Inverse(rest) * bone.localRotation;
            Vector3 de = dq.eulerAngles;
            Vector3 deltaBedrock = new Vector3(
                de.x > 180f ? de.x - 360f : de.x,
                -(de.y > 180f ? de.y - 360f : de.y),
                -(de.z > 180f ? de.z - 360f : de.z));
            Vector3 re = rest.eulerAngles;
            Vector3 bindBedrock = new Vector3(
                re.x > 180f ? re.x - 360f : re.x,
                -(re.y > 180f ? re.y - 360f : re.y),
                -(re.z > 180f ? re.z - 360f : re.z));
            Vector3 total = bindBedrock + deltaBedrock;
            return total[comp];
        }

        private void Apply(Transform bone, string channel, Vector3 v, bool absolute = false)
        {
            switch (channel)
            {
                case "rotation":
                    if (absolute)
                    {
                        // Bedrock "x - this" yields the FINAL bedrock angle;
                        // our bind pose already baked the geo bind_pose_rotation
                        // in (rest = Euler(-bpr)). Convert: Unity local =
                        // rest * Euler(v_final - bindAngle) where bindAngle
                        // is the bedrock angle baked into rest.
                        Quaternion rest = restRot.TryGetValue(bone, out var rr) ? rr : bone.localRotation;
                        Vector3 re = rest.eulerAngles;
                        Vector3 bindBedrock = new Vector3(
                            re.x > 180f ? re.x - 360f : re.x,
                            -(re.y > 180f ? re.y - 360f : re.y),
                            -(re.z > 180f ? re.z - 360f : re.z));
                        // Same convention as the relative branch: bedrock x/z
                        // map identity, y negated (Ry(pi)-conjugated frame;
                        // z-negation was a bug - fox.sleep head curled away).
                        Vector3 target = v - bindBedrock;
                        bone.localRotation = rest * Quaternion.Euler(target.x, -target.y, target.z);
                    }
                    else
                    {
                        Quaternion rest = restRot.TryGetValue(bone, out var r) ? r : bone.localRotation;
                        bone.localRotation = rest * Quaternion.Euler(v.x, -v.y, v.z);
                    }
                    break;
                case "position":
                    if (absolute)
                    {
                        // Bedrock absolute position = FINAL bedrock-space pivot
                        // position (px). Our bind pose already baked the setup
                        // clip's result in, so apply only the DELTA from the
                        // setup constants (sitting "-18 - this" vs setup
                        // "-14 - this" => delta -4 px).
                        Vector3 sp = setupPos.TryGetValue(bone.name.ToLowerInvariant(), out var sv) ? sv : Vector3.zero;
                        Vector3 delta = v - sp;
                        Vector3 rp = restPos.TryGetValue(bone, out var p2) ? p2 : bone.localPosition;
                        bone.localPosition = rp + new Vector3(-delta.x, delta.y, -delta.z) * (1f / 16f);
                    }
                    else
                    {
                        Vector3 rp = restPos.TryGetValue(bone, out var p) ? p : bone.localPosition;
                        // z sign: -v.z (validated by wolf/fox sit + all walk
                        // GIFs). The +v.z experiment fixed ocelot's back paws
                        // but buried the front ones (-0.199m) - neither sign
                        // matches vanilla's planted sit; open issue, see
                        // sitdump_ocelot analysis 2026-09-26.
                        bone.localPosition = rp + new Vector3(-v.x, v.y, -v.z) * (1f / 16f);
                    }
                    break;
                // "scale" intentionally unsupported (boxes are unit-scaled)
            }
        }

        float lifeTime;
        // Engine-driven query state (synthesized): wing flap phase advances
        // while airborne (hopper archetype), head-aim defaults to straight
        // ahead (no target in harness scenes).
        float wingFlapPos, wingFlapSpd = 0.5f;
        /// <summary>Hopper species airborne flag - drives the engine-side
        /// wing_flap_position query (parrot flying state).</summary>
        public bool airborneWings;

        Molang.Ctx BuildCtx(float animTime)
        {
            return new Molang.Ctx
            {
                // query.modified_distance_moved is in VANILLA internal units
                // everywhere (bone expressions AND pre_animation):
                // metres * 14.325. With degree trig (vanilla molang),
                // cos_deg(d*14.325*38.17) = cos_rad(d*9.5425) - exactly the
                // golden-baseline gait frequency (period 0.658 m).
                animTime = animTime,
                distance = distanceMoved * 14.325f,
                headYaw = headYawDeg,
                vars = variables,
                moveSpeed = Mathf.Clamp01(walkSpeedRef),
                deltaTime = 1f / 60f,
                wingFlapPosition = wingFlapPos,
                wingFlapSpeed = wingFlapSpd,
                targetXRotation = 0f,
                targetYRotation = 0f,
                // Engine ground truth: mobs in harness scenes stand on the
                // ground (parrot pre_anim: !is_on_ground -> flying state).
                isOnGround = 1f,
                propertyLookup = molangProperties,
                // String-property defaults (engine role): the registry's
                // BP properties table is the single source (armadillo:
                // minecraft:armadillo_state default "unrolled" from BP
                // description.properties; bedrock-samples v1.21.80.3).
                stringPropertyEq = (name, val) =>
                {
                    var reg = Creatures.CreatureRegistry.Get(species);
                    if (reg != null && reg.properties != null &&
                        reg.properties.TryGetValue(name, out var cur))
                        return cur == val;
                    return val == null;
                },
            };
        }
        /// <summary>Optional property table for query.property('...')
        /// lookups (populated by the controller runtime / game layer).</summary>
        public System.Func<string, float> molangProperties;
        /// <summary>Evaluate one pre_animation assignment line
        /// ("variable.x = expr", "variable.x += expr", ternaries allowed)
        /// and write the result into `variables`. Unknown queries eval to 0
        /// (bedrock default). Non-assignment lines are evaluated for side
        /// effects only (none here) - parse errors are silently ignored like
        /// eyelib's zero-value fallback.</summary>
        void EvalAssign(string line, Molang.Ctx ctx)
        {
            if (string.IsNullOrEmpty(line)) return;
            string t = line.Trim().TrimEnd(';');
            int eq = t.IndexOf('=');
            if (eq <= 0) return;
            // skip ==, !=, <=, >=, +=, -=, *=, /=
            char before = t[eq - 1];
            bool compound = false;
            string op = "=";
            if (before == '+' || before == '-' || before == '*' || before == '/')
            { compound = true; op = before + "="; }
            if (eq > 0 && (before == '=' || before == '!' || before == '<' || before == '>')) return; // comparison, not assignment
            string target = t.Substring(0, eq - (compound ? 1 : 0)).Trim();
            string expr = t.Substring(eq + 1).Trim();
            if (!target.ToLowerInvariant().StartsWith("variable.")) return;
            string key = target.Substring("variable.".Length).ToLowerInvariant();
            float v = Molang.Eval(expr, ctx);
            if (compound)
            {
                float cur = variables.TryGetValue(key, out var c) ? c : 0f;
                if (op == "+=") v = cur + v;
                else if (op == "-=") v = cur - v;
                else if (op == "*=") v = cur * v;
                else if (op == "/=") v = cur != 0f ? cur / v : 0f;
            }
            variables[key] = v;
        }

        // ---------------- Molang-lite ----------------
        public static class Molang
        {
            public struct Ctx
            {
                public float animTime, distance, headYaw, thisVal, moveSpeed, deltaTime;
                // Engine-driven queries (vanilla C++ supplies these; we
                // synthesize): wing flap phase/speed (hoppers), head-aim
                // targets (look_at_target clamps).
                public float wingFlapPosition, wingFlapSpeed, targetXRotation, targetYRotation;
                // B-plan entity queries (controller conditions). Defaults keep
                // old behaviour: unset = 0 == "no". Populated by
                // BedrockControllerRuntime from the entity state.
                public float isBaby, isSitting, isSleeping, isOnGround, isRiding,
                    isJumping, isDancing, hasTarget, isStalking, isInterested,
                    isStunned, isShakingWetness, isResting, isGrazing,
                    sitAmount, lieAmount, rollCounter, allAnimationsFinished,
                    modifiedMoveSpeed;
                internal Dictionary<string, float> vars;
                internal System.Func<string, string, bool> stringPropertyEq;
                internal System.Func<string, float> propertyLookup;
            }

            public static float Eval(string expr, Ctx ctx)
            {
                try
                {
                    var p = new Parser(expr, ctx);
                    float v = p.NullCoalesce();
                    return p.atEnd ? v : 0f;
                }
                catch { return 0f; }
            }

            private sealed class Parser
            {
                private readonly string s;
                private int i;
                private readonly Ctx ctx;

                internal Parser(string e, Ctx c) { s = e.Trim(); i = 0; ctx = c; }
                internal Parser(string e, int start, Ctx c) { s = e; i = start; ctx = c; }
                internal bool atEnd { get { SkipWs(); return i >= s.Length; } }

                private void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
                private bool Peek(char c) { SkipWs(); return i < s.Length && s[i] == c; }
                private bool Eat(char c) { if (Peek(c)) { i++; return true; } return false; }

                // molang null-coalescing "variable.x ?? expr": vars hold
                // floats, so null == UNSET variable. Only this exact pattern
                // appears in vanilla data (armadillo rolled_up_time ?? 0.0).
                internal float NullCoalesce()
                {
                    SkipWs();
                    int save = i;
                    if (i < s.Length && char.IsLetter(s[i]))
                    {
                        int st = i;
                        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                        string tok = s.Substring(st, i - st).ToLowerInvariant();
                        SkipWs();
                        if (i + 1 < s.Length && s[i] == '?' && s[i + 1] == '?' &&
                            tok.StartsWith("variable.") && ctx.vars != null)
                        {
                            string vkey = tok.Substring("variable.".Length);
                            i += 2; // skip ??
                            float rhs = NullCoalesce();
                            if (ctx.vars.ContainsKey(vkey))
                            {
                                var p2 = new Parser(s, save, ctx);
                                return p2.NullCoalesce();
                            }
                            return rhs;
                        }
                        i = save;
                    }
                    return Ternary();
                }

                // molang logical layer: || lowest, && next (below compare).
                // Operands are floats: nonzero = true (armadillo
                // variable.walking = mms > 0.01 && !variable.is_rolled_up).
                internal float AndOr()
                {
                    float v = Compare();
                    while (true)
                    {
                        SkipWs();
                        if (i + 1 < s.Length && s[i] == '&' && s[i + 1] == '&')
                        { i += 2; float r = Compare(); v = (v != 0f && r != 0f) ? 1f : 0f; }
                        else if (i + 1 < s.Length && s[i] == '|' && s[i + 1] == '|')
                        { i += 2; float r = Compare(); v = (v != 0f || r != 0f) ? 1f : 0f; }
                        else return v;
                    }
                }

                internal float Ternary()
                {
                    float cond = AndOr();
                    if (Eat('?'))
                    {
                        float a = Ternary();
                        if (Eat(':')) { float b = Ternary(); return cond != 0f ? a : b; }
                        return cond != 0f ? a : 0f;
                    }
                    return cond;
                }

                private float Compare()
                {
                    float l = Add();
                    string op = null;
                    bool Has2(char c0, char c1)
                    {
                        return i + 1 < s.Length && s[i] == c0 && s[i + 1] == c1;
                    }
                    if (Peek('<')) { op = Has2('<', '=') ? "<=" : "<"; }
                    else if (Peek('>')) { op = Has2('>', '=') ? ">=" : ">"; }
                    else if (Has2('=', '=')) op = "==";
                    else if (Has2('!', '=')) op = "!=";
                    if (op == null) return l;
                    i += op.Length;
                    float r = Add();
                    switch (op)
                    {
                        case "<": return l < r ? 1f : 0f;
                        case ">": return l > r ? 1f : 0f;
                        case "<=": return l <= r ? 1f : 0f;
                        case ">=": return l >= r ? 1f : 0f;
                        case "==": return Mathf.Abs(l - r) < 1e-6f ? 1f : 0f;
                        default: return l != r ? 1f : 0f;
                    }
                }

                private float Add()
                {
                    float v = Mul();
                    while (true)
                    {
                        if (Eat('+')) v += Mul();
                        else if (Eat('-')) v -= Mul();
                        else return v;
                    }
                }

                private float Mul()
                {
                    float v = Unary();
                    while (true)
                    {
                        if (Eat('*')) v *= Unary();
                        else if (Eat('/')) { float d = Unary(); v = d != 0f ? v / d : 0f; }
                        else return v;
                    }
                }

                private float Unary()
                {
                    SkipWs();
                    if (i < s.Length && s[i] == '!' && (i + 1 >= s.Length || s[i + 1] != '='))
                    { i++; float v = Unary(); return v == 0f ? 1f : 0f; }
                    if (Eat('-')) return -Unary();
                    if (Eat('('))
                    {
                        float v = Ternary();
                        Eat(')');
                        return v;
                    }
                    if (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) return Number();
                    return Ident();
                }

                private float Number()
                {
                    int st = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    return float.Parse(s.Substring(st, i - st),
                        CultureInfo.InvariantCulture);
                }

                private float Ident()
                {
                    SkipWs();
                    int st = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                    string id = s.Substring(st, i - st);
                    // Vanilla json mixes Math.* and math.* (goat pre_anim uses
                    // Math.cos, controllers use math.min) - normalize to lower
                    // BEFORE dispatch; string-literal args keep raw case.
                    string idLower = id.ToLowerInvariant();
                    string rawArg = null;
                    bool isCall = Peek('(');

                    if (isCall)
                    {
                        i++; // '('
                        // string-literal first arg (query.property('minecraft:...'))
                        SkipWs();
                        if (i < s.Length && (s[i] == '\'' || s[i] == '"'))
                        {
                            char q = s[i++];
                            int qs = i;
                            while (i < s.Length && s[i] != q) i++;
                            rawArg = s.Substring(qs, i - qs);
                            i++; // closing quote
                        }
                        // string-literal SECOND arg: property_eq('key','val')
                        // compares an entity string property for equality
                        // (armadillo_state == 'unrolled' family). arg2 raw.
                        string rawArg2 = null;
                        if (Eat(','))
                        {
                            SkipWs();
                            char qc = i < s.Length ? s[i] : char.MinValue;
                            if (qc == 39 || qc == 34) // ' or "
                            {
                                i++; int q2s = i;
                                while (i < s.Length && s[i] != qc) i++;
                                rawArg2 = s.Substring(q2s, i - q2s); i++;
                                Eat(')');
                            }
                            else { i--; /* restore for b path */ }
                        }
                        if (idLower == "property_eq" && rawArg != null && rawArg2 != null)
                            return ctx.stringPropertyEq != null && ctx.stringPropertyEq(rawArg, rawArg2) ? 1f : 0f;
                        // Args are parsed BEFORE the closing paren; 3-arg
                        // funcs (clamp/lerp/random_integer) read arg3 here -
                        // reading it after Eat(')') runs off the expression
                        // end and the whole Eval catch-drops to 0 (armadillo
                        // walk weight min(1.4,lerp(0.2,2.4,mms)) hit this).
                        float a = Ternary();
                        float b = 0f;
                        float c = 0f;
                        if (Eat(',')) { b = Ternary(); if (Eat(',')) c = Ternary(); }
                        Eat(')');
                        switch (idLower)
                        {
                            // vanilla molang trig takes DEGREES (eyelib
                            // MolangMath.java cos/sin wrap Deg2Rad; parrot
                            // dance.x = cos(life_time*57.3*20) confirms).
                            case "math.cos": return Mathf.Cos(a * Mathf.Deg2Rad);
                            case "math.sin": return Mathf.Sin(a * Mathf.Deg2Rad);
                            case "math.abs": return Mathf.Abs(a);
                            case "math.mod": return b != 0f ? a - b * Mathf.Floor(a / b) : 0f;
                            case "math.clamp": return Mathf.Clamp(a, b, c);
                            case "math.min": return Mathf.Min(a, b);
                            case "math.max": return Mathf.Max(a, b);
                            case "math.lerp": return Mathf.Lerp(a, b, c);
                            case "math.floor": return Mathf.Floor(a);
                            case "math.ceil": return Mathf.Ceil(a);
                            case "math.sqrt": return Mathf.Sqrt(a);
                            case "math.pow": return Mathf.Pow(a, b);
                            case "math.random": return Random.Range(a, b);
                            case "query.property":
                                // rawArg holds the quoted property name
                                return ctx.propertyLookup != null && rawArg != null ? ctx.propertyLookup(rawArg) : 0f;
                            default: return 0f;
                        }
                    }
                    switch (idLower)
                    {
                        case "query.anim_time": return ctx.animTime;
                        case "query.delta_time": return ctx.deltaTime;
                        case "query.wing_flap_position": return ctx.wingFlapPosition;
                        case "query.wing_flap_speed": return ctx.wingFlapSpeed;
                        case "query.target_x_rotation": return ctx.targetXRotation;
                        case "query.target_y_rotation": return ctx.targetYRotation;
                        case "query.modified_distance_moved": return ctx.distance;
                        case "query.modified_move_speed": return ctx.modifiedMoveSpeed != 0f ? ctx.modifiedMoveSpeed : ctx.moveSpeed;
                        case "query.head_yaw": return ctx.headYaw;
                        // B-plan entity state queries (controller conditions).
                        case "query.is_baby": return ctx.isBaby;
                        case "query.is_sitting": return ctx.isSitting;
                        case "query.is_sleeping": return ctx.isSleeping;
                        case "query.is_on_ground": return ctx.isOnGround;
                        case "query.is_riding": return ctx.isRiding;
                        case "query.is_jumping": return ctx.isJumping;
                        case "query.is_dancing": return ctx.isDancing;
                        case "query.has_target": return ctx.hasTarget;
                        case "query.is_stalking": return ctx.isStalking;
                        case "query.is_interested": return ctx.isInterested;
                        case "query.is_stunned": return ctx.isStunned;
                        case "query.is_shaking_wetness": return ctx.isShakingWetness;
                        case "query.is_resting": return ctx.isResting;
                        case "query.is_grazing": return ctx.isGrazing;
                        case "query.sit_amount": return ctx.sitAmount;
                        case "query.lie_amount": return ctx.lieAmount;
                        case "query.roll_counter": return ctx.rollCounter;
                        case "query.all_animations_finished": return ctx.allAnimationsFinished;
                        case "query.life_time": return ctx.animTime;
                        case "this": return ctx.thisVal;
                        case "query.key_frame_lerp_time": return 0f; // lerp phase; grazing head wiggle approximated as constant
                        case "true": return 1f;
                        case "false": return 0f;
                    }
                    if (idLower.StartsWith("variable.") && ctx.vars != null)
                    {
                        // vars keys are stored lowercase (pre_animation writes
                        // variable.X; Molang names are case-insensitive).
                        string key = idLower.Substring("variable.".Length);
                        return ctx.vars.TryGetValue(key, out var v) ? v : 0f;
                    }
                    return 0f; // unknown -> 0
                }
            }
        }
    }
}

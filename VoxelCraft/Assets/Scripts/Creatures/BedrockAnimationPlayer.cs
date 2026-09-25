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
    /// [rx,ry,rz] -> Unity Euler(-rx,-ry,-rz) applied AFTER the bone's rest
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
            public List<Keyframe> frames = new List<Keyframe>();
        }

        public class Clip
        {
            public string name;
            public float length = -1f;    // -1 = derive from max keyframe time
            public bool loop = true;
            public bool timeFromDistance; // anim_time_update = modified_distance_moved
            public List<Track> tracks = new List<Track>();
        }

        private class Playing
        {
            internal Clip clip;
            internal float time;
            internal bool absolute; // setup/sitting-style: values are targets
        }

        // ---------------- public API ----------------
        [Tooltip("Bedrock animation JSONs (resource_pack/animations)")]
        public List<TextAsset> clipsJson = new List<TextAsset>();
        public readonly Dictionary<string, Clip> clips = new Dictionary<string, Clip>();
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

        /// <summary>Goat: compute tcos gait vars from the entity-layer
        /// pre_animation script (goat.entity.json) each tick.</summary>
        public bool goatGait;

        /// <summary>Locomotion speed (m/s) feeding gait variable math.</summary>
        public float walkSpeedRef = 1.4f;

        /// <summary>0 = rest pose, 1 = full gait. Set by the AI each frame;
        /// idle animals return their legs to the rest pose instead of
        /// freezing mid-swing (vanilla lerps limbSwing the same way).</summary>
        public float gaitWeight = 1f;

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
        }

        /// <summary>Start a clip; no-op if it is already playing.</summary>
        public int ClipCount => clips.Count;

        public bool Play(string clipName)
        {
            return Play(clipName, false);
        }

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
            for (int i = 0; i < playing.Count; i++)
                if (playing[i].clip == clip) return true;
            playing.Add(new Playing { clip = clip, time = 0f, absolute = absolute });
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
            playing.Clear();
            foreach (var kv in restPos) kv.Key.localPosition = kv.Value;
            foreach (var kv in restRot) kv.Key.localRotation = kv.Value;
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
                if (a.TryGetValue("loop", out object lp) && lp is bool lb) clip.loop = lb;
                if (a.TryGetValue("anim_time_update", out object atu) &&
                    atu is string atus && atus.Contains("modified_distance_moved"))
                    clip.timeFromDistance = true;

                if (!(a.TryGetValue("bones", out object bv) &&
                      bv is Dictionary<string, object> bones)) continue;
                foreach (var bk in bones)
                {
                    if (!(bk.Value is Dictionary<string, object> bd)) continue;
                    foreach (var ch in bd)
                    {
                        if (ch.Key != "position" && ch.Key != "rotation" && ch.Key != "scale") continue;
                        var track = new Track { bone = bk.Key, channel = ch.Key };
                        ParseChannel(track, ch.Value);
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
            for (int i = playing.Count - 1; i >= 0; i--)
            {
                var p = playing[i];
                var c = p.clip;
                float maxT = c.length > 0f ? c.length : MaxTrackTime(c);
                if (c.timeFromDistance)
                    // Bedrock distance is in blocks with a gait period of
                    // ~0.66 m per full swing; our distanceMoved is metres at
                    // game scale, so scale to keep the vanilla step cadence
                    // (raw 38.17 rad/m is ~8 Hz at 1.4 m/s - comically fast).
                    p.time = distanceMoved * 0.25f;
                else p.time += dt;
                if (maxT > 0f && p.time > maxT)
                {
                    if (c.loop) p.time = p.time % maxT;
                    else if (!p.absolute) { playing.RemoveAt(i); continue; }
                    else p.time = maxT; // hold final pose
                }
                Sample(c, p.time, p.absolute);
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

        private void Sample(Clip c, float time, bool absolute = false)
        {
            float w = absolute ? 1f : Mathf.Clamp01(gaitWeight);
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
                    v = kf.expr != null ? EvalExpr(kf.expr, bone, tr.channel, time)
                                        : kf.post;
                }
                else v = SampleKeyframes(tr, time);
                if (!absolute && w < 1f)
                {
                    // Blend toward the BIND pose captured at Bind() time - a
                    // fixed target. Blending toward the bone's CURRENT value
                    // (previous frame's output) is a feedback loop: the pose
                    // never settles and the legs visibly jitter when idle.
                    Vector3 rest = RestValue(bone, tr.channel);
                    v = Vector3.Lerp(rest, v, w);
                }
                Apply(bone, tr.channel, v, absolute);
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

        private Vector3 EvalExpr(string[] exprs, Transform bone, string channel, float time)
        {
            var ctx = new Molang.Ctx { animTime = time, distance = distanceMoved, headYaw = headYawDeg, vars = variables };
            var v = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                if (exprs[i] == null) continue;
                ctx.thisVal = ThisValue(bone, channel, i);
                v[i] = Molang.Eval(exprs[i], ctx);
            }
            return v;
        }

        private Vector3 SampleKeyframes(Track tr, float t)
        {
            var f = tr.frames;
            if (t <= f[0].time) return EvalKf(f[0], t);
            if (t >= f[f.Count - 1].time) return EvalKf(f[f.Count - 1], t);
            for (int i = 0; i < f.Count - 1; i++)
            {
                if (t >= f[i].time && t <= f[i + 1].time)
                {
                    float u = (t - f[i].time) / Mathf.Max(1e-5f, f[i + 1].time - f[i].time);
                    return Vector3.Lerp(EvalKf(f[i], t), EvalKf(f[i + 1], t), u);
                }
            }
            return EvalKf(f[f.Count - 1], t);
        }

        private Vector3 EvalKf(Keyframe kf, float time)
        {
            if (kf.expr == null) return kf.post;
            var v = kf.post;
            var ctx = new Molang.Ctx { animTime = time, distance = distanceMoved, headYaw = headYawDeg, vars = variables };
            for (int i = 0; i < 3; i++)
                if (kf.expr[i] != null) v[i] = Molang.Eval(kf.expr[i], ctx);
            return v;
        }

        private float ThisValue(Transform bone, string channel, int comp)
        {
            if (channel != "rotation") return 0f;
            if (!restRot.TryGetValue(bone, out var rest)) return 0f;
            Quaternion delta = Quaternion.Inverse(rest) * bone.localRotation;
            return delta.eulerAngles[comp] > 180f ? delta.eulerAngles[comp] - 360f : delta.eulerAngles[comp];
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
                        // rest * Euler(-(v_final - bindAngle)) where bindAngle
                        // is the bedrock angle baked into rest.
                        Quaternion rest = restRot.TryGetValue(bone, out var rr) ? rr : bone.localRotation;
                        Vector3 re = rest.eulerAngles;
                        Vector3 bindBedrock = new Vector3(
                            re.x > 180f ? re.x - 360f : re.x,
                            -(re.y > 180f ? re.y - 360f : re.y),
                            -(re.z > 180f ? re.z - 360f : re.z));
                        // importer convention: rest = Euler(-bprX, +bprY?, ...) —
                        // verify: pig torso bpr [90,0,0] -> rest Euler(-90,0,0),
                        // so bedrock angle = -restEuler per axis (y/z negated
                        // for bedrock->unity axis flip).
                        Vector3 target = v - bindBedrock;
                        bone.localRotation = rest * Quaternion.Euler(-target.x, -target.y, -target.z);
                    }
                    else
                    {
                        Quaternion rest = restRot.TryGetValue(bone, out var r) ? r : bone.localRotation;
                        bone.localRotation = rest * Quaternion.Euler(-v.x, -v.y, -v.z);
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
                        bone.localPosition = rp + new Vector3(-v.x, v.y, -v.z) * (1f / 16f);
                    }
                    break;
                // "scale" intentionally unsupported (boxes are unit-scaled)
            }
        }

        // ---------------- Molang-lite ----------------
        public static class Molang
        {
            public struct Ctx
            {
                public float animTime, distance, headYaw, thisVal;
                internal Dictionary<string, float> vars;
            }

            public static float Eval(string expr, Ctx ctx)
            {
                try
                {
                    var p = new Parser(expr, ctx);
                    float v = p.Ternary();
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
                internal bool atEnd { get { SkipWs(); return i >= s.Length; } }

                private void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
                private bool Peek(char c) { SkipWs(); return i < s.Length && s[i] == c; }
                private bool Eat(char c) { if (Peek(c)) { i++; return true; } return false; }

                internal float Ternary()
                {
                    float cond = Compare();
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
                    bool isCall = Peek('(');

                    if (isCall)
                    {
                        i++; // '('
                        float a = Ternary();
                        float b = 0f;
                        if (Eat(',')) b = Ternary();
                        Eat(')');
                        switch (id)
                        {
                            case "math.cos": return Mathf.Cos(a);
                            case "math.sin": return Mathf.Sin(a);
                            case "math.abs": return Mathf.Abs(a);
                            case "math.mod": return b != 0f ? a - b * Mathf.Floor(a / b) : 0f;
                            case "math.clamp": { float c = Ternary(); return Mathf.Clamp(a, b, c); }
                            default: return 0f;
                        }
                    }
                    switch (id)
                    {
                        case "query.anim_time": return ctx.animTime;
                        case "query.modified_distance_moved": return ctx.distance;
                        case "query.head_yaw": return ctx.headYaw;
                        case "query.life_time": return ctx.animTime;
                        case "this": return ctx.thisVal;
                        case "query.is_baby": return 0f; // no baby variants
                        case "query.key_frame_lerp_time": return 0f; // lerp phase; grazing head wiggle approximated as constant
                        case "true": return 1f;
                        case "false": return 0f;
                    }
                    if (id.StartsWith("variable.") && ctx.vars != null)
                    {
                        string key = id.Substring("variable.".Length);
                        return ctx.vars.TryGetValue(key, out var v) ? v : 0f;
                    }
                    return 0f; // unknown -> 0
                }
            }
        }
    }
}

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

        /// <summary>Set externally (e.g. by look-at AI) in degrees.</summary>
        public float headYawDeg;

        public void LoadClips()
        {
            clips.Clear();
            foreach (var ta in clipsJson)
            {
                if (ta == null) continue;
                ParseFile(ta.text);
            }
        }

        public void Bind(Transform modelRoot)
        {
            boneIndex.Clear();
            restPos.Clear();
            restRot.Clear();
            IndexRec(modelRoot);
        }

        /// <summary>Start a clip; no-op if it is already playing.</summary>
        public bool Play(string clipName)
        {
            if (!clips.TryGetValue(clipName, out var clip)) return false;
            for (int i = 0; i < playing.Count; i++)
                if (playing[i].clip == clip) return true;
            playing.Add(new Playing { clip = clip, time = 0f });
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
        private void IndexRec(Transform t)
        {
            if (!boneIndex.ContainsKey(t.name))
            {
                boneIndex[t.name] = t;
                restPos[t] = t.localPosition;
                restRot[t] = t.localRotation;
            }
            foreach (Transform c in t) IndexRec(c);
        }

        private void Update()
        {
            Vector3 pos = transform.position;
            if (hasPrevPos) distanceMoved += Vector3.Distance(pos, prevPos);
            prevPos = pos; hasPrevPos = true;
            Tick();
        }

        /// <summary>Advance clocks and apply poses (exposed for tests).</summary>
        public void Tick()
        {
            float dt = Time.deltaTime;
            for (int i = playing.Count - 1; i >= 0; i--)
            {
                var p = playing[i];
                var c = p.clip;
                float maxT = c.length > 0f ? c.length : MaxTrackTime(c);
                if (c.timeFromDistance) p.time = distanceMoved;
                else p.time += dt;
                if (maxT > 0f && p.time > maxT)
                {
                    if (c.loop) p.time = p.time % maxT;
                    else { playing.RemoveAt(i); continue; }
                }
                Sample(c, p.time);
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

        private void Sample(Clip c, float time)
        {
            foreach (var tr in c.tracks)
            {
                if (!boneIndex.TryGetValue(tr.bone, out var bone)) continue;
                Vector3 v;
                if (tr.frames.Count == 1)
                {
                    var kf = tr.frames[0];
                    v = kf.expr != null ? EvalExpr(kf.expr, bone, tr.channel, time)
                                        : kf.post;
                }
                else v = SampleKeyframes(tr, time);
                Apply(bone, tr.channel, v);
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

        private void Apply(Transform bone, string channel, Vector3 v)
        {
            switch (channel)
            {
                case "rotation":
                    Quaternion rest = restRot.TryGetValue(bone, out var r) ? r : bone.localRotation;
                    bone.localRotation = rest * Quaternion.Euler(-v.x, -v.y, -v.z);
                    break;
                case "position":
                    Vector3 rp = restPos.TryGetValue(bone, out var p) ? p : bone.localPosition;
                    bone.localPosition = rp + new Vector3(-v.x, v.y, -v.z) * (1f / 16f);
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

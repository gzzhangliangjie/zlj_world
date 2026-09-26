using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// Minimal recursive-descent JSON parser (object/array/string/number/
    /// bool/null -> Dictionary&lt;string,object&gt;/List&lt;object&gt;/string/double/bool/
    /// null). Used by BedrockGeoImporter; JsonUtility cannot handle bedrock
    /// geo files (dynamic keys, mixed arrays).
    /// </summary>
    public static class MiniJson
    {
        public static object Deserialize(string json)
        {
            int pos = 0;
            try
            {
                return ParseValue(json, ref pos);
            }
            catch
            {
                return null;
            }
        }

        private static object ParseValue(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) return null;
            char c = s[pos];
            switch (c)
            {
                case '{': return ParseObject(s, ref pos);
                case '[': return ParseArray(s, ref pos);
                case '"': return ParseString(s, ref pos);
                case 't': pos += 4; return true;
                case 'f': pos += 5; return false;
                case 'n': pos += 4; return null;
                default: return ParseNumber(s, ref pos);
            }
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r')) pos++;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // {
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return dict; }
            while (pos < s.Length)
            {
                SkipWs(s, ref pos);
                string key = ParseString(s, ref pos);
                SkipWs(s, ref pos);
                pos++; // :
                dict[key] = ParseValue(s, ref pos);
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == '}') { pos++; break; }
                if (pos >= s.Length) break;
            }
            return dict;
        }

        private static List<object> ParseArray(string s, ref int pos)
        {
            var list = new List<object>();
            pos++; // [
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return list; }
            while (pos < s.Length)
            {
                list.Add(ParseValue(s, ref pos));
                SkipWs(s, ref pos);
                if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                if (pos < s.Length && s[pos] == ']') { pos++; break; }
                if (pos >= s.Length) break;
            }
            return list;
        }

        private static string ParseString(string s, ref int pos)
        {
            pos++; // opening quote
            var sb = new StringBuilder();
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    char e = s[pos + 1];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'u':
                            if (pos + 5 < s.Length)
                            {
                                sb.Append((char)int.Parse(s.Substring(pos + 2, 4), NumberStyles.HexNumber));
                                pos += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    pos += 2;
                }
                else
                {
                    sb.Append(s[pos]);
                    pos++;
                }
            }
            pos++; // closing quote
            return sb.ToString();
        }

        private static object ParseNumber(string s, ref int pos)
        {
            int start = pos;
            while (pos < s.Length && (char.IsDigit(s[pos]) || s[pos] == '-' || s[pos] == '+' ||
                s[pos] == '.' || s[pos] == 'e' || s[pos] == 'E')) pos++;
            if (double.TryParse(s.Substring(start, pos - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double d)) return d;
            return null;
        }
    }

    /// <summary>
    /// Generic bedrock-samples .geo.json importer: parses bones (name, parent,
    /// pivot, rotation) and cubes (origin, size, uv, rotation) and instantiates
    /// them as a Unity transform tree of SkinnedBox meshes. The JSON IS the
    /// vanilla model source, so geometry lands exactly where Mojang put it -
    /// no per-species hand tuning (which failed for wolf's deep chest and
    /// fox's pillar body). Handles both 1.8 dict format (geometry.X at top
    /// level, texturewidth/textureheight keys) and 1.12+ list format.
    ///
    /// Coordinates: bedrock -Z is the model front; our animals face +Z, so z
    /// is flipped and x flipped with it (keeps handedness/winding). Units:
    /// texture px; 16 px = 1 m. v runs top-down in the sheet. Bedrock bone
    /// and cube rotations are clockwise-positive degrees; Unity euler is
    /// counter-clockwise, so signs are negated per axis.
    /// </summary>
    public static class BedrockGeoImporter
    {
        private const float Px = 1f / 16f;

        /// <summary>Builds the geo model under <paramref name="parent"/>. Bones named
        /// leg0..leg3 -> legsOut, tail -> tailOut, head -> headOut, wing* -> wingsOut.
        /// <paramref name="bodyPitchDeg"/> tips the "body" bone (and its children)
        /// about X for geos whose rest pose is a vertical pillar (fox: -45).</summary>
        public static bool Build(Transform parent, TextAsset geoJson, string geometryId,
            Material skin, float bodyPitchDeg, TextAsset setupAnimJson,
            out Transform[] legsOut, out Transform tailOut,
            out Transform headOut, out Transform[] wingsOut)
        {
            return Build(parent, geoJson, geometryId, skin, bodyPitchDeg, setupAnimJson,
                out legsOut, out tailOut, out headOut, out wingsOut, null);
        }

        public static bool Build(Transform parent, TextAsset geoJson, string geometryId,
            Material skin, float bodyPitchDeg,
            out Transform[] legsOut, out Transform tailOut,
            out Transform headOut, out Transform[] wingsOut)
        {
            return Build(parent, geoJson, geometryId, skin, bodyPitchDeg, null,
                out legsOut, out tailOut, out headOut, out wingsOut, null);
        }

        /// <summary>Full build: imports the geo, then bakes the species'
        /// ".setup" animation clip (bedrock runtime applies it permanently).
        /// Bake semantics derived from vanilla data: position target "c - this"
        /// is the final bedrock pivot; our model space already equals the
        /// setup-applied frame, so we apply only the DELTA from the geo pivot,
        /// skipping the uniform y-shift (v1.8 coordinate convention) when every
        /// bone shares it. Rotation targets tilt the bone from its geo rest.</summary>
        public static bool Build(Transform parent, TextAsset geoJson, string geometryId,
            Material skin, float bodyPitchDeg, TextAsset setupAnimJson,
            out Transform[] legsOut, out Transform tailOut,
            out Transform headOut, out Transform[] wingsOut,
            Transform geoRootOut)
        {
            legsOut = new Transform[4];
            wingsOut = new Transform[2];
            tailOut = null;
            headOut = null;
            if (geoJson == null || skin == null) return false;

            var wrap = MiniJson.Deserialize(geoJson.text) as Dictionary<string, object>;
            if (wrap == null) return false;

            List<object> bones = null;
            int texW = 64, texH = 32;

            if (wrap.TryGetValue("minecraft:geometry", out object mg) && mg is List<object> geos)
            {
                // 1.12+ list format.
                Dictionary<string, object> chosen = null;
                foreach (var g in geos)
                {
                    if (g is Dictionary<string, object> geo &&
                        geo["description"] is Dictionary<string, object> desc &&
                        (string.IsNullOrEmpty(geometryId) || (string)desc["identifier"] == geometryId))
                    {
                        chosen = geo;
                        break;
                    }
                }
                if (chosen == null) return false;
                var d = (Dictionary<string, object>)chosen["description"];
                texW = (int)(double)d["texture_width"];
                texH = (int)(double)d["texture_height"];
                bones = (List<object>)chosen["bones"];
            }
            else
            {
                // 1.8 dict format: top-level "geometry.X" keys.
                foreach (var kv in wrap)
                {
                    if (!kv.Key.StartsWith("geometry.") || !(kv.Value is Dictionary<string, object> geo)) continue;
                    string id = kv.Key;
                    if (!string.IsNullOrEmpty(geometryId) && id != geometryId) continue;
                    if (geo.TryGetValue("texturewidth", out object tw)) texW = (int)(double)tw;
                    if (geo.TryGetValue("textureheight", out object th)) texH = (int)(double)th;
                    if (geo.TryGetValue("bones", out object bl)) bones = (List<object>)bl;
                    // Multi-geometry files (sheep: sheared 64x32 + fleeced
                    // 64x64): pick the variant matching the skin's aspect.
                    if (!string.IsNullOrEmpty(geometryId) &&
                        skin.mainTexture != null && skin.mainTexture.height != texH)
                        continue; // keep scanning for the matching variant
                    break;
                }
            }
            if (bones == null) return false;

            // 1.8 mobs.json (humanoid.custom) declares NO texturewidth/
            // textureheight - the default 64x32 halves every v coordinate on
            // a 64x64 skin (steve: face sampled the torso band). Fall back to
            // the actual skin dimensions when the geo is silent.
            if (skin != null && skin.mainTexture != null)
            {
                if (texW == 64 && texH == 32 &&
                    (skin.mainTexture.width != 64 || skin.mainTexture.height != 32))
                {
                    texW = skin.mainTexture.width;
                    texH = skin.mainTexture.height;
                }
            }

            var root = new GameObject("GeoRoot").transform;
            root.SetParent(parent, false);

            // Pass 1: create a transform per bone at its pivot.
            var byName = new Dictionary<string, Transform>();
            var boneMeta = new Dictionary<string, Dictionary<string, object>>();
            var boneNameToOriginal = new Dictionary<string, string>();
            var boneBind = new Dictionary<string, Vector3>();
            foreach (var b in bones)
            {
                var bone = (Dictionary<string, object>)b;
                string name = (string)bone["name"];
                var go = new GameObject(SafeName(name));
                var t = go.transform;
                byName[name] = t;
                boneNameToOriginal[SafeName(name)] = name;
                boneMeta[name] = bone;
            }

            // Pass 2: parent bones + set pivots (bedrock pivot coords are
            // model-space positions of the bone origin). localPos is derived
            // from model-space pivots so rotated parents still place bones
            // exactly at their model-space pivot.
            // Split into 2a/2b/2c because FILE ORDER IS NOT PARENT-FIRST:
            // legacy 1.8 fox geo lists head BEFORE its parent body, so the
            // old single loop fell back to ABSOLUTE placement for head
            // (floating half a unit up - the fox/sit two-domain split).
            var modelPos = new Dictionary<string, Vector3>();
            // 2a: collect model-space pivots + bind rotations for ALL bones.
            foreach (var kv in boneMeta)
            {
                Vector3 pos = Vector3.zero;
                if (kv.Value.TryGetValue("pivot", out object pv) && pv is List<object> pl && pl.Count == 3)
                {
                    pos = new Vector3(-ToFloat(pl[0]) * Px, ToFloat(pl[1]) * Px, -ToFloat(pl[2]) * Px);
                }
                modelPos[kv.Key] = pos;
                if (kv.Value.TryGetValue("bind_pose_rotation", out object bpv) && bpv is List<object> bpl && bpl.Count == 3)
                {
                    boneBind[kv.Key] = new Vector3(ToFloat(bpl[0]), ToFloat(bpl[1]), ToFloat(bpl[2]));
                }
            }
            // 2b: build the hierarchy + explicit rest rotations (children in
            // 2c must read the FINAL parent rotation when rebasing).
            foreach (var kv in boneMeta)
            {
                var bone = kv.Value;
                var t = byName[kv.Key];
                string parentName = bone.TryGetValue("parent", out object p) && p is string ps ? ps : null;
                Transform pt = parentName != null && byName.TryGetValue(parentName, out var ptFound) ? ptFound : null;
                t.SetParent(pt != null ? pt : root, false);
                // 1.8 files store the torso UPRIGHT and tip it horizontal via
                // bind_pose_rotation. Semantics per bedrock runtime: the bind
                // rotation reorients THIS BONE'S OWN CUBES around its pivot
                // but does NOT rotate child bones - child pivots are already
                // authored in the final (lying-down) frame. We therefore keep
                // bone localRotation identity here and rotate the bone's cubes
                // individually in pass 3 (see bindRot lookup). Explicit
                // "rotation" is an animation-time rest offset and still
                // applies to the transform.
                if (bone.TryGetValue("rotation", out object rv) && rv is List<object> rl && rl.Count == 3)
                {
                    t.localRotation = Quaternion.Euler(
                        -ToFloat(rl[0]), -ToFloat(rl[1]), -ToFloat(rl[2]));
                }
            }
            // 2c: rebase pivots - every parent's pivot AND rotation is known,
            // so file order no longer matters.
            foreach (var kv in boneMeta)
            {
                string name = kv.Key;
                string parentName = kv.Value.TryGetValue("parent", out object p2) && p2 is string ps2 ? ps2 : null;
                Transform pt = parentName != null && byName.TryGetValue(parentName, out var ptFound2) ? ptFound2 : null;
                var t = byName[name];
                if (pt != null && modelPos.TryGetValue(parentName, out var pp))
                {
                    t.localPosition = Quaternion.Inverse(pt.localRotation) * (modelPos[name] - pp);
                }
                else
                {
                    t.localPosition = modelPos[name];
                }
            }

            // Pass 3: cubes. Each cube becomes a SkinnedBox parented to its
            // bone. Bedrock cube origin = model-space corner (min-x, min-y,
            // max-z in our flipped frame); box centre = origin + size/2.
            foreach (var kv in boneMeta)
            {
                string name = kv.Key;
                if (!kv.Value.TryGetValue("cubes", out object cv) || !(cv is List<object> cubes)) continue;
                var bone = byName[name];
                bool boneMirror = kv.Value.TryGetValue("mirror", out object mv) && mv is bool mb && mb;
                // Accumulated parent scale on this bone is 1 (we never scale
                // bones), so world-space sizes pass straight through.
                foreach (var c in cubes)
                {
                    var cube = (Dictionary<string, object>)c;
                    var origin = (List<object>)cube["origin"];
                    var size = (List<object>)cube["size"];
                    var uv = cube.TryGetValue("uv", out object uvl) ? (List<object>)uvl : null;
                    int u = uv != null ? (int)ToFloat(uv[0]) : 0;
                    int v = uv != null ? (int)ToFloat(uv[1]) : 0;
                    float sx = ToFloat(size[0]), sy = ToFloat(size[1]), sz = ToFloat(size[2]);
                    int W = (int)sx, H = (int)sy, D = (int)sz;
                    // Zero-thickness plates (bee wing 9x0x6, legs 7x2x0):
                    // clamp to 1 px so the UV rect doesn't collapse (a
                    // degenerate u0==u1 rect samples a 1-texel line).
                    if (W < 1) W = 1; if (H < 1) H = 1; if (D < 1) D = 1;

                    // Flipped frame: x' = -x, z' = -z. Bedrock origin is the
                    // (-x, +y-bottom, -z-front) corner; after flipping x/z the
                    // cube centre in model space:
                    float cx = -(ToFloat(origin[0]) + sx * 0.5f) * Px;
                    float cy = (ToFloat(origin[1]) + sy * 0.5f) * Px;
                    float cz = -(ToFloat(origin[2]) + sz * 0.5f) * Px;
                    Vector3 centre = new Vector3(cx, cy, cz);

                    // 1.8 bind_pose_rotation (e.g. pig torso [90,0,0]): the
                    // cube is AUTHORED in the upright frame and must be spun
                    // around the bone pivot into the final pose. Children
                    // bones are NOT affected (their pivots are already final).
                    if (boneBind.TryGetValue(name, out Vector3 bindDeg))
                    {
                        // SIGN PINNED NUMERICALLY (bpr_sign_unity.py):
                        // bedrock-frame ground truth for fox body bpr[90,0,0]
                        // is Rx(-90) @pivot[0,8,0] -> body z -3..+8 (front
                        // edge meets head back at z=-3, covers tail pivot
                        // z=+7 and both leg rows z=-1..6; tail Rx(-80)
                        // sweeps back z +7.6..17.3). Conjugating through the
                        // frame flip F=diag(-1,1,-1): F*Rx(t)*F = Rx(-t), so
                        // bedrock Rx(-90) == Unity Euler(+90) == Euler(+bpr.x)
                        // (F preserves Y sign, flips Z). The anim convention
                        // (bx,-by,bz) does NOT apply here - it has no frame
                        // conjugation. Empirical: gif12 walk 40/40 with +;
                        // SV8 with - (vs orientation + below) = 45deg legs.
                        Quaternion bindRot = Quaternion.Euler(bindDeg.x, bindDeg.y, bindDeg.z);
                        Vector3 pivot = modelPos[name];
                        // Rotate around the bone pivot in model space.
                        centre = pivot + bindRot * (centre - pivot);
                    }

                    // local position relative to the bone pivot, in the bone's
                    // parent frame (model-space delta rotated into local space)
                    Vector3 local = centre - modelPos[name];
                    if (bone.parent != null && bone.parent != root)
                        local = Quaternion.Inverse(bone.parent.localRotation) * local;

                    Vector4[] net = BuildNet(u, v, W, H, D, boneMirror);
                    var box = Art.BoxBuilder.SkinnedBox(bone, "cube_" + W + "x" + H + "x" + D,
                        local, new Vector3(Mathf.Max(sx, 0.5f) * Px, Mathf.Max(sy, 0.5f) * Px, Mathf.Max(sz, 0.5f) * Px), skin, net, texW, texH,
                        null, boneMirror ? MirrorFlips : null);
                    // A bind-rotated cube also changes ORIENTATION (upright
                    // pillar -> horizontal torso), not just position.
                    if (boneBind.TryGetValue(name, out Vector3 bd2))
                        box.transform.localRotation = Quaternion.Euler(bd2.x, bd2.y, bd2.z);

                    // Cube-local rotation (about cube origin) if present.
                    if (cube.TryGetValue("rotation", out object cro) && cro is List<object> crl && crl.Count == 3)
                    {
                        var pivotGo = new GameObject("CubePivot");
                        pivotGo.transform.SetParent(bone, false);
                        pivotGo.transform.localPosition = local;
                        pivotGo.transform.localRotation = Quaternion.Euler(
                            -ToFloat(crl[0]), -ToFloat(crl[1]), -ToFloat(crl[2]));
                        box.transform.SetParent(pivotGo.transform, false);
                        box.transform.localPosition = Vector3.zero;
                    }
                }
            }

            // Expose animation handles by bedrock naming convention.
            for (int i = 0; i < 4; i++)
                if (byName.TryGetValue("leg" + i, out var lt) && legsOut[i] == null) legsOut[i] = lt;
            // humanoid (steve): leftLeg/rightLeg; map [0]=left [1]=right
            if (byName.TryGetValue("leftLeg", out var sll)) legsOut[0] = sll;
            if (byName.TryGetValue("rightLeg", out var slr)) legsOut[1] = slr;
            // goat uses named legs; map them too
            if (byName.TryGetValue("left_front_leg", out var lfl)) legsOut[0] = lfl;
            if (byName.TryGetValue("right_front_leg", out var rfl)) legsOut[1] = rfl;
            if (byName.TryGetValue("left_back_leg", out var lbl)) legsOut[2] = lbl;
            if (byName.TryGetValue("right_back_leg", out var rbl)) legsOut[3] = rbl;
            // ocelot (1.8 geo) uses backLegL/R + frontLegL/R
            if (byName.TryGetValue("frontLegL", out var fll)) legsOut[0] = fll;
            if (byName.TryGetValue("frontLegR", out var flr)) legsOut[1] = flr;
            if (byName.TryGetValue("backLegL", out var bll)) legsOut[2] = bll;
            if (byName.TryGetValue("backLegR", out var blr)) legsOut[3] = blr;
            // horse v3 geo: LegBL/LegBR (back) + LegFL/LegFR (front)
            if (byName.TryGetValue("LegFL", out var hfl)) legsOut[0] = hfl;
            if (byName.TryGetValue("LegFR", out var hfr)) legsOut[1] = hfr;
            if (byName.TryGetValue("LegBL", out var hbl)) legsOut[2] = hbl;
            if (byName.TryGetValue("LegBR", out var hbr)) legsOut[3] = hbr;
            if (byName.TryGetValue("right_front_leg", out var rf2)) legsOut[1] = rf2;
            // ocelot tail chain: tail1 is the root hinge
            if (byName.TryGetValue("tail", out var tt)) tailOut = tt;
            if (tailOut == null && byName.TryGetValue("tail1", out var t1)) tailOut = t1;
            // Bedrock swaps head <-> head_sleeping when the mob sleeps; hide
            // the sleeping variant until then (its UVs sample the closed-eye
            // region and double every head cube).
            if (byName.TryGetValue("head_sleeping", out var hs)) hs.gameObject.SetActive(false);
            if (byName.TryGetValue("head", out var ht)) headOut = ht;
            if (byName.TryGetValue("wing0", out var w0)) wingsOut[0] = w0;
            if (byName.TryGetValue("wing1", out var w1)) wingsOut[1] = w1;
            // bee: leftwing_bone/rightwing_bone; bat: leftWing/rightWing.
            // Archetype-driven naming: match <side>wing* case-insensitively.
            foreach (var kv in byName)
            {
                string n = kv.Key.ToLowerInvariant();
                if (n.StartsWith("leftwing")) wingsOut[0] = kv.Value;
                else if (n.StartsWith("rightwing")) wingsOut[1] = kv.Value;
            }

            // Optional body pitch for pillar-rest-pose geos (fox) and leg
            // re-rooting so walk swings stay vertical.
            if (bodyPitchDeg != 0f && byName.TryGetValue("body", out var bodyBone))
            {
                bodyBone.localRotation = Quaternion.Euler(bodyPitchDeg, 0f, 0f) * bodyBone.localRotation;
            }
            // Hierarchy stays EXACTLY as bedrock authored it: legs/tail/head
            // remain children of body. When an official clip rotates the body
            // (fox.sleep rolls -90, fox.sit pitches -60, wolf sitting) the
            // whole subtree follows and the clip's own leg channels compensate
            // - the same math the vanilla runtime runs. (An earlier hack
            // re-rooted legs to the root for a tilted-frame walk swing; it
            // planted the feet but DECOUPLED them from the body, so any body
            // rotation split the model apart mid-animation.)

            // ---- setup-clip bake (bedrock runtime applies ".setup" once) ----
            if (setupAnimJson != null) BakeSetup(setupAnimJson.text, byName, boneNameToOriginal, modelPos);

            return true;
        }

        /// <summary>Parse the species' .setup clip and bake its constant
        /// transforms into the imported skeleton. Position targets are FINAL
        /// bedrock pivots ("c - this" with this=0); our import already sits in
        /// the setup-applied frame for the uniform v1.8 y-shift, so only
        /// per-bone deltas beyond that shift move bones. Rotation targets are
        /// applied relative to the imported rest rotation.</summary>
        private static void BakeSetup(string animJson,
            Dictionary<string, Transform> byName,
            Dictionary<string, string> boneNameToOriginal,
            Dictionary<string, Vector3> modelPos)
        {
            var wrap = MiniJson.Deserialize(animJson) as Dictionary<string, object>;
            if (wrap == null) return;
            if (!(wrap.TryGetValue("animations", out object av) && av is Dictionary<string, object> anims)) return;

            var posTargets = new Dictionary<string, Vector3>();
            var rotTargets = new Dictionary<string, Vector3>();
            foreach (var kv in anims)
            {
                if (!kv.Key.Contains(".setup") || kv.Key.Contains("v1.0") || kv.Key.Contains("baby")) continue;
                if (!(kv.Value is Dictionary<string, object> clip)) continue;
                if (!clip.TryGetValue("bones", out object bv) || !(bv is Dictionary<string, object> bs)) continue;
                foreach (var bk in bs)
                {
                    if (!(bk.Value is Dictionary<string, object> ch)) continue;
                    if (ch.TryGetValue("position", out object pv) && pv is List<object> pl && pl.Count == 3)
                    {
                        Vector3 t = new Vector3(ConstOf(pl[0]), ConstOf(pl[1]), ConstOf(pl[2]));
                        posTargets[bk.Key.ToLowerInvariant()] = t;
                    }
                    if (ch.TryGetValue("rotation", out object rv) && rv is List<object> rl && rl.Count == 3)
                    {
                        Vector3 t = new Vector3(ConstOf(rl[0]), ConstOf(rl[1]), ConstOf(rl[2]));
                        rotTargets[bk.Key.ToLowerInvariant()] = t;
                    }
                }
            }
            // Sheep's setup.v2 head position ("0, -6-this, 0") moves the head
            // 8px BACK into the body in our frame - pixel-verified wrong; the
            // v1.8 geo head pivot already sits correct. Wolf's mane z-5 IS
            // correct. Species whose setup positions are known-bad:
            if (animJson.Contains("sheep.setup")) posTargets.Clear();

            if (posTargets.Count == 0 && rotTargets.Count == 0) return;

            // Uniform y-shift detection: bedrock v1.8 geos sit 24px higher in
            // author space; a setup that lowers EVERY bone by the same amount
            // is a coordinate-convention shift our import already absorbed.
            float uniformY = float.NaN;
            bool allSameY = posTargets.Count > 0;
            foreach (var kv in posTargets)
            {
                // pivot lookup by original (un-SafeName'd) bone name
                Vector3 bp = Vector3.zero; bool found = false;
                foreach (var mp in modelPos)
                    if (mp.Key.ToLowerInvariant() == kv.Key)
                    { bp = new Vector3(-mp.Value.x, mp.Value.y, -mp.Value.z) / Px; found = true; break; }
                if (!found) { allSameY = false; break; }
                float dy = kv.Value.y - bp.y;
                if (float.IsNaN(uniformY)) uniformY = dy;
                else if (Mathf.Abs(dy - uniformY) > 0.01f) { allSameY = false; break; }
            }
            float skipY = allSameY ? uniformY : 0f;

            foreach (var kv in posTargets)
            {
                Transform bone = null;
                foreach (var bn in byName)
                    if (bn.Key.ToLowerInvariant() == kv.Key) { bone = bn.Value; break; }
                if (bone == null) continue;
                Vector3 bp = Vector3.zero;
                foreach (var mp in modelPos)
                    if (mp.Key.ToLowerInvariant() == kv.Key)
                    { bp = new Vector3(-mp.Value.x, mp.Value.y, -mp.Value.z) / Px; break; }
                Vector3 target = kv.Value;
                Vector3 delta = target - bp;
                delta.y -= skipY;
                if (delta.sqrMagnitude < 1e-6f) continue;
                // bedrock x/z flip: our model x'=-x, z'=-z
                bone.localPosition += new Vector3(-delta.x, delta.y, -delta.z) * Px;
                Debug.Log($"[BakeSetup] pos {bone.name}: bp={bp} target={target} skipY={skipY} applied={new Vector3(-delta.x, delta.y, -delta.z) * Px}");
            }
            foreach (var kv in rotTargets)
            {
                Transform bone = null;
                foreach (var bn in byName)
                    if (bn.Key.ToLowerInvariant() == kv.Key) { bone = bn.Value; break; }
                if (bone == null) continue;
                Vector3 r = kv.Value;
                if (r.sqrMagnitude < 1e-6f) continue;
                // Empirically grounded sign: bedrock Rx(+90) tips the upright
                // pillar onto its back the same way geo bind_pose_rotation
                // does (cube-level Euler(+90) - verified by the grid-searched
                // wolf: body must land at z -0.563..0 covering the hind legs).
                bone.localRotation = Quaternion.Euler(r.x, r.y, r.z) * bone.localRotation;
                Debug.Log($"[BakeSetup] rot {bone.name}: target={r}");
            }
        }

        private static float ConstOf(object o)
        {
            if (o is double d) return (float)d;
            if (o is long l) return l;
            if (o is string str)
            {
                str = str.Trim();
                if (float.TryParse(str, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var f)) return f;
                if (str == "-this" || str == "this" || str == "query.is_baby ? 0.0 : -this") return 0f;
                // "c - this" (also "query.is_baby ? 0.0 : (c - this)")
                int cut = str.LastIndexOf('-');
                while (cut > 0)
                {
                    var head = str.Substring(0, cut).Trim();
                    if (float.TryParse(head, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var h)) return h;
                    cut = str.LastIndexOf('-', cut - 1);
                }
            }
            return 0f;
        }

        private static string SafeName(string s) => string.IsNullOrEmpty(s) ? "bone" : s.Replace(':', '_');

        private static float ToFloat(object o) => o is double d ? (float)d : 0f;

        /// <summary>
        /// Bedrock per-cube UV net. Bedrock unwraps: +X/-X flanks D wide,
        /// top/bottom WxD at v, front (+Z ours) and back WxH at v+D. Mirrored
        /// x flips left/right nets; v runs top-down.
        /// </summary>
        private static Vector4[] BuildNet(int u, int v, int W, int H, int D, bool mirror = false)
        {
            // same layout as BoxBuilder.McNet but with the +Z/-Z rects swapped
            // for the flipped-frame front: our +Z face shows the bedrock front.
            var net = new Vector4[]
            {
                new Vector4(u, v + D, D, H),                 // +X (our east)
                new Vector4(u + D + W, v + D, D, H),         // -X (our west)
                new Vector4(u + D, v, W, D),                 // +Y top
                new Vector4(u + D + W, v, W, D),             // -Y bottom
                new Vector4(u + D, v + D, W, H),             // +Z our FRONT (bedrock front)
                new Vector4(u + 2 * D + W, v + D, W, H),     // -Z our BACK (bedrock back)
            };
            if (mirror)
            {
                // Bedrock "mirror": true on a BONE flips its cubes' unwrap
                // horizontally: the +X/-X flank rects swap places and every
                // face samples its rect with u reversed (handled by the
                // faceFlip array in SkinnedBox). Bat wings/ears and humanoid
                // left limbs share uv with the right side and rely on this.
                var t = net[0]; net[0] = net[1]; net[1] = t;
            }
            return net;
        }

        private static readonly bool[] MirrorFlips = { true, true, true, true, true, true };
    }
}

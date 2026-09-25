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
            Material skin, float bodyPitchDeg,
            out Transform[] legsOut, out Transform tailOut,
            out Transform headOut, out Transform[] wingsOut)
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
                    break;
                }
            }
            if (bones == null) return false;

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
            var modelPos = new Dictionary<string, Vector3>();
            foreach (var kv in boneMeta)
            {
                string name = kv.Key;
                var bone = kv.Value;
                var t = byName[name];
                string parentName = bone.TryGetValue("parent", out object p) && p is string ps ? ps : null;
                Transform pt = parentName != null && byName.TryGetValue(parentName, out var ptFound) ? ptFound : null;
                t.SetParent(pt != null ? pt : root, false);

                Vector3 pos = Vector3.zero;
                if (bone.TryGetValue("pivot", out object pv) && pv is List<object> pl && pl.Count == 3)
                {
                    pos = new Vector3(-ToFloat(pl[0]) * Px, ToFloat(pl[1]) * Px, -ToFloat(pl[2]) * Px);
                }
                modelPos[name] = pos;
                if (pt != null && modelPos.TryGetValue(parentName, out var pp))
                {
                    t.localPosition = Quaternion.Inverse(pt.localRotation) * (pos - pp);
                }
                else
                {
                    t.localPosition = pos;
                }
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
                if (bone.TryGetValue("bind_pose_rotation", out object bpv) && bpv is List<object> bpl && bpl.Count == 3)
                {
                    // Store for cube-level application; ALSO bake it into the
                    // transform so children authored in the final frame stay
                    // correct: pivot math below already handles rotated
                    // parents via Inverse(parent.localRotation).
                    boneBind[name] = new Vector3(ToFloat(bpl[0]), ToFloat(bpl[1]), ToFloat(bpl[2]));
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
                        // Bedrock Rx(-theta) lands the pillar on its back in
                        // our flipped frame; verified: pig torso must sit at
                        // y 6..14 px meeting the 6 px legs.
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

                    Vector4[] net = BuildNet(u, v, W, H, D);
                    var box = Art.BoxBuilder.SkinnedBox(bone, "cube_" + W + "x" + H + "x" + D,
                        local, new Vector3(sx * Px, sy * Px, sz * Px), skin, net, texW, texH);
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
            // goat uses named legs; map them too
            if (byName.TryGetValue("left_front_leg", out var lfl)) legsOut[0] = lfl;
            if (byName.TryGetValue("right_front_leg", out var rfl)) legsOut[1] = rfl;
            if (byName.TryGetValue("left_back_leg", out var lbl)) legsOut[2] = lbl;
            if (byName.TryGetValue("right_back_leg", out var rbl)) legsOut[3] = rbl;
            if (byName.TryGetValue("right_front_leg", out var rf2)) legsOut[1] = rf2;
            if (byName.TryGetValue("tail", out var tt)) tailOut = tt;
            if (byName.TryGetValue("head", out var ht)) headOut = ht;
            if (byName.TryGetValue("wing0", out var w0)) wingsOut[0] = w0;
            if (byName.TryGetValue("wing1", out var w1)) wingsOut[1] = w1;

            // Optional body pitch for pillar-rest-pose geos (fox) and leg
            // re-rooting so walk swings stay vertical.
            if (bodyPitchDeg != 0f && byName.TryGetValue("body", out var bodyBone))
            {
                bodyBone.localRotation = Quaternion.Euler(bodyPitchDeg, 0f, 0f) * bodyBone.localRotation;
            }
            // Legs parented under a rotated body (fox body pitch, 1.8 torso
            // bind rotation) inherit a tilted frame: re-parent them to the
            // root at their model-space pivot with an upright rotation, so
            // the walk swing axis stays vertical and feet stay planted.
            for (int i = 0; i < 4; i++)
            {
                var lt = legsOut[i];
                if (lt == null) continue;
                if (lt.parent != null && lt.parent.name != "GeoRoot")
                {
                    // Re-derive the model-space pivot (stored in modelPos
                    // during pass 2) instead of trusting the pitched world
                    // position: SetParent(root, worldPositionStays) also
                    // bakes the parent pitch into localRotation, which flips
                    // legs sideways. Parent with stays=false and reset pose.
                    // (No child compensation needed: with cube-level bind
                    // rotation no leg bone carries a rotated frame, and the
                    // fox body pitch must NOT be baked into its legs.)
                    string legName = boneNameToOriginal != null && boneNameToOriginal.TryGetValue(lt.name, out var orig) ? orig : lt.name;
                    Vector3 mp = modelPos.TryGetValue(legName, out var lp) ? lp : lt.localPosition;
                    lt.SetParent(root, false);
                    lt.localPosition = mp;
                    lt.localRotation = Quaternion.identity;
                }
            }

            return true;
        }

        private static string SafeName(string s) => string.IsNullOrEmpty(s) ? "bone" : s.Replace(':', '_');

        private static float ToFloat(object o) => o is double d ? (float)d : 0f;

        /// <summary>
        /// Bedrock per-cube UV net. Bedrock unwraps: +X/-X flanks D wide,
        /// top/bottom WxD at v, front (+Z ours) and back WxH at v+D. Mirrored
        /// x flips left/right nets; v runs top-down.
        /// </summary>
        private static Vector4[] BuildNet(int u, int v, int W, int H, int D)
        {
            // same layout as BoxBuilder.McNet but with the +Z/-Z rects swapped
            // for the flipped-frame front: our +Z face shows the bedrock front.
            return new Vector4[]
            {
                new Vector4(u, v + D, D, H),                 // +X (our east)
                new Vector4(u + D + W, v + D, D, H),         // -X (our west)
                new Vector4(u + D, v, W, D),                 // +Y top
                new Vector4(u + D + W, v, W, D),             // -Y bottom
                new Vector4(u + D, v + D, W, H),             // +Z our FRONT (bedrock front)
                new Vector4(u + 2 * D + W, v + D, W, H),     // -Z our BACK (bedrock back)
            };
        }
    }
}

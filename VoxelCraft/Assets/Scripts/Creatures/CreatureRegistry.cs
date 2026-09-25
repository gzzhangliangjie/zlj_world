using System.Collections.Generic;
using UnityEngine;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// Data-driven species registry (Resources/Registry/creatures.json).
    /// Adding a species = dropping its Geo/&lt;sp&gt;.geo, Textures/&lt;sp&gt;_skin.png,
    /// Anims/&lt;sp&gt;.animation.json and one registry entry - zero code changes.
    /// Everything that used to be a `species ==` branch (walk clip, gait mode,
    /// ambient behaviours, face-uv overrides) reads from here.
    /// </summary>
    public static class CreatureRegistry
    {
        public class BehaviourDef
        {
            public string clip;
            public bool absolute;
            public float minT, maxT;
        }

        public class SpeciesDef
        {
            public string walkClip = "animation.quadruped.walk";
            public List<string> extraClips;      // played permanently alongside walk
            public Dictionary<string, float> extraVariables;
            public string gait;                  // "goat" = goatGait entity-layer vars
            public bool biped;
            public List<BehaviourDef> behaviours;
        }

        static Dictionary<string, SpeciesDef> cache;

        public static SpeciesDef Get(string species)
        {
            Load();
            if (cache != null && cache.TryGetValue(species, out var d)) return d;
            return null; // unknown species: caller falls back to defaults
        }

        /// <summary>Face-uv rect override (png coords u,v,w,h) or null.</summary>
        public static int[] FaceOverride(string species)
        {
            var asset = Resources.Load<TextAsset>("Registry/creatures");
            if (asset == null) return null;
            try
            {
                var root = MiniJson.Deserialize(asset.text) as Dictionary<string, object>;
                if (root == null) return null;
                if (!(root.TryGetValue("faceOverrides", out var fo) && fo is Dictionary<string, object> fod)) return null;
                if (!fod.TryGetValue(species, out var v) || !(v is List<object> l) || l.Count != 4) return null;
                return new[] { System.Convert.ToInt32(l[0]), System.Convert.ToInt32(l[1]), System.Convert.ToInt32(l[2]), System.Convert.ToInt32(l[3]) };
            }
            catch { return null; }
        }

        static void Load()
        {
            if (cache != null) return;
            cache = new Dictionary<string, SpeciesDef>();
            var asset = Resources.Load<TextAsset>("Registry/creatures");
            if (asset == null) return;
            try
            {
                var root = MiniJson.Deserialize(asset.text) as Dictionary<string, object>;
                if (root == null) return;
                if (!(root.TryGetValue("species", out var sv) && sv is Dictionary<string, object> sps)) return;
                foreach (var kv in sps)
                {
                    if (!(kv.Value is Dictionary<string, object> o)) continue;
                    var def = new SpeciesDef();
                    if (o.TryGetValue("walkClip", out var wc) && wc is string wcs) def.walkClip = wcs;
                    if (o.TryGetValue("gait", out var g) && g is string gs) def.gait = gs;
                    if (o.TryGetValue("biped", out var bp) && bp is bool bpb) def.biped = bpb;
                    if (o.TryGetValue("extraClips", out var ec) && ec is List<object> ecl && ecl.Count > 0)
                    {
                        def.extraClips = new List<string>();
                        foreach (var c in ecl) if (c is string cs) def.extraClips.Add(cs);
                    }
                    if (o.TryGetValue("extraVariables", out var ev) && ev is Dictionary<string, object> evd)
                    {
                        def.extraVariables = new Dictionary<string, float>();
                        foreach (var ekv in evd)
                            def.extraVariables[ekv.Key] = System.Convert.ToSingle(ekv.Value);
                    }
                    if (o.TryGetValue("behaviours", out var bh) && bh is List<object> bhl && bhl.Count > 0)
                    {
                        def.behaviours = new List<BehaviourDef>();
                        foreach (var b in bhl)
                        {
                            if (!(b is Dictionary<string, object> bo)) continue;
                            var bd = new BehaviourDef();
                            if (bo.TryGetValue("clip", out var cn) && cn is string cns) bd.clip = cns;
                            if (bo.TryGetValue("absolute", out var ab) && ab is bool abb) bd.absolute = abb;
                            if (bo.TryGetValue("minT", out var mn)) bd.minT = System.Convert.ToSingle(mn);
                            if (bo.TryGetValue("maxT", out var mx)) bd.maxT = System.Convert.ToSingle(mx);
                            if (!string.IsNullOrEmpty(bd.clip)) def.behaviours.Add(bd);
                        }
                    }
                    cache[kv.Key] = def;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError("[CreatureRegistry] parse failed: " + e.Message);
                cache = null;
            }
        }
    }
}

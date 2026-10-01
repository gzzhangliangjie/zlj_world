// M36 Animation Preview Window: pick any species, browse its clip list,
// play/pause clips, fire engine behaviour events, watch the vanilla
// controller react. Editor-only tool (Window > VoxelCraft > Anim Preview).
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using VoxelCraft.Creatures;

namespace VoxelCraft.Editor
{
    public class AnimPreviewWindow : EditorWindow
    {
        string curSpecies = "camel";
        string curVariant;
        bool showHidden;
        BlockyAnimal ani;
        BedrockAnimationPlayer player;
        GameObject stageRoot, previewGo;
        Vector2 clipScroll, evScroll;
        string filter = "";
        bool walking = true, autoEvents = true;
        float lastDt = 0.05f;
        string status = "";

        [MenuItem("Window/VoxelCraft/Anim Preview")]
        static void Open() => GetWindow<AnimPreviewWindow>("Anim Preview");

        void OnEnable()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(100f, 300f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));
            Rebuild();
        }

        void Rebuild()
        {
            if (stageRoot != null) DestroyImmediate(stageRoot);
            stageRoot = new GameObject("AnimPreviewStage");
            stageRoot.transform.position = new Vector3(0f, -500f, 0f); // away from scene
            previewGo = new GameObject("Preview_" + curSpecies);
            previewGo.transform.SetParent(stageRoot.transform, false);
            ani = previewGo.AddComponent<BlockyAnimal>();
            ani.species = curSpecies;
            ani.variant = curVariant;
            ani.BuildModel();
            player = previewGo.GetComponent<BedrockAnimationPlayer>();
            status = $"{curSpecies}: {player.ClipCount} clips";
        }

        void OnGUI()
        {
            if (stageRoot == null) Rebuild();

            // species picker
            GUILayout.BeginHorizontal();
            var speciesList = RegistrySpecies().ToList();
            int cur = Mathf.Max(0, speciesList.IndexOf(curSpecies));
            int next = EditorGUILayout.Popup("Species", cur, speciesList.ToArray());
            if (next != cur) { curSpecies = speciesList[next]; curVariant = ""; Rebuild(); }
            var vars = CreatureRegistry.Get(curSpecies)?.variants;
            if (vars != null && vars.Count > 0)
            {
                int vi = Mathf.Max(0, vars.IndexOf(curVariant.Length == 0 ? vars[0] : curVariant));
                int vn = EditorGUILayout.Popup("Variant", vi, vars.ToArray());
                string want = vars[vn];
                if (want != (curVariant.Length == 0 ? vars[0] : curVariant))
                {
                    curVariant = want;
                    if (ani != null) { ani.variant = want; Rebuild(); }
                }
            }
            filter = EditorGUILayout.TextField("Filter clips", filter);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            walking = GUILayout.Toggle(walking, "walking");
            autoEvents = GUILayout.Toggle(autoEvents, "auto events");
            bool tm = GUILayout.Toggle(ani != null && ani.tamed, "tamed (collar)");
            if (ani != null && tm != ani.tamed) ani.SetTamed(tm);
            bool sh = GUILayout.Toggle(showHidden, "show hidden bones");
            if (sh != showHidden)
            {
                showHidden = sh;
                if (previewGo != null)
                    foreach (var t in previewGo.GetComponentsInChildren<Transform>(true))
                        t.gameObject.SetActive(true);
                status = sh ? "hidden bones FORCED visible (inspect mode)" : status;
            }
            if (GUILayout.Button("Reset model")) Rebuild();
            GUILayout.EndHorizontal();

            EditorGUILayout.LabelField("Status", status);
            EditorGUILayout.HelpBox(
                "clips played via player.Play() (engine-direct). Engine events fire " +
                "the vanilla gates (ForceBehaviourEvent) - controllers/pre_animation " +
                "see the flags and schedule the official clips themselves.", MessageType.None);

            if (player == null) return;

            GUILayout.BeginHorizontal();
            // left: clip list
            GUILayout.BeginVertical(GUILayout.Width(340));
            clipScroll = GUILayout.BeginScrollView(clipScroll, GUILayout.Height(300));
            foreach (var c in player.ClipNames.OrderBy(n => n))
            {
                if (!string.IsNullOrEmpty(filter) &&
                    !c.ToLowerInvariant().Contains(filter.ToLowerInvariant())) continue;
                GUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(c, GUILayout.Width(250));
                if (GUILayout.Button("play", GUILayout.Width(40)))
                {
                    ani.walking = false;
                    if (player != null) player.moving = false;
                    player.Play(c, false);
                    status = "playing " + c;
                }
                if (GUILayout.Button("stop", GUILayout.Width(40)))
                    player.Stop(c);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            if (GUILayout.Button("stop all"))
            {
                foreach (var c in player.ClipNames.ToArray()) player.Stop(c);
                status = "stopped all";
            }
            GUILayout.EndVertical();

            // right: engine events for this species
            GUILayout.BeginVertical(GUILayout.Width(240));
            GUILayout.Label("Engine behaviour events:");
            evScroll = GUILayout.BeginScrollView(evScroll, GUILayout.Height(300));
            foreach (var ev in EventNames())
            {
                if (GUILayout.Button(ev))
                {
                    var ok = ani.ForceBehaviourEvent(ev, 4f);
                    status = ok ? "event fired: " + ev : "no such event: " + ev;
                }
            }
            GUILayout.EndScrollView();
            GUILayout.Label($"active: {ani.ActiveBehaviourEvent ?? "(none)"}");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            // playing summary
            GUILayout.Space(6);
            EditorGUILayout.LabelField("Playing:", player.DebugPlaying());
        }

        void Update()
        {
            if (ani == null || player == null) return;
            if (!Application.isPlaying)
            {
                float dt = (float)(EditorApplication.timeSinceStartup - (lastReal ?? EditorApplication.timeSinceStartup));
                lastReal = EditorApplication.timeSinceStartup;
                dt = Mathf.Clamp(dt, 0f, 0.1f);
                lastDt = dt;
                ani.walking = walking;
                player.moving = walking;
                if (autoEvents) ani.TickBehaviourEvents(dt);
                ani.TickControllers(dt, walking);
                player.Tick(dt);
                Repaint();
            }
        }
        double? lastReal;

        static IEnumerable<string> RegistrySpecies()
        {
            var asm = typeof(CreatureRegistry);
            var list = new List<string>();
            // CreatureRegistry keeps a species list in the json; expose via
            // the known load path: read Registry/creatures.json keys.
            var ta = UnityEngine.Resources.Load<TextAsset>("Registry/creatures");
            if (ta != null)
            {
                var json = MiniJson.Deserialize(ta.text) as Dictionary<string, object>;
                if (json != null && json.TryGetValue("species", out var sp))
                {
                    var dict = sp as Dictionary<string, object>;
                    if (dict != null) list.AddRange(dict.Keys.Cast<string>());
                }
            }
            list.Sort();
            return list;
        }

        static string[] EventNames()
        {
            return new[] { "rear", "graze", "tail_shake", "baby", "ram_attack", "attack",
                "shake", "interested", "sit", "sleep", "pounce", "crouch", "wiggle",
                "bite", "swim", "nectar", "sting", "raise_arms", "jump_goal",
                "croak", "eat_mob", "dash" };
        }
    }
}

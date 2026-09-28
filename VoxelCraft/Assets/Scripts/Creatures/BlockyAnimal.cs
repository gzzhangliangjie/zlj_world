using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Items;
using VoxelCraft.World;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// A Minecraft-style blocky animal: box model with a real MC-format skin
    /// (procedural fallback), code-driven leg animation, capsule collision, and a
    /// wander AI that anchors to terrain (ignoring trees) and avoids obstacles,
    /// water, steep steps, the player and other animals. Sword hits knock it back
    /// and drops meat on death.
    /// </summary>
    public class BlockyAnimal : MonoBehaviour
    {
        public WorldRoot world;
        public Transform playerRef;
        public string species = "pig";
        public float walkSpeed = 1.4f;
        public float health = 3f;
        public bool dead;
        /// <summary>Debug/harness: force the fish controller into the
        /// flopping (dry-land) state - used by AnimVerify when the swim clip
        /// only drives bones the geo lacks (pufferfish large has no tailfin).</summary>
        public bool aquaticDryLand;

        private Transform[] legs = new Transform[4];
        private Transform[] wings = new Transform[2]; // chicken wing hinges
        private Transform tail;                       // wolf/fox tail hinges
        private Quaternion tailBaseRot = Quaternion.identity;

        private Transform bodyRoot;
        private Transform head;
        private BedrockAnimationPlayer geoAnimPlayer; // official clips (geo path)
        private BedrockControllerRuntime controllers; // B-plan state machine (opt-in per species)
        private float animPhase;
        private float stateTimer;
        [Tooltip("Roaming state; verification harness forces this true/false.")]
        public bool walking = true;
        private float targetYaw;
        private float smoothY;
        private readonly Collider[] overlapBuffer = new Collider[8];

        public bool IsWalking => walking;

        /// <summary>
        /// Standard Minecraft 64x32 skin nets for a species (VoxeLibre mobs_mc
        /// sheets). Bodies are MC-rotated boxes (long axis vertical in the
        /// texture), so they use QuadrupedBodyNet + QuadrupedBodyRots; heads and
        /// legs are plain upright boxes. Exposed for the SelfTest net validation.
        /// </summary>
        public static void GetSpeciesNets(string kind, out Vector4[] body, out int[] bodyRot,
            out Vector4[] head, out Vector4[] leg)
        {
            switch (kind)
            {
                case "cow":
                    body = BoxBuilder.QuadrupedBodyNet(18, 4, 12, 18, 10);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 6);
                    leg = BoxBuilder.McNet(0, 16, 4, 12, 4);
                    break;
                case "sheep":
                    // Vanilla woolly sheep (Java SheepWoolModel = bedrock fur
                    // overlay): UPRIGHT body 8x16x6 uv(28,8), head 6x6x6
                    // uv(0,0), legs 4x6x4 uv(0,16) - body NOT rotated (rot90
                    // was the misfit; sheep torso runs along Z in the sheet).
                    body = BoxBuilder.McNet(28, 8, 8, 16, 6);
                    head = BoxBuilder.McNet(0, 0, 6, 6, 6);
                    leg = BoxBuilder.McNet(0, 16, 4, 12, 4);
                    break;
                case "chicken":
                    // Vanilla Java ChickenModel: body uv (0,9) 6x8x6 rot90.
                    // Was (0,8): off-by-one row sampled the head net bottom,
                    // scrambling the whole torso.
                    body = BoxBuilder.QuadrupedBodyNet(0, 9, 6, 8, 6);
                    head = BoxBuilder.McNet(0, 0, 4, 6, 3);
                    // Vanilla legs are 3x5x3 boxes at uv(26,0), not 2x5x2
                    // sticks: the 2px sticks sampled across column
                    // boundaries and picked up wing/base-white noise.
                    leg = BoxBuilder.McNet(26, 0, 3, 5, 3);
                    break;
                case "wolf":
                    // Vanilla wolf (bedrock): body 6x9x6 uv(18,14), head
                    // 6x6x4 uv(0,0), snout 3x3x4 uv(0,10), mane 8x6x7
                    // uv(21,0), legs 2x8x2 uv(0,18), tail 2x8x2 uv(9,18).
                    body = BoxBuilder.McNet(18, 14, 6, 9, 6);
                    head = BoxBuilder.McNet(0, 0, 6, 6, 4);
                    leg = BoxBuilder.McNet(0, 18, 2, 8, 2);
                    break;
                case "fox":
                    // Bedrock-layout sheet (bedrock-samples v1.21.80.3
                    // fox.png, 64x32): offsets are the geo-declared UVs -
                    // head uv(0,0) 8x6x6 (front rect(6,6,8,6) with K eyes
                    // at x0/x7), body uv(30,15) 6x11x6, legs uv(14,24)/
                    // (22,24) 2x6x2. The old Java-layout offsets (24,15)/
                    // (1,5) sampled transparent padding on the bedrock
                    // sheet. Rendering itself uses the geo pipeline; these
                    // nets feed the SelfTest m9 uv validation.
                    body = BoxBuilder.McNet(30, 15, 6, 11, 6);
                    head = BoxBuilder.McNet(0, 0, 8, 6, 6);
                    leg = BoxBuilder.McNet(14, 24, 2, 6, 2);
                    break;
                case "goat":
                    // Vanilla goat (bedrock geo, 64x64 sheet): neck+chest
                    // 9x11x16 uv(1,1), rump 11x14x11 uv(0,28), head
                    // 5x7x10 uv(34,46), horns 2x7x2 uv(12,55), legs
                    // 3x10x3 front uv(35,2), 3x6x3 back uv(36,29)/(49,29).
                    body = BoxBuilder.McNet(1, 1, 9, 11, 16);
                    head = BoxBuilder.McNet(34, 46, 5, 7, 10);
                    leg = BoxBuilder.McNet(35, 2, 3, 10, 3);
                    break;
                case "horse":
                    // Bedrock horse_v3 geo (64x64 sheet): body 10x10x22
                    // uv(0,32), head 6x5x7 uv(0,13), legs 4x11x4 uv(48,21).
                    // Feeds SelfTest m9 uv validation; rendering is the geo
                    // pipeline.
                    body = BoxBuilder.McNet(0, 32, 10, 10, 22);
                    head = BoxBuilder.McNet(0, 13, 6, 5, 7);
                    leg = BoxBuilder.McNet(48, 21, 4, 11, 4);
                    break;
                case "donkey":
                    // horse_v3 geo family (donkey_v3 entity -> geometry.horse.v3,
                    // textures/entity/horse2/donkey.png 64x64). Same nets as horse.
                    body = BoxBuilder.McNet(0, 32, 10, 10, 22);
                    head = BoxBuilder.McNet(0, 13, 6, 5, 7);
                    leg = BoxBuilder.McNet(48, 21, 4, 11, 4);
                    break;
                case "rabbit":
                    // geometry.rabbit (64x32 sheet): body 6x5x10 uv(0,0),
                    // head 5x4x5 uv(32,0), front legs 2x7x2 uv(8,15)/uv(0,15),
                    // haunch 2x4x5 uv(16,15), rear foot 2x1x7 uv(8,24).
                    body = BoxBuilder.McNet(0, 0, 6, 5, 10);
                    head = BoxBuilder.McNet(32, 0, 5, 4, 5);
                    leg = BoxBuilder.McNet(8, 15, 2, 7, 2);
                    break;
                case "panda":
                    // geometry.panda (64x64 sheet): body 19x26x13 uv(0,25),
                    // head 13x10x9 uv(0,6), legs 6x9x6 uv(40,0) all four.
                    body = BoxBuilder.McNet(0, 25, 19, 26, 13);
                    head = BoxBuilder.McNet(0, 6, 13, 10, 9);
                    leg = BoxBuilder.McNet(40, 0, 6, 9, 6);
                    break;
                case "armadillo":
                    // Bedrock armadillo geo (64x64 sheet): body 8x8x12
                    // uv(0,20) (second shell cube uv(0,40)), head 3x5x2
                    // uv(43,15), legs 2x3x2 uv(51,31)/(42,31).
                    body = BoxBuilder.McNet(0, 20, 8, 8, 12);
                    head = BoxBuilder.McNet(43, 15, 3, 5, 2);
                    leg = BoxBuilder.McNet(51, 31, 2, 3, 2);
                    break;
                case "ocelot":
                    // Bedrock ocelot geo (64x32 sheet): head 5x4x5 uv(0,0),
                    // body 4x16x6 uv(20,0), front legs 2x10x2 uv(40,0),
                    // back legs 2x6x2 uv(8,13). Feeds SelfTest m9.
                    body = BoxBuilder.McNet(20, 0, 4, 16, 6);
                    head = BoxBuilder.McNet(0, 0, 5, 4, 5);
                    leg = BoxBuilder.McNet(40, 0, 2, 10, 2);
                    break;
                case "creeper":
                    // Bedrock creeper geo (64x32 sheet): head 8x8x8 uv(0,0),
                    // body 8x12x4 uv(16,16), legs 4x6x4 uv(0,16).
                    // McNet(w,h,d) mirrors the standard creeper layout.
                    body = BoxBuilder.McNet(16, 16, 8, 12, 4);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(0, 16, 4, 6, 4);
                    break;
                case "hoglin":
                    // Bedrock hoglin geo (128x64 sheet): body 16x14x26
                    // uv(1,1) (mane plate 0x10x19 uv(90,33)), head 14x6x19
                    // uv(61,1) + tusks 2x11x2 uv(1,13), ears 6x1x4
                    // uv(1,1)/(1,6), front legs 6x14x6 uv(41,42)/(66,42),
                    // back legs 5x11x5 uv(0,45)/(21,45).
                    body = BoxBuilder.McNet(1, 1, 16, 14, 26);
                    head = BoxBuilder.McNet(61, 1, 14, 6, 19);
                    leg = BoxBuilder.McNet(41, 42, 6, 14, 6);
                    break;
                case "polar_bear":
                    // Bedrock polarbear geo (128x64 sheet): body 14x14x11
                    // uv(0,19) + hump 12x12x10 uv(39,0), head 7x7x7 uv(0,0)
                    // + snout 5x3x3 uv(0,44) + ears 2x2x1 uv(26,0),
                    // legs 4x10x8 uv(50,22) front / 4x10x6 uv(50,40) rear.
                    body = BoxBuilder.McNet(0, 19, 14, 14, 11);
                    head = BoxBuilder.McNet(0, 0, 7, 7, 7);
                    leg = BoxBuilder.McNet(50, 22, 4, 10, 8);
                    break;
                case "llama":
                    // Bedrock llama geo (128x64 sheet): head 8x18x6 uv(0,14),
                    // body 12x18x10 uv(29,0), legs 4x14x4 uv(29,29).
                    body = BoxBuilder.McNet(29, 0, 12, 18, 10);
                    head = BoxBuilder.McNet(0, 14, 8, 18, 6);
                    leg = BoxBuilder.McNet(29, 29, 4, 14, 4);
                    break;
                case "bee":
                    // Bedrock bee geo (64x64 sheet): body 7x7x10 uv(0,0),
                    // wing plates 9x1x6 uv(0,18)/(9,24), legs 7x2x1 uv(26,1+).
                    body = BoxBuilder.McNet(0, 0, 7, 7, 10);
                    head = BoxBuilder.McNet(0, 0, 7, 7, 10);
                    leg = BoxBuilder.McNet(26, 1, 7, 2, 1);
                    break;
                case "spider":
                    // Bedrock spider v1.8 geo (64x32 sheet): abdomen body1
                    // 10x8x12 uv(0,12), head 8x8x8 uv(32,4), leg plates
                    // 16x2x2 uv(18,0) all eight.
                    body = BoxBuilder.McNet(0, 12, 10, 8, 12);
                    head = BoxBuilder.McNet(32, 4, 8, 8, 8);
                    leg = BoxBuilder.McNet(18, 0, 16, 2, 2);
                    break;
                case "parrot":
                    // Bedrock parrot geo (32x32 sheet): body 3x6x3 uv(2,8),
                    // head 2x3x2 uv(2,2), leg plates 1x2x1 uv(14,18).
                    body = BoxBuilder.McNet(2, 8, 3, 6, 3);
                    head = BoxBuilder.McNet(2, 2, 2, 3, 2);
                    leg = BoxBuilder.McNet(14, 18, 1, 2, 1);
                    break;
                case "bat":
                    // Bedrock bat geo (64x64 sheet): head 6x6x6 uv(0,0),
                    // body 6x12x6 uv(0,16), wing plates 10x16x1 uv(42,0).
                    body = BoxBuilder.McNet(0, 16, 6, 12, 6);
                    head = BoxBuilder.McNet(0, 0, 6, 6, 6);
                    leg = BoxBuilder.McNet(42, 0, 10, 16, 1);
                    break;
                case "steve":
                    // Bedrock geometry.humanoid.custom (64x64 sheet):
                    // body 8x12x4 uv(16,16), head 8x8x8 uv(0,0),
                    // arm 4x12x4 uv(40,16), leg 4x12x4 uv(0,16).
                    body = BoxBuilder.McNet(16, 16, 8, 12, 4);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(0, 16, 4, 12, 4);
                    break;
                case "zombie":
                    // Bedrock humanoid 1.8 geo (64x32 sheet): body 8x12x4
                    // uv(16,16), head 8x8x8 uv(0,0), arm 4x12x4 uv(40,16),
                    // leg 4x12x4 uv(0,16).
                    body = BoxBuilder.McNet(16, 16, 8, 12, 4);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(0, 16, 4, 12, 4);
                    break;
                case "skeleton":
                    // Bedrock skeleton 1.8 geo (64x32 sheet): same layout as
                    // zombie but limbs are 2x12x2 sticks - leg uv(0,16),
                    // arm uv(40,16). The 2px-wide rects are fully painted.
                    body = BoxBuilder.McNet(16, 16, 8, 12, 4);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(0, 16, 2, 12, 2);
                    break;
                case "villager":
                    // Bedrock villager v1.8 geo (64x64 sheet): head 8x10x8
                    // uv(0,0), robe body 8x12x6 uv(16,20), arms folded
                    // 8x4x4 uv(40,38), legs 4x12x4 uv(0,22).
                    body = BoxBuilder.McNet(16, 20, 8, 12, 6);
                    head = BoxBuilder.McNet(0, 0, 8, 10, 8);
                    leg = BoxBuilder.McNet(0, 22, 4, 12, 4);
                    break;
                case "salmon":
                    // Bedrock salmon geo (32x32 sheet): body_front 3x5x8
                    // uv(0,0), body_back 3x5x8 uv(0,13), tailfin 0x5x6
                    // uv(20,10), head 2x4x3 uv(22,0), fins 2x0x2 uv(2,0).
                    body = BoxBuilder.McNet(0, 0, 3, 5, 8);
                    head = BoxBuilder.McNet(22, 0, 2, 4, 3);
                    leg = BoxBuilder.McNet(2, 0, 2, 2, 2);
                    break;
                case "pufferfish":
                    // Bedrock pufferfish large geo (32x32 sheet): body 8x8x8
                    // uv(0,0), tailfin 6x6x1 uv(24,21), fins 2x1x2 uv(24,0)/
                    // (24,3), spines uv(0,16)/(14,16)/(14,19).
                    body = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(24, 0, 2, 1, 2);
                    break;
                case "axolotl":
                    // Bedrock axolotl geo (64x64 sheet): body 8x4x10
                    // uv(0,11) + tail fin 0x5x12 uv(2,19), head 8x5x5
                    // uv(0,1), gills 3x7x0 uv(0,40)/(11,40), legs 3x5x0
                    // uv(2,13).
                    body = BoxBuilder.McNet(0, 11, 8, 4, 10);
                    head = BoxBuilder.McNet(0, 1, 8, 5, 5);
                    leg = BoxBuilder.McNet(2, 13, 3, 5, 3);
                    break;
                case "mooshroom":
                    goto case "cow";
                default: // pig
                    body = BoxBuilder.QuadrupedBodyNet(28, 8, 10, 16, 8);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(0, 16, 4, 6, 4);
                    break;
            }
            bodyRot = (kind == "sheep" || kind == "wolf" || kind == "fox" || kind == "goat" || kind == "horse")
                ? new int[] { 0, 0, 0, 0, 0, 0 }
                : BoxBuilder.QuadrupedBodyRots;
        }

        /// <summary>Builds the box model. Call once after species is assigned.</summary>
        public void BuildModel()
        {
            GetDims(species, out Vector3 bodySize, out float legH, out Vector3 headBox, out float legThick);
            float totalHeight = legH + bodySize.y + headBox.y * 0.5f;

            var skin = CreatureTextureFactory.GetSkinMaterial(species + "_skin");
            bodyRoot = new GameObject("Body").transform;
            bodyRoot.SetParent(transform, false);

            // entity.json scripts.scale (polar_bear "1.2"): whole-entity
            // uniform scale from the data registry, applied on the root so
            // every bone/cube inherits it.
            var regDef = CreatureRegistry.Get(species);
            if (regDef != null && !string.IsNullOrEmpty(regDef.entityScale))
            {
                float es;
                if (float.TryParse(regDef.entityScale, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out es) && es > 0f)
                    bodyRoot.localScale = Vector3.one * es;
            }

            // Preferred path: build straight from the vanilla bedrock geo JSON
            // (the model source itself) - bones, pivots and cubes land exactly
            // where Mojang put them. Falls back to the hand-built nets below
            // when no geo file exists for this species.
            var geoAsset = Resources.Load<TextAsset>("Geo/" + species + ".geo");
            // Fox geo texture is 64x32 declared but padded skin shifts UVs and
            // the 45-deg pillar pose needs per-bone handling the generic
            // importer lacks - fox uses the hand-built (verified) path below.
            // Fox migrated to the geo pipeline: vanilla fox.geo stores the
            // body as an upright pillar; the classic 45-degree forward pitch
            // (vanilla fox stance) is applied via geoPitch by the importer.
            bool foxHandbuilt = false;
            float geoPitch = 0f; // vanilla fox.setup = "-this": body stays as authored (vertical column)
            // setup clip to bake (bedrock runtime applies it permanently);
            // loaded from the species' own animation file.
            TextAsset setupAnim = Resources.Load<TextAsset>("Anims/" + species + ".animation");
            if (!foxHandbuilt && skin != null && geoAsset != null &&
                BedrockGeoImporter.Build(bodyRoot, geoAsset, null, skin, geoPitch, setupAnim,
                    out var geoLegs, out var geoTail, out var geoHead, out var geoWings))
            {
                for (int i = 0; i < 4; i++) legs[i] = geoLegs[i];
                for (int i = 0; i < 2; i++) wings[i] = geoWings[i];
                tail = geoTail;
                if (tail != null) tailBaseRot = tail.localRotation;
                head = geoHead;

                // Official bedrock animation clips drive the same bone names.
                var animPlayer = gameObject.GetComponent<BedrockAnimationPlayer>();
                if (animPlayer == null) animPlayer = gameObject.AddComponent<BedrockAnimationPlayer>();
                animPlayer.species = species;
                animPlayer.clipsJson.Clear();
                // Clip library: the species file plus the shared families.
                // horse_v3 family: the horse/donkey entities reference
                // animation.horse.v3.* clips which live in horse_v3.animation
                // .json, not in horse.animation.json (legacy names). Load the
                // entity-referenced family files too - the entity json is the
                // single source of truth for clip names.
                var animFamily = new List<string> { species, "quadruped", "wolf", "humanoid", "player", "villager" };
                var edTa = Resources.Load<TextAsset>("EntityDefs/" + species + ".entity");
                if (edTa != null)
                {
                    object eo0 = null; try { eo0 = MiniJson.Deserialize(edTa.text); } catch { }
                    if (eo0 is Dictionary<string, object> ej && ej.TryGetValue("minecraft:client_entity", out var ce) &&
                        ce is Dictionary<string, object> ced && ced.TryGetValue("description", out var desc) &&
                        desc is Dictionary<string, object> dd && dd.TryGetValue("animations", out var am) &&
                        am is Dictionary<string, object> amd)
                        foreach (var clipName0 in amd.Values)
                        {
                            if (!(clipName0 is string clipName)) continue;
                            // animation.horse.v3.walk -> horse_v3 (family file)
                            if (!clipName.StartsWith("animation.", System.StringComparison.Ordinal)) continue;
                            var parts = clipName.Substring("animation.".Length).Split('.');
                            // animation.horse.v3.walk: family "horse",
                            // versioned sub-family "horse_v3" (the actual
                            // file on disk when vanilla splits versions).
                            for (int pi = 0; pi < parts.Length - 1; pi++)
                            {
                                var fam = string.Join("_", parts, 0, pi + 1);
                                if (!animFamily.Contains(fam)) animFamily.Add(fam);
                            }
                        }
                }
                foreach (var af in animFamily)
                {
                    var ta = Resources.Load<TextAsset>("Anims/" + af + ".animation");
                    if (ta != null) animPlayer.clipsJson.Add(ta);
                }
                animPlayer.LoadClips();
                // Setup clips (parrot "base") must be known BEFORE Bind():
                // Bind bakes them into the REST pose (BakeDefaultLegPose),
                // so the push has to happen before the bake runs.
                var reg = CreatureRegistry.Get(species);
                if (reg != null && reg.bakedSetupClips != null && reg.bakedSetupClips.Count > 0)
                    animPlayer.bakedSetupClips = new System.Collections.Generic.List<string>(reg.bakedSetupClips);
                if (reg != null && reg.bakedSetupPos != null && reg.bakedSetupPos.Count > 0)
                    animPlayer.bakedSetupPos = new System.Collections.Generic.List<string>(reg.bakedSetupPos);
                animPlayer.Bind(bodyRoot);
                string walkClip = reg != null ? reg.walkClip : "animation.quadruped.walk";
                // Per-species ground offset (spider geo pivots sit ~0.53m
                // above the leg contact point; data-driven correction).
                if (reg != null && !string.IsNullOrEmpty(reg.groundOffset))
                {
                    float go = float.Parse(reg.groundOffset,
                        System.Globalization.CultureInfo.InvariantCulture);
                    var bp = bodyRoot.localPosition;
                    bodyRoot.localPosition = new Vector3(bp.x, bp.y + go, bp.z);
                    UnityEngine.Debug.Log($"[GOF] {species} bodyRoot {bp.y:F3}->{bodyRoot.localPosition.y:F3}");
                }
                // Gait variable injection is registry-driven: the gait field
                // names the ENTITY-layer pre_animation family (goat tcos_*,
                // creeper leg_rot, horse leg_x_rot_anim, steve tcos0). New
                // species with a known gait need zero code here.
                // Engine-variable gait families (rabbit jump_rotation) have
                // NO pre_animation source - the variable is engine-side, so
                // the flag synthesizer stays on even in controller mode.
                if (reg != null && reg.controllers &&
                    (reg.gait == "rabbit" || reg.gait == "var:jump_rotation"))
                    animPlayer.rabbitGait = true;
                // Aquatic species (fish/axolotl, locomotion=swim): engine
                // answers is_in_water=1 so fish.general keeps swimming and
                // ZRot flop stays 0; fish clips read the synthesized
                // animationamountblend phase (engine-side, like rabbit).
                if (reg != null && reg.locomotion == "swim")
                {
                    animPlayer.inWater = true;
                    if (reg.gait == "var:animationamountblend" || reg.gait == "fish")
                        animPlayer.fishGait = true;
                }
                if (reg != null && !string.IsNullOrEmpty(reg.gait) && !reg.controllers)
                {
                    switch (reg.gait)
                    {
                        case "goat": case "var:tcos": animPlayer.goatGait = true; break;
                        case "creeper": case "var:leg_rot": animPlayer.creeperGait = true; break;
                        case "horse": case "var:leg_x_rot_anim": animPlayer.horseGait = true; break;
                        case "rabbit": case "var:jump_rotation": animPlayer.rabbitGait = true; break;
                        case "steve": case "var:tcos0": animPlayer.steveGait = true; break;
                        case "fish": case "var:animationamountblend": animPlayer.fishGait = true; break;
                    }
                    if (reg.gait != "none" && reg.gait != "dist-cos")
                        animPlayer.walkSpeedRef = walkSpeed;
                }
                // walkSpeed/gaitWeight apply in BOTH modes (data-layer tuning
                // of the shared gait engine; the controller path still uses
                // the player's distance clock and gait weight).
                if (reg != null && !string.IsNullOrEmpty(reg.walkSpeed))
                    animPlayer.walkSpeedRef = float.Parse(reg.walkSpeed,
                        System.Globalization.CultureInfo.InvariantCulture);
                if (reg != null && !string.IsNullOrEmpty(reg.gaitWeight))
                    animPlayer.gaitWeightTarget = float.Parse(reg.gaitWeight,
                        System.Globalization.CultureInfo.InvariantCulture);
                if (reg == null || !reg.controllers)
                {
                    animPlayer.Play(walkClip);
                    if (reg != null && reg.extraVariables != null)
                        foreach (var ekv in reg.extraVariables)
                            animPlayer.variables[ekv.Key] = ekv.Value;
                    if (reg != null && reg.extraClips != null)
                        foreach (var extra in reg.extraClips)
                        {
                            // Setup clips (parrot "base") are baked into the REST
                            // pose, not played: a looping extraClip re-applies its
                            // constant channels every tick and overwrites the walk
                            // clip (parrot wings frozen, legs buried by -6px).
                            if (reg.bakedSetupClips != null && reg.bakedSetupClips.Contains(extra))
                                continue;
                            animPlayer.Play(extra);
                        }
                }
                else
                {
                    // controller-driven: pre_animation variables from the
                    // entity json (gait var family) run every tick in Update.
                    if (reg.extraVariables != null)
                        foreach (var ekv in reg.extraVariables)
                            animPlayer.variables[ekv.Key] = ekv.Value;
                    if (reg != null && !string.IsNullOrEmpty(reg.walkSpeed))
                        animPlayer.walkSpeedRef = float.Parse(reg.walkSpeed,
                            System.Globalization.CultureInfo.InvariantCulture);
                }

                // NOTE: species ".setup" clips (wolf/pig "-this" re-roots) are
                // NOT played: they exist to convert bedrock's 1.8 bind pose,
                // which BedrockGeoImporter already applies via
                // bind_pose_rotation + leg re-rooting.
                geoAnimPlayer = animPlayer;

                // B-plan: controller-driven species (registry "controllers":
                // true) get the vanilla state machine; the legacy
                // walk+extraClips scheduling above is then skipped.
                if (reg != null && reg.controllers)
                {
                    var acTa = Resources.Load<TextAsset>("AnimControllers/" + species + ".animation_controllers");
                    // Shared-family fallback (M25 fish): salmon/pufferfish
                    // reference controller.animation.fish.general which lives
                    // in fish.animation_controllers.json, not per-species files
                    // (vanilla ships one AC file per family).
                    if (acTa == null)
                        foreach (var fam in new[] { "fish" })
                        {
                            var fTa = Resources.Load<TextAsset>("AnimControllers/" + fam + ".animation_controllers");
                            if (fTa == null) continue;
                            object fAo0 = null; try { fAo0 = MiniJson.Deserialize(fTa.text); } catch { }
                            if (fAo0 is Dictionary<string, object> fJson &&
                                fJson.TryGetValue("animation_controllers", out object fAo) &&
                                fAo is Dictionary<string, object> fAd)
                            {
                                // adopt only controllers the entity references
                                var eTa0 = Resources.Load<TextAsset>("EntityDefs/" + species + ".entity");
                                if (eTa0 != null)
                                {
                                    object eo1 = null; try { eo1 = MiniJson.Deserialize(eTa0.text); } catch { }
                                    if (eo1 is Dictionary<string, object> eJson1 &&
                                        eJson1.TryGetValue("minecraft:client_entity", out object ce1) &&
                                        ce1 is Dictionary<string, object> ced1 &&
                                        ced1.TryGetValue("description", out object desc1) &&
                                        desc1 is Dictionary<string, object> dd1 &&
                                        dd1.TryGetValue("animations", out object an1) &&
                                        an1 is Dictionary<string, object> anims1)
                                        foreach (var kv in anims1)
                                            if (kv.Value is string vs && vs.StartsWith("controller.animation.fish."))
                                            { acTa = fTa; break; }
                                }
                            }
                            if (acTa != null) break;
                        }
                    var eTa = Resources.Load<TextAsset>("EntityDefs/" + species + ".entity");
                    Dictionary<string, object> acDefs = null, eDesc = null;
                    if (acTa != null)
                    {
                        object ao0 = null; try { ao0 = MiniJson.Deserialize(acTa.text); } catch { }
                        var acJson = ao0 as Dictionary<string, object>;
                        if (acJson != null && acJson.TryGetValue("animation_controllers", out object ao) && ao is Dictionary<string, object> ad)
                            acDefs = ad;
                    }
                    if (eTa != null)
                    {
                        object eo0 = null; try { eo0 = MiniJson.Deserialize(eTa.text); } catch { }
                        var eJson = eo0 as Dictionary<string, object>;
                        if (eJson != null && eJson.TryGetValue("minecraft:client_entity", out object ce) &&
                            ce is Dictionary<string, object> ced && ced.TryGetValue("description", out object desc) &&
                            desc is Dictionary<string, object> dd)
                            eDesc = dd;
                    }
                    controllers = new BedrockControllerRuntime(animPlayer, acDefs, eDesc);
                    // Generic pre_animation: feed the entity's script lines to
                    // the player; the flag-based gait synthesizers stay off
                    // for controller species (single source of truth).
                    if (eDesc != null &&
                        eDesc.TryGetValue("scripts", out object sc2) && sc2 is Dictionary<string, object> scd)
                    {
                        if (scd.TryGetValue("pre_animation", out object pre) && pre is List<object> preList)
                            foreach (var pl in preList)
                                if (pl is string pls && !string.IsNullOrWhiteSpace(pls))
                                {
                                    // String-property comparisons become the
                                    // numeric property_eq(key,val) (Molang has
                                    // no string values; the ENGINE owns the
                                    // current property string). Default state
                                    // table lives in the entity-role registrar.
                                    pls = System.Text.RegularExpressions.Regex.Replace(pls,
                                        @"query\.property\('([^']+)'\)\s*([!=]=)\s*'([^']+)'",
                                        m => (m.Groups[2].Value == "=="
                                            ? $"property_eq('{m.Groups[1].Value}','{m.Groups[3].Value}')"
                                            : $"(1 - property_eq('{m.Groups[1].Value}','{m.Groups[3].Value}'))"));
                                    animPlayer.preAnimation.Add(pls);
                                }
                        // scripts.initialize: constant seeds run ONCE (vanilla
                        // semantics), not per-frame - parse "variable.x = num;"
                        // and seed the player variable store.
                        if (scd.TryGetValue("initialize", out object init) && init is List<object> initList)
                            foreach (var il in initList)
                                if (il is string ils)
                                {
                                    var m = System.Text.RegularExpressions.Regex.Match(ils,
                                        @"^\s*variable\.([A-Za-z0-9_]+)\s*=\s*(-?[0-9]+(?:\.[0-9]+)?)\s*;?\s*$");
                                    if (m.Success)
                                        animPlayer.variables[m.Groups[1].Value.ToLowerInvariant()] =
                                            float.Parse(m.Groups[2].Value,
                                                System.Globalization.CultureInfo.InvariantCulture);
                                }
                    }
                }
                // Ambient behaviours (wolf sit/shake, sheep graze, fox sit/sleep)
                if (gameObject.GetComponent<BehaviourBrain>() == null)
                    gameObject.AddComponent<BehaviourBrain>();

                // Collider from the imported bounds (feet-origin model).
                var rends = GetComponentsInChildren<Renderer>();
                if (rends.Length > 0)
                {
                    var b = rends[0].bounds;
                    foreach (var r in rends) b.Encapsulate(r.bounds);
                    totalHeight = b.size.y;
                }
                var geoCapsule = gameObject.AddComponent<CapsuleCollider>();
                geoCapsule.center = new Vector3(0f, totalHeight * 0.5f, 0f);
                geoCapsule.height = totalHeight + 0.15f;
                geoCapsule.radius = Mathf.Max(bodySize.x, 0.22f) * 0.6f;

                smoothY = transform.position.y;
                // Spawn yaw must only be applied at RUNTIME spawn: editor
                // verification/snapshot harnesses build models at identity
                // orientation (a random yaw rotated the wolf 90deg sideways
                // in every snapshot - the "floating head" false alarm).
#if !UNITY_EDITOR
                targetYaw = Random.Range(0f, 360f);
                transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
#endif
                return;
            }

            // Head placement per vanilla model data (bedrock-samples geometry,
            // px/16 m, z flipped so +Z is our front). The generic formula put
            // the pig head 5 px too high, which scrambled single-view skin
            // recovery - head positions are vanilla-measured now:
            //   pig:     head y 8..16 px  -> centre 0.75 m, 1 px z-gap past body
            //   chicken: head y 9..15 px  -> centre 0.75 m, 1 px z-sink into body
            Vector3 headPos;
            if (species == "pig")
                headPos = new Vector3(0f, legH + 0.125f + headBox.y * 0.5f,
                    bodySize.z * 0.5f + headBox.z * 0.5f + 0.0625f);
            else if (species == "chicken")
                headPos = new Vector3(0f, legH + bodySize.y * 0.5f + 0.25f,
                    bodySize.z * 0.5f + headBox.z * 0.5f - 0.0625f);
            else if (species == "wolf")
                // Vanilla wolf (bedrock geo, +Z front): head 6x6x4 spans
                // y 7.5..13.5 px. Bedrock leaves a 4px gap between head
                // rear (z 5) and mane front (z 1) - reads as a floating
                // head, so sink the head rear 2px INTO the mane (seams
                // must overlap, never coplanar - z-fight rule).
                headPos = new Vector3(0f, 0.656f, 0.3125f);
            else if (species == "fox")
                // Vanilla fox: head 8x6x6 spans y 4..10 px, sticking out
                // in front of the leaned body pillar - a LOW head.
                headPos = new Vector3(0f, 0.4375f, 0.375f);
            else
                headPos = new Vector3(0f, legH + bodySize.y + headBox.y * 0.4f,
                    bodySize.z * 0.5f + headBox.z * 0.45f);

            if (skin != null)
            {
                // Real MC-format skin: standard quadruped nets.
                GetSpeciesNets(species, out var bodyNet, out var bodyRot, out var headNet, out var legNet);

                // Torso placement. Most quadrupeds: horizontal box resting on
                // the legs. Wolf (bedrock wolf.geo.json): tall UPRIGHT chest,
                // centre y 7.5 px / z +2 px. Fox: body pillar pitched -36 deg
                // (Java FoxModel body xRot) inside its own frame, so the box
                // leans nose-down while the head/legs stay level.
                Transform torso = bodyRoot;
                Vector3 bodyCenter = new Vector3(0f, legH + bodySize.y * 0.5f, 0f);
                if (species == "wolf")
                    bodyCenter = new Vector3(0f, 0.46875f, -0.125f); // y 3..12 px, z -1..5 px
                else if (species == "fox")
                {
                    var fb = new GameObject("FoxBody").transform;
                    fb.SetParent(bodyRoot, false);
                    fb.localPosition = new Vector3(0f, 0.5f, 0f);      // bedrock pivot (0,8,0)
                    // Java FoxModel: body.xRot = PI/4 (45 deg) - the geo rest
                    // pose is a vertical pillar, tipped nose-down when standing
                    fb.localRotation = Quaternion.Euler(-45f, 0f, 0f);
                    torso = fb;
                    bodyCenter = new Vector3(0f, -0.15625f, 0f);      // cube centre rel. pivot
                }
                BoxBuilder.SkinnedBox(torso, "Body", bodyCenter, bodySize,
                    skin, bodyNet, 64, 32, bodyRot);
                head = BoxBuilder.SkinnedBox(bodyRoot, "Head", headPos,
                    headBox, skin, headNet, 64, 32).transform;
                if (species == "pig")
                {
                    // Vanilla pig snout (bedrock pig.geo.json): 4x3x1 px slab,
                    // uv (16,16), hanging 1 px proud of the head front at the
                    // face's lower half. Sunk 14mm so the back face is never
                    // coplanar with the head front (z-fighting).
                    AddSkinnedChild(head, "Snout",
                        new Vector3(0f, -headBox.y * 0.1875f, headBox.z * 0.5f + 0.017f),
                        new Vector3(0.25f, 0.1875f, 0.0625f), skin, BoxBuilder.McNet(16, 16, 4, 3, 1));
                }

                if (species == "chicken")
                {
                    // Vanilla beak (bedrock chicken.geo.json): 4x2x2 px box at
                    // uv (14,0). Sunk 18mm into the head so the back face is
                    // never coplanar with the head front face (z-fighting).
                    AddSkinnedChild(head, "Beak",
                        new Vector3(0f, -headBox.y * 0.18f, headBox.z * 0.5f + 0.01325f),
                        new Vector3(0.25f, 0.125f, 0.125f), skin, BoxBuilder.McNet(14, 0, 4, 2, 2));
                    // Vanilla red comb: 2x2x2 px box on TOP of the head
                    // (bedrock uv (14,4); Java ChickenModel CombLayer).
                    AddSkinnedChild(head, "Comb",
                        new Vector3(0f, headBox.y * 0.5f + 0.0625f, headBox.z * 0.25f),
                        new Vector3(0.125f, 0.125f, 0.125f), skin, BoxBuilder.McNet(14, 4, 2, 2, 2));
                    // Vanilla red wattle: 2x2x2 px box hanging below the beak
                    // (sheet red block measured at net (2,5); the old (15,2)
                    // sampled beak orange + stray pixels - face garbage root cause).
                    AddSkinnedChild(head, "Wattle",
                        new Vector3(0f, -headBox.y * 0.5f - 0.055f, headBox.z * 0.5f - 0.01f),
                        new Vector3(0.125f, 0.125f, 0.125f), skin, BoxBuilder.McNet(2, 5, 2, 2, 2));
                }

                if (species == "wolf")
                {
                    // Vanilla wolf extras (bedrock wolf.geo.json):
                    // snout 3x3x4 uv(0,10) on the head front (z -12..-8 px),
                    // mane 8x6x7 uv(21,0) capping y 7..13 px / z -1..6 px,
                    // ears 2x2x1 uv(16,14) on the head top, tail 2x8x2
                    // uv(9,18) angled up from the body rear (z 7..9 px).
                    AddSkinnedChild(head, "Snout",
                        new Vector3(0f, -0.0234375f, headBox.z * 0.5f + 0.125f),
                        new Vector3(0.1875f, 0.1875f, 0.25f), skin, BoxBuilder.McNet(0, 10, 3, 3, 4));
                    for (int e = 0; e < 2; e++)
                    {
                        float es = e == 0 ? -0.125f : 0.125f;
                        AddSkinnedChild(head, "Ear" + e,
                            new Vector3(es, headBox.y * 0.5f + 0.0625f, -0.03125f),
                            new Vector3(0.125f, 0.125f, 0.0625f), skin, BoxBuilder.McNet(16, 14, 2, 2, 1));
                    }
                    AddSkinnedChild(bodyRoot, "Mane",
                        new Vector3(0f, 0.625f, -0.15625f),
                        new Vector3(0.5f, 0.375f, 0.4375f), skin, BoxBuilder.McNet(21, 0, 8, 6, 7));
                    // Tail hinges at its base so Update() can wag it.
                    var tailHinge = new GameObject("TailHinge");
                    tailHinge.transform.SetParent(bodyRoot, false);
                    tailHinge.transform.localPosition = new Vector3(0f, 0.75f, -0.5f);
                    tailHinge.transform.localRotation = Quaternion.Euler(120f, 0f, 0f);
                    AddSkinnedChild(tailHinge.transform, "Tail",
                        new Vector3(0f, -0.25f + 0.03125f, 0f),
                        new Vector3(0.125f, 0.5f, 0.125f), skin, BoxBuilder.McNet(9, 18, 2, 8, 2));
                    tail = tailHinge.transform;
                    tailBaseRot = tailHinge.transform.localRotation;
                }

                if (species == "fox")
                {
                    // Vanilla fox extras - offsets pixel-fitted to fox.png:
                    // snout (6,18): front=(9,21,4,2) white muzzle + black
                    // nose; ears (8,1)/(15,1) - y0 row is transparent so the
                    // geo-declared (0,0)/(22,0) sampled blank; tail (30,0):
                    // top face holds the white tip.
                    AddSkinnedChild(head, "Snout",
                        new Vector3(0f, -headBox.y * 0.2f, headBox.z * 0.5f + 0.09375f),
                        new Vector3(0.25f, 0.125f, 0.1875f), skin, BoxBuilder.McNet(6, 18, 4, 2, 3));
                    for (int e = 0; e < 2; e++)
                    {
                        float es = e == 0 ? -0.1875f : 0.1875f;
                        AddSkinnedChild(head, "Ear" + e,
                            new Vector3(es, headBox.y * 0.5f + 0.0625f, -0.03125f),
                            new Vector3(0.125f, 0.125f, 0.0625f), skin,
                            BoxBuilder.McNet(e == 0 ? 8 : 15, 1, 2, 2, 1));
                    }
                    // Tail hinges where it meets the body rear so Update()
                    // can sway it.
                    var foxTailGo = new GameObject("FoxTailHinge");
                    foxTailGo.transform.SetParent(bodyRoot, false);
                    foxTailGo.transform.localPosition = new Vector3(0f, 0.625f, -0.28125f);
                    foxTailGo.transform.localRotation = Quaternion.Euler(100f, 0f, 0f);
                    AddSkinnedChild(foxTailGo.transform, "Tail",
                        new Vector3(0f, -0.28125f + 0.03125f, 0f),
                        new Vector3(0.25f, 0.5625f, 0.3125f), skin, BoxBuilder.McNet(30, 0, 4, 9, 5));
                    tail = foxTailGo.transform;
                    tailBaseRot = foxTailGo.transform.localRotation;
                }

                if (species == "mooshroom")
                {
                    // Vanilla mooshroom mushrooms (bedrock geo dump): 4x6x1
                    // slabs uv(52,0) on the FRONT face sides of the body,
                    // y 11..17 px, x ±2 px from centre.
                    for (int m = 0; m < 2; m++)
                    {
                        float ms = m == 0 ? -0.1875f : 0.1875f;
                        AddSkinnedChild(bodyRoot, "Mushroom" + m,
                            new Vector3(ms, 0.875f, 0.375f),
                            new Vector3(0.25f, 0.375f, 0.0625f), skin, BoxBuilder.McNet(52, 0, 4, 6, 1));
                    }
                }

                if (species == "chicken")
                {
                    // Vanilla 1x4x6 px wings flush against the body sides
                    // (bedrock sheet rect 24,13). Their inward faces are
                    // backfaces against the body side, so no z-fighting there.
                    var wingNet = BoxBuilder.McNet(24, 13, 1, 4, 6);
                    float bodyTop = legH + bodySize.y;
                    for (int s = 0; s < 2; s++)
                    {
                        float side = s == 0 ? -1f : 1f;
                        // Hinge the wing at its TOP (vanilla flaps rotate about
                        // the top edge): child pivot sits at the wing top, the
                        // box hangs below it, so Update() can flap it outward.
                        var hingeGo = new GameObject("WingHinge" + s);
                        hingeGo.transform.SetParent(bodyRoot, false);
                        hingeGo.transform.localPosition = new Vector3(
                            side * (bodySize.x * 0.5f + 0.0325f), bodyTop - 0.0625f, 0f);
                        AddSkinnedChild(hingeGo.transform, "Wing",
                            new Vector3(0f, -0.125f, 0f),
                            new Vector3(0.0625f, 0.25f, 0.375f), skin, wingNet);
                        wings[s] = hingeGo.transform;
                    }
                }

                // Quadrupeds: 4 hips at the body corners. The chicken is a
                // biped: vanilla legs are 3 px wide, centered x=-3..0 and
                // 0..3 px (bedrock leg0/leg1), i.e. hips at x=+-1.5 px.
                bool biped = species == "chicken";
                Vector2[] hipXZ;
                if (species == "wolf")
                    hipXZ = new[] // bedrock z flipped: FRONT pair (near head) +4px, HIND pair (near tail) -7px
                    {
                        new Vector2(-0.09375f, 0.25f), new Vector2(0.09375f, 0.25f),
                        new Vector2(-0.09375f, -0.4375f), new Vector2(0.09375f, -0.4375f),
                    };
                else if (species == "fox")
                    hipXZ = new[] // bedrock z flipped: FRONT pair +1px, HIND pair -6px
                    {
                        new Vector2(-0.125f, 0.0625f), new Vector2(0.125f, 0.0625f),
                        new Vector2(-0.125f, -0.375f), new Vector2(0.125f, -0.375f),
                    };
                else if (biped)
                    hipXZ = new[] { new Vector2(-0.09375f, 0f), new Vector2(0.09375f, 0f) };
                else
                    hipXZ = new[]
                    {
                        new Vector2(-bodySize.x * 0.32f, -bodySize.z * 0.32f),
                        new Vector2(bodySize.x * 0.32f, -bodySize.z * 0.32f),
                        new Vector2(-bodySize.x * 0.32f, bodySize.z * 0.32f),
                        new Vector2(bodySize.x * 0.32f, bodySize.z * 0.32f),
                    };
                for (int i = 0; i < hipXZ.Length; i++)
                {
                    var hip = new GameObject("Hip" + i).transform;
                    hip.SetParent(bodyRoot, false);
                    hip.localPosition = new Vector3(hipXZ[i].x, legH, hipXZ[i].y);
                    // Top sunk 12mm into the body so the leg-top face is never
                    // coplanar with the body-bottom face (z-fighting).
                    BoxBuilder.SkinnedBox(hip, "Leg", new Vector3(0f, -legH * 0.5f + 0.012f, 0f),
                        new Vector3(legThick, legH, legThick), skin, legNet, 64, 32);
                    // Vanilla-style flat foot: 3x1x2 px slab at the leg bottom,
                    // spreading forward; parented to the hip so it swings too.
                    if (biped)
                    {
                        AddSkinnedChild(hip, "Foot",
                            new Vector3(0f, -legH + 0.031f, 0.075f),
                            new Vector3(0.1875f, 0.0625f, 0.125f), skin, legNet);
                    }
                    legs[i] = hip;
                }
            }
            else
            {
                // Procedural fallback (original look).
                BoxBuilder.Box(bodyRoot, "Body", new Vector3(0f, legH + bodySize.y * 0.5f, 0f), bodySize,
                    CreatureTextureFactory.Get(species, "body"));
                head = BoxBuilder.Box(bodyRoot, "Head",
                    new Vector3(0f, legH + bodySize.y + headBox.y * 0.4f, bodySize.z * 0.5f + headBox.z * 0.45f),
                    headBox, CreatureTextureFactory.Get(species, "face")).transform;

                var legMat = CreatureTextureFactory.Get(species, "leg");
                bool bipedF = species == "chicken";
                Vector2[] hipXZ = bipedF
                    ? new[] { new Vector2(-0.09375f, 0f), new Vector2(0.09375f, 0f) }
                    : new[]
                    {
                        new Vector2(-bodySize.x * 0.32f, -bodySize.z * 0.32f),
                        new Vector2(bodySize.x * 0.32f, -bodySize.z * 0.32f),
                        new Vector2(-bodySize.x * 0.32f, bodySize.z * 0.32f),
                        new Vector2(bodySize.x * 0.32f, bodySize.z * 0.32f),
                    };
                for (int i = 0; i < hipXZ.Length; i++)
                {
                    var hip = new GameObject("Hip" + i).transform;
                    hip.SetParent(bodyRoot, false);
                    hip.localPosition = new Vector3(hipXZ[i].x, legH, hipXZ[i].y);
                    var leg = BoxBuilder.Box(hip, "Leg", new Vector3(0f, -legH * 0.5f + 0.012f, 0f),
                        new Vector3(legThick, legH, legThick), legMat);
                    legs[i] = hip;
                }
            }

            // Capsule collider: the player's CharacterController collides with this,
            // and sibling animals use it for separation probes.
            var capsule = gameObject.AddComponent<CapsuleCollider>();
            capsule.center = new Vector3(0f, totalHeight * 0.5f, 0f);
            capsule.height = totalHeight + 0.15f;
            capsule.radius = Mathf.Max(bodySize.x, 0.22f) * 0.6f;

            smoothY = transform.position.y;
            targetYaw = Random.Range(0f, 360f);
            transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
        
        // Shared tail of both build paths: ambient behaviours. The geo path
        // already attached a player above; the hand-built path (fox) gets a
        // fresh one with the species' own official clips loaded so the
        // behaviour brain can play sit/sleep/graze.
        {
            // Hand-built species (fox) share the geo pipeline's animation
            // stack: official species clips + quadruped locomotion, so the
            // behaviour brain can play sit/sleep and the gait uses the
            // official quadruped.walk clip.
            var playerX = gameObject.GetComponent<BedrockAnimationPlayer>();
            if (playerX == null)
            {
                playerX = gameObject.AddComponent<BedrockAnimationPlayer>();
                foreach (var af in new[] { species, "quadruped" })
                {
                    var ta = Resources.Load<TextAsset>("Anims/" + af + ".animation");
                    if (ta != null) playerX.clipsJson.Add(ta);
                }
                playerX.LoadClips();
                playerX.Bind(bodyRoot);
                playerX.Play("animation.quadruped.walk");
                geoAnimPlayer = playerX;
            }
            // v1.21 geos (fox) are authored UPRIGHT; the vanilla entity layer
            // keeps ".setup" resident, but its "-this" semantics means ZERO
            // net change on an already-clean bind - playing it absolutely
            // here would double-subtract the bind angle. The quadruped walk
            // + behaviour clips position the body correctly on their own.
            // (Verified: hd2 bind pose is the vanilla upright rest.)
            if (gameObject.GetComponent<BehaviourBrain>() == null)
                gameObject.AddComponent<BehaviourBrain>();
        }
}

        /// <summary>
        /// SkinnedBox child that stays <paramref name="size"/> in world space:
        /// SkinnedBox sets localScale = size, so a box parented to ANOTHER
        /// skinned box (e.g. the beak on the head) would inherit the parent's
        /// scale and shrink to a sliver. Dividing BOTH the local position and
        /// the local scale by the parent's lossy scale cancels that out, so
        /// <paramref name="localPos"/> and <paramref name="size"/> are world
        /// meters relative to the parent's center.
        /// </summary>
        private static void AddSkinnedChild(Transform parent, string name, Vector3 localPos,
            Vector3 size, Material skin, Vector4[] net)
        {
            var go = BoxBuilder.SkinnedBox(parent, name, localPos, size, skin, net, 64, 32);
            var p = parent.lossyScale;
            go.transform.localPosition = new Vector3(localPos.x / p.x, localPos.y / p.y, localPos.z / p.z);
            go.transform.localScale = new Vector3(size.x / p.x, size.y / p.y, size.z / p.z);
        }

        private static void GetDims(string kind, out Vector3 bodySize, out float legH, out Vector3 headBox, out float legThick)
        {
            switch (kind)
            {
                case "cow":
                    bodySize = new Vector3(0.75f, 0.625f, 1.125f); // 12 x 10 x 18 px
                    legH = 0.75f;
                    headBox = new Vector3(0.5f, 0.5f, 0.375f);     // 8 x 8 x 6 px
                    legThick = 0.25f;
                    break;
                case "sheep":
                    bodySize = new Vector3(0.5f, 0.375f, 1.0f);    // 8 x 6 x 16 px
                    legH = 0.75f;
                    headBox = new Vector3(0.375f, 0.375f, 0.375f); // 6 x 6 x 6 px
                    legThick = 0.25f;
                    break;
                case "chicken":
                    // Vanilla chicken (bedrock geo): body 6x8x6 rot90 at
                    // y 4..12 px, legs 3x5x3 at y 0..5, head 4x6x3 at y 9..15.
                    bodySize = new Vector3(0.375f, 0.5f, 0.375f);  // 6 x 8 x 6 px
                    legH = 0.3125f;                                 // 5 px legs
                    headBox = new Vector3(0.25f, 0.375f, 0.1875f); // 4 x 6 x 3 px
                    legThick = 0.1875f;                             // 3 px vanilla legs
                    break;
                case "wolf":
                    // Vanilla wolf: body 6x9x6 + mane 8x6x7, head 6x6x4 + snout,
                    // legs 2x8x2, tail 2x8x2 (bedrock wolf.geo.json).
                    bodySize = new Vector3(0.375f, 0.5625f, 0.375f); // 6 x 9 x 6 px
                    legH = 0.5f;                                     // 8 px legs
                    headBox = new Vector3(0.375f, 0.375f, 0.25f);    // 6 x 6 x 4 px
                    legThick = 0.125f;                               // 2 px legs
                    break;
                case "fox":
                    // Vanilla fox: body 6x11x6 (head-end pivot), head 8x6x6 +
                    // 4x2x3 snout, legs 2x6x2, big tail 4x9x5 (48x32 sheet).
                    bodySize = new Vector3(0.375f, 0.6875f, 0.375f); // 6 x 11 x 6 px
                    legH = 0.375f;                                   // 6 px legs
                    headBox = new Vector3(0.5f, 0.375f, 0.375f);     // 8 x 6 x 6 px
                    legThick = 0.125f;                               // 2 px legs
                    break;
                case "mooshroom":
                    goto case "cow";
                default: // pig
                    bodySize = new Vector3(0.625f, 0.5f, 1.0f);    // 10 x 8 x 16 px
                    legH = 0.375f;
                    headBox = new Vector3(0.5f, 0.5f, 0.5f);       // 8 x 8 x 8 px
                    legThick = 0.25f;
                    break;
            }
        }

        /// <summary>Sword hit: knockback plus damage; death drops meat as world entities.</summary>
        public void TakeHit(Vector3 attackDirection, Items.ToolType tool)
        {
            if (dead)
            {
                return;
            }
            float damage = tool == Items.ToolType.Sword ? 1f : 0.34f;
            health -= damage;
            Vector3 push = attackDirection;
            push.y = 0f;
            push.Normalize();
            transform.position += push * 0.55f;
            targetYaw = Quaternion.LookRotation(push).eulerAngles.y; // flee away from the attacker
            walking = true;
            stateTimer = Mathf.Max(stateTimer, 2.5f);
            walkSpeed = 2.6f;
            if (health <= 0f)
            {
                dead = true;
                int drops = 1 + (Random.value < 0.5f ? 1 : 0);
                ItemDrops.SpawnMeat(transform.position + Vector3.up * 0.5f, drops);
                Destroy(gameObject);
            }
        }

        /// <summary>Public controller tick for batch harnesses (Update never
        /// runs in batchmode). Same state feeding as Update does.</summary>
        public void TickControllers(float dt, bool moving)
        {
            if (controllers == null || geoAnimPlayer == null) return;
            BedrockControllerRuntime.SetDt(dt);
            // Aquatic species (M25): fish live in water - keeps the fish
            // controller in its swimming state (flop only on land), and swim
            // mid-water (axolotl move.v2 picks swim only when !is_on_ground;
            // grounded picks walk_floor_water).
            var regW = CreatureRegistry.Get(species);
            if (regW != null && regW.locomotion == "swim")
            {
                controllers.State.isOnGround = 0f;
                controllers.State.isInWater = aquaticDryLand ? 0f : 1f;
            }
            else
            {
                controllers.State.isOnGround = 1f;
                controllers.State.isInWater = 0f;
            }
            controllers.State.hasTarget = 0f;
            // Engine role: ocelot-family variable.state (behavior-layer
            // locomotion selector, vanilla: 0 sneak/1 sprint/2 sit/3 walk).
            // Harness/AI walks => 3; idle keeps sitting for sit-clip species.
            if (!controllers.State.variables.ContainsKey("state") ||
                controllers.State.variables["state"] == 2f && moving ||
                controllers.State.variables["state"] == 3f && !moving)
                controllers.State.variables["state"] = moving ? 3f : 2f;
            // Molang scope is SHARED (eyelib single scope): pre_animation
            // assignments in player.variables must be visible to controller
            // conditions - sync them into the runtime state store each tick.
            foreach (var kv in geoAnimPlayer.variables)
                controllers.State.variables[kv.Key] = kv.Value;
            controllers.State.variables["gliding_speed_value"] =
                geoAnimPlayer.variables.TryGetValue("gliding_speed_value", out var gsv) ? gsv : 1.0f;
            controllers.State.variables["attack_time"] = -1f;
            // query.modified_move_speed value domain is NORMALIZED (bedrock-
            // wiki queries.md: player walking ≈0.86, sprinting = 1.0) - NOT a
            // raw ratio to re-derive per frame. The harness moves species at
            // full walk speed, so moving => 1.0 (sprint-equivalent), idle => 0.
            // This keeps {walk: query.modified_move_speed} entries behavior-
            // equivalent to the legacy gaitWeight 0.3 baseline (gates at golden).
            controllers.State.modifiedMoveSpeed = moving ? 1f : 0f;
            controllers.Tick(dt);
        }

        private void Update()
        {
            if (bodyRoot == null || world == null || world.sim == null || dead)
            {
                return;
            }

            stateTimer -= Time.deltaTime;
            if (stateTimer <= 0f)
            {
                walking = !walking && Random.value > 0.35f;
                if (walking)
                {
                    targetYaw = Random.Range(0f, 360f);
                    stateTimer = Random.Range(3f, 7f);
                }
                else
                {
                    stateTimer = Random.Range(2f, 5f);
                }
            }

            float move = 0f;
            if (walking)
            {
                Vector3 pos = transform.position;
                if (playerRef != null)
                {
                    Vector3 toPlayer = playerRef.position - pos;
                    toPlayer.y = 0f;
                    if (toPlayer.magnitude < 1.4f)
                    {
                        targetYaw = Quaternion.LookRotation(-toPlayer).eulerAngles.y + Random.Range(-30f, 30f);
                    }
                }
                int overlaps = Physics.OverlapSphereNonAlloc(pos, 1.0f, overlapBuffer);
                for (int o = 0; o < overlaps; o++)
                {
                    var other = overlapBuffer[o];
                    if (other == null || other.gameObject == gameObject ||
                        !other.TryGetComponent(out BlockyAnimal _))
                    {
                        continue;
                    }
                    Vector3 away = pos - other.transform.position;
                    away.y = 0f;
                    if (away.magnitude < 1.0f)
                    {
                        targetYaw = Quaternion.LookRotation(away).eulerAngles.y + Random.Range(-20f, 20f);
                    }
                }

                var rot = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, targetYaw, 0f), Time.deltaTime * 3f);
                transform.rotation = rot;
                Vector3 forward = transform.forward;
                var next = pos + forward * (walkSpeed * Time.deltaTime);

                int gx = Mathf.FloorToInt(next.x);
                int gz = Mathf.FloorToInt(next.z);
                int ground = world.sim.SurfaceHeight(gx, gz, ignoreTrees: true);
                int currentGround = world.sim.SurfaceHeight(
                    Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z), ignoreTrees: true);

                bool ok = ground >= VoxelMath.SeaLevel && ground >= 0;
                if (ok)
                {
                    var surfaceBlock = world.sim.GetBlock(gx, ground, gz);
                    var stepBlock = world.sim.GetBlock(gx, ground + 1, gz);
                    if (surfaceBlock == BlockType.Air || surfaceBlock == BlockType.Water ||
                        stepBlock == BlockType.Water ||
                        BlockDatabase.IsSolid(stepBlock) ||
                        Mathf.Abs(ground - currentGround) > 1)
                    {
                        ok = false;
                    }
                }
                if (!ok)
                {
                    targetYaw = Random.Range(0f, 360f);
                }
                else
                {
                    smoothY = Mathf.Lerp(smoothY, ground + 1.02f, Time.deltaTime * 10f);
                    next.y = smoothY;
                    transform.position = next;
                    move = walkSpeed;
                }
            }

            // Geo-imported animals are driven by the official bedrock clips
            // (quadruped.walk etc.); the legacy hand-tuned gait below only
            // runs for hand-built fallback models.
            if (geoAnimPlayer != null)
            {
                // Locomotion drives the gait clock; idle animals ease back to
                // the rest pose instead of freezing mid-swing. Vanilla gates
                // walk by query.modified_move_speed (~0.25 while walking) and
                // EXCLUDES it entirely in sit/sleep states - full-weight
                // swing was flinging legs at 80 deg.
                geoAnimPlayer.moving = walking && move > 0f;
                geoAnimPlayer.walkSpeedRef = walkSpeed;
                var regGw = CreatureRegistry.Get(species);
                float target = regGw != null && !string.IsNullOrEmpty(regGw.gaitWeight)
                    ? float.Parse(regGw.gaitWeight, System.Globalization.CultureInfo.InvariantCulture)
                    : (geoAnimPlayer.moving ? 0.3f : 0f);
                geoAnimPlayer.gaitWeight = Mathf.Lerp(
                    geoAnimPlayer.gaitWeight,
                    target,
                    Time.deltaTime * 4f);
                // Feed the wing-flap variable the legacy flap value so the
                // official chicken.general clip can use it.
                if (wings[0] != null)
                    geoAnimPlayer.variables["wing_flap"] =
                        walking && move > 0f
                            ? (0.25f + 0.45f * Mathf.Abs(Mathf.Sin(animPhase * 1.5f))) * 57.3f
                            : (0.25f + 0.06f * Mathf.Sin(animPhase * 0.7f)) * 57.3f;
                // B-plan: advance the vanilla controller state machine; it
                // schedules clips itself (walk weight = query.modified_move_speed).
                TickControllers(Time.deltaTime, geoAnimPlayer.moving);
                return;
            }
            animPhase += Time.deltaTime * (4f + move * 3f);
            ApplyLegacyGait(move);
        }

        /// <summary>Hand-built fallback gait (fox and other non-geo species).
        /// Public + time-parameterised so the batch verification harness can
        /// drive it without MonoBehaviour.Update (editor batch mode never
        /// runs Update; the gait then froze and evaded testing).</summary>
        public void ApplyLegacyGait(float move, float dt, float phase)
        {
            float swing = walking && move > 0f ? Mathf.Sin(phase) * 0.55f : 0f;
            for (int i = 0; i < 4; i++)
            {
                if (legs[i] == null) continue; // bipeds only fill slots 0..1
                float sign = (i == 0 || i == 3) ? 1f : -1f;
                legs[i].localRotation = Quaternion.Euler(swing * sign * 57.3f, 0f, 0f);
            }
            if (head != null)
            {
                head.localRotation = Quaternion.Euler(Mathf.Sin(phase * 0.4f) * 4f, 0f, 0f);
            }
            // Chicken wings: flap outward about the top hinge (vanilla idle
            // spreads ~20 deg; while walking they beat at the anim phase).
            if (wings[0] != null)
            {
                float flap = walking && move > 0f
                    ? 0.25f + 0.45f * Mathf.Abs(Mathf.Sin(phase * 1.5f))
                    : 0.25f + 0.06f * Mathf.Sin(phase * 0.7f);
                wings[0].localRotation = Quaternion.Euler(0f, 0f, -flap * 57.3f); // left wing out
                wings[1].localRotation = Quaternion.Euler(0f, 0f, flap * 57.3f);  // right wing out
            }
            // Wolf tail: gentle wag around its built-in base angle.
            if (tail != null)
            {
                tail.localRotation = tailBaseRot * Quaternion.Euler(
                    -10f + Mathf.Sin(phase * 1.2f) * 12f, 0f, 0f);
            }
        }

        private void ApplyLegacyGait(float move) => ApplyLegacyGait(move, Time.deltaTime, animPhase);
    }
}

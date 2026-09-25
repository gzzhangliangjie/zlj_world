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

        private Transform[] legs = new Transform[4];
        private Transform[] wings = new Transform[2]; // chicken wing hinges
        private Transform tail;                       // wolf/fox tail hinges
        private Quaternion tailBaseRot = Quaternion.identity;

        private Transform bodyRoot;
        private Transform head;
        private BedrockAnimationPlayer geoAnimPlayer; // official clips (geo path)
        private float animPhase;
        private float stateTimer;
        private bool walking = true;
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
                    // Vanilla fox (bedrock, 48x32 sheet padded to 64):
                    // body 6x11x6 uv(30,15), head 8x6x6 uv(0,0), snout
                    // 4x2x3 uv(0,24), legs 2x6x2 uv(14,24)/(22,24),
                    // tail 4x9x5 uv(28,0).
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
                case "mooshroom":
                    goto case "cow";
                default: // pig
                    body = BoxBuilder.QuadrupedBodyNet(28, 8, 10, 16, 8);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(0, 16, 4, 6, 4);
                    break;
            }
            bodyRot = (kind == "sheep" || kind == "wolf" || kind == "fox" || kind == "goat")
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

            // Preferred path: build straight from the vanilla bedrock geo JSON
            // (the model source itself) - bones, pivots and cubes land exactly
            // where Mojang put them. Falls back to the hand-built nets below
            // when no geo file exists for this species.
            var geoAsset = Resources.Load<TextAsset>("Geo/" + species + ".geo");
            // Fox geo rest pose is a vertical pillar (the game pitches it in
            // animations); tip it into the standing pose at import.
            float geoPitch = species == "fox" ? 45f : 0f;
            if (skin != null && geoAsset != null &&
                BedrockGeoImporter.Build(bodyRoot, geoAsset, null, skin, geoPitch,
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
                animPlayer.clipsJson.Clear();
                foreach (var af in new[] { species, "quadruped", "wolf" })
                {
                    var ta = Resources.Load<TextAsset>("Anims/" + af + ".animation");
                    if (ta != null) animPlayer.clipsJson.Add(ta);
                }
                animPlayer.LoadClips();
                animPlayer.Bind(bodyRoot);
                // Base gaits: quadruped.walk (distance-driven) or chicken.move.
                string walkClip = species == "chicken" ? "animation.chicken.move"
                    : "animation.quadruped.walk";
                animPlayer.Play(walkClip);
                // Chicken wings flap while moving (official general clip).
                if (species == "chicken")
                {
                    animPlayer.variables["wing_flap"] = 0f;
                    animPlayer.Play("animation.chicken.general");
                }
                // NOTE: species ".setup" clips (wolf/pig "-this" re-roots) are
                // NOT played: they exist to convert bedrock's 1.8 bind pose,
                // which BedrockGeoImporter already applies via
                // bind_pose_rotation + leg re-rooting.
                geoAnimPlayer = animPlayer;

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
                targetYaw = Random.Range(0f, 360f);
                transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
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
                // y 7.5..13.5 px at the FRONT of the tall chest box.
                headPos = new Vector3(0f, 0.656f, 0.4375f);
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
                    fb.localRotation = Quaternion.Euler(-8f, 0f, 0f); // standing: pillar near-upright, shoulder slightly low
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
                    // Vanilla fox extras (bedrock fox.geo.json, z flipped for
                    // +Z front): snout 4x2x3 uv(0,24), ears 2x2x1 uv(0,0)/
                    // (22,0) on the head top, bushy tail 4x9x5 uv(28,0)
                    // sweeping back and down from the body rear (z 4..9 px).
                    AddSkinnedChild(head, "Snout",
                        new Vector3(0f, -headBox.y * 0.2f, headBox.z * 0.5f + 0.09375f),
                        new Vector3(0.25f, 0.125f, 0.1875f), skin, BoxBuilder.McNet(0, 24, 4, 2, 3));
                    for (int e = 0; e < 2; e++)
                    {
                        float es = e == 0 ? -0.1875f : 0.1875f;
                        AddSkinnedChild(head, "Ear" + e,
                            new Vector3(es, headBox.y * 0.5f + 0.0625f, -0.03125f),
                            new Vector3(0.125f, 0.125f, 0.0625f), skin,
                            BoxBuilder.McNet(e == 0 ? 0 : 22, 0, 2, 2, 1));
                    }
                    // Tail hinges where it meets the body rear so Update()
                    // can sway it.
                    var foxTailGo = new GameObject("FoxTailHinge");
                    foxTailGo.transform.SetParent(bodyRoot, false);
                    foxTailGo.transform.localPosition = new Vector3(0f, 0.625f, -0.28125f);
                    foxTailGo.transform.localRotation = Quaternion.Euler(100f, 0f, 0f);
                    AddSkinnedChild(foxTailGo.transform, "Tail",
                        new Vector3(0f, -0.28125f + 0.03125f, 0f),
                        new Vector3(0.25f, 0.5625f, 0.3125f), skin, BoxBuilder.McNet(28, 0, 4, 9, 5));
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
                // Feed the wing-flap variable the legacy flap value so the
                // official chicken.general clip can use it.
                if (wings[0] != null)
                    geoAnimPlayer.variables["wing_flap"] =
                        walking && move > 0f
                            ? (0.25f + 0.45f * Mathf.Abs(Mathf.Sin(animPhase * 1.5f))) * 57.3f
                            : (0.25f + 0.06f * Mathf.Sin(animPhase * 0.7f)) * 57.3f;
                return;
            }
            animPhase += Time.deltaTime * (4f + move * 3f);
            float swing = walking && move > 0f ? Mathf.Sin(animPhase) * 0.55f : 0f;
            for (int i = 0; i < 4; i++)
            {
                if (legs[i] == null) continue; // bipeds only fill slots 0..1
                float sign = (i == 0 || i == 3) ? 1f : -1f;
                legs[i].localRotation = Quaternion.Euler(swing * sign * 57.3f, 0f, 0f);
            }
            if (head != null)
            {
                head.localRotation = Quaternion.Euler(Mathf.Sin(animPhase * 0.4f) * 4f, 0f, 0f);
            }
            // Chicken wings: flap outward about the top hinge (vanilla idle
            // spreads ~20 deg; while walking they beat at the anim phase).
            if (wings[0] != null)
            {
                float flap = walking && move > 0f
                    ? 0.25f + 0.45f * Mathf.Abs(Mathf.Sin(animPhase * 1.5f))
                    : 0.25f + 0.06f * Mathf.Sin(animPhase * 0.7f);
                wings[0].localRotation = Quaternion.Euler(0f, 0f, -flap * 57.3f); // left wing out
                wings[1].localRotation = Quaternion.Euler(0f, 0f, flap * 57.3f);  // right wing out
            }
            // Wolf tail: gentle wag around its built-in base angle.
            if (tail != null)
            {
                tail.localRotation = tailBaseRot * Quaternion.Euler(
                    -10f + Mathf.Sin(animPhase * 1.2f) * 12f, 0f, 0f);
            }
        }
    }
}

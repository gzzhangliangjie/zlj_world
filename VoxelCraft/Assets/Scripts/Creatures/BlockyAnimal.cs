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

        private Transform bodyRoot;
        private readonly Transform[] legs = new Transform[4];
        private Transform head;
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
                    // mobs_mc sheep.png mixes the SHEARED gray body (x28..47)
                    // with the WOOLLY white body (x48..63, bitmap rows16..31).
                    // Our sheep is always woolly: every face samples the bright
                    // wool rects (verified opaque by SelfTest net checks).
                    body = new Vector4[]
                    {
                        new Vector4(48, 16, 8, 14), new Vector4(48, 16, 8, 14),
                        new Vector4(48, 16, 8, 6),  new Vector4(48, 20, 8, 6),
                        new Vector4(48, 16, 8, 14), new Vector4(48, 16, 8, 14),
                    };
                    head = BoxBuilder.McNet(2, 2, 6, 6, 6);
                    leg = BoxBuilder.McNet(0, 16, 4, 12, 4);
                    break;
                case "chicken":
                    body = BoxBuilder.QuadrupedBodyNet(0, 8, 6, 8, 6);
                    head = BoxBuilder.McNet(0, 0, 4, 6, 3);
                    leg = BoxBuilder.McNet(26, 0, 2, 5, 2);        // 2 px thin sticks
                    break;
                default: // pig
                    body = BoxBuilder.QuadrupedBodyNet(28, 8, 10, 16, 8);
                    head = BoxBuilder.McNet(0, 0, 8, 8, 8);
                    leg = BoxBuilder.McNet(0, 16, 4, 6, 4);
                    break;
            }
            bodyRot = kind == "sheep" ? new int[] { 0, 0, 0, 0, 0, 0 } : BoxBuilder.QuadrupedBodyRots;
        }

        /// <summary>Builds the box model. Call once after species is assigned.</summary>
        public void BuildModel()
        {
            GetDims(species, out Vector3 bodySize, out float legH, out Vector3 headBox, out float legThick);
            float totalHeight = legH + bodySize.y + headBox.y * 0.5f;

            var skin = CreatureTextureFactory.GetSkinMaterial(species + "_skin");
            bodyRoot = new GameObject("Body").transform;
            bodyRoot.SetParent(transform, false);

            if (skin != null)
            {
                // Real MC-format skin: standard quadruped nets.
                GetSpeciesNets(species, out var bodyNet, out var bodyRot, out var headNet, out var legNet);

                BoxBuilder.SkinnedBox(bodyRoot, "Body", new Vector3(0f, legH + bodySize.y * 0.5f, 0f), bodySize,
                    skin, bodyNet, 64, 32, bodyRot);
                head = BoxBuilder.SkinnedBox(bodyRoot, "Head",
                    new Vector3(0f, legH + bodySize.y + headBox.y * 0.4f, bodySize.z * 0.5f + headBox.z * 0.45f),
                    headBox, skin, headNet, 64, 32).transform;

                if (species == "pig")
                {
                    // Vanilla pig snout: 8x4x1 px slab on the face lower half
                    // (mobs_mc draws it at sheet x17..24 rows16..19). Sunk 14mm
                    // so the back face is never coplanar with the head front.
                    AddSkinnedChild(head, "Snout",
                        new Vector3(0f, -headBox.y * 0.25f, headBox.z * 0.5f + 0.014f),
                        new Vector3(0.5f, 0.25f, 0.0625f), skin, BoxBuilder.McNet(16, 15, 8, 4, 1));
                }

                if (species == "chicken")
                {
                    // 4x3x1 px beak slab on the head front (sheet rect at 14,0).
                    // Sunk 18mm into the head so the back face is never coplanar
                    // with the head front face (z-fighting).
                    AddSkinnedChild(head, "Beak",
                        new Vector3(0f, -headBox.y * 0.18f, headBox.z * 0.5f + 0.01325f),
                        new Vector3(0.25f, 0.1875f, 0.0625f), skin, BoxBuilder.McNet(14, 0, 4, 3, 1));
                    // Vanilla red wattle: 2x2x2 px box hanging below the beak
                    // (mobs_mc draws the red at sheet ~15,2).
                    AddSkinnedChild(head, "Wattle",
                        new Vector3(0f, -headBox.y * 0.5f - 0.055f, headBox.z * 0.5f - 0.01f),
                        new Vector3(0.125f, 0.125f, 0.125f), skin, BoxBuilder.McNet(15, 2, 2, 2, 2));
                }

                if (species == "chicken")
                {
                    // Vanilla 1x4x6 px wings flush against the body sides
                    // (sheet rect 24,13). Their inward faces are backfaces
                    // against the body side, so no z-fighting there.
                    var wingNet = BoxBuilder.McNet(24, 13, 1, 4, 6);
                    float bodyTop = legH + bodySize.y;
                    for (int s = 0; s < 2; s++)
                    {
                        float side = s == 0 ? -1f : 1f;
                        AddSkinnedChild(bodyRoot, "Wing" + s,
                            new Vector3(side * (bodySize.x * 0.5f + 0.0325f), bodyTop - 0.125f, 0f),
                            new Vector3(0.0625f, 0.25f, 0.375f), skin, wingNet);
                        // orientation is identity either way
                    }
                }

                // Quadrupeds: 4 hips at the body corners. The chicken is a
                // biped: 2 legs 1 px (0.0625 m) either side of center.
                bool biped = species == "chicken";
                Vector2[] hipXZ = biped
                    ? new[] { new Vector2(-0.0625f, 0f), new Vector2(0.0625f, 0f) }
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
                    ? new[] { new Vector2(-0.0625f, 0f), new Vector2(0.0625f, 0f) }
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
                    bodySize = new Vector3(0.375f, 0.375f, 0.5f);  // 6 x 6 x 8 px
                    legH = 0.3125f;
                    headBox = new Vector3(0.25f, 0.375f, 0.1875f); // 4 x 6 x 3 px
                    legThick = 0.125f;                             // 2 px thin sticks
                    break;
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
        }
    }
}

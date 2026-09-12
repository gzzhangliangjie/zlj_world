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

        // Standard MC quadruped net offsets (64x32 sheets from VoxeLibre mobs_mc).
        private static readonly Vector4[] PigBodyNet =
        {
            new Vector4(28, 16, 8, 16), new Vector4(46, 16, 8, 16),
            new Vector4(36, 8, 10, 8), new Vector4(46, 8, 10, 8),
            new Vector4(36, 16, 10, 8), new Vector4(54, 16, 10, 8),
        };
        private static readonly Vector4[] CowBodyNet =
        {
            new Vector4(18, 14, 10, 18), new Vector4(40, 14, 10, 18),
            new Vector4(28, 4, 12, 10), new Vector4(40, 4, 12, 10),
            new Vector4(28, 14, 12, 18), new Vector4(50, 14, 12, 18),
        };
        private static readonly Vector4[] SheepBodyNet =
        {
            new Vector4(28, 14, 6, 16), new Vector4(42, 14, 6, 16),
            new Vector4(36, 8, 8, 6), new Vector4(44, 8, 8, 6),
            new Vector4(36, 14, 8, 16), new Vector4(50, 14, 8, 16),
        };

        /// <summary>Builds the box model. Call once after species is assigned.</summary>
        public void BuildModel()
        {
            GetDims(species, out Vector3 bodySize, out float legH, out float headSize);
            float totalHeight = legH + bodySize.y + headSize * 0.5f;

            var skin = CreatureTextureFactory.GetSkinMaterial(species + "_skin");
            bodyRoot = new GameObject("Body").transform;
            bodyRoot.SetParent(transform, false);

            if (skin != null)
            {
                // Real MC-format skin: standard quadruped nets.
                Vector4[] bodyNet = species switch
                {
                    "cow" => CowBodyNet,
                    "sheep" => SheepBodyNet,
                    _ => PigBodyNet,
                };
                var headNet = BoxBuilder.McNet(0, 0, 8, 8, 6);
                var legNet = BoxBuilder.McNet(0, 16, 4, 12, 4);

                BoxBuilder.SkinnedBox(bodyRoot, "Body", new Vector3(0f, legH + bodySize.y * 0.5f, 0f), bodySize,
                    skin, bodyNet, 64, 32);
                head = BoxBuilder.SkinnedBox(bodyRoot, "Head",
                    new Vector3(0f, legH + bodySize.y + headSize * 0.4f, bodySize.z * 0.5f + headSize * 0.45f),
                    Vector3.one * headSize, skin, headNet, 64, 32).transform;

                Vector2[] hipXZ =
                {
                    new Vector2(-bodySize.x * 0.32f, -bodySize.z * 0.32f),
                    new Vector2(bodySize.x * 0.32f, -bodySize.z * 0.32f),
                    new Vector2(-bodySize.x * 0.32f, bodySize.z * 0.32f),
                    new Vector2(bodySize.x * 0.32f, bodySize.z * 0.32f),
                };
                for (int i = 0; i < 4; i++)
                {
                    var hip = new GameObject("Hip" + i).transform;
                    hip.SetParent(bodyRoot, false);
                    hip.localPosition = new Vector3(hipXZ[i].x, legH, hipXZ[i].y);
                    BoxBuilder.SkinnedBox(hip, "Leg", new Vector3(0f, -legH * 0.5f, 0f),
                        new Vector3(0.25f, legH, 0.25f), skin, legNet, 64, 32);
                    legs[i] = hip;
                }
            }
            else
            {
                // Procedural fallback (original look).
                BoxBuilder.Box(bodyRoot, "Body", new Vector3(0f, legH + bodySize.y * 0.5f, 0f), bodySize,
                    CreatureTextureFactory.Get(species, "body"));
                head = BoxBuilder.Box(bodyRoot, "Head",
                    new Vector3(0f, legH + bodySize.y + headSize * 0.4f, bodySize.z * 0.5f + headSize * 0.45f),
                    Vector3.one * headSize, CreatureTextureFactory.Get(species, "face")).transform;

                var legMat = CreatureTextureFactory.Get(species, "leg");
                Vector2[] hipXZ =
                {
                    new Vector2(-bodySize.x * 0.32f, -bodySize.z * 0.32f),
                    new Vector2(bodySize.x * 0.32f, -bodySize.z * 0.32f),
                    new Vector2(-bodySize.x * 0.32f, bodySize.z * 0.32f),
                    new Vector2(bodySize.x * 0.32f, bodySize.z * 0.32f),
                };
                for (int i = 0; i < 4; i++)
                {
                    var hip = new GameObject("Hip" + i).transform;
                    hip.SetParent(bodyRoot, false);
                    hip.localPosition = new Vector3(hipXZ[i].x, legH, hipXZ[i].y);
                    var leg = BoxBuilder.Box(hip, "Leg", new Vector3(0f, -legH * 0.5f, 0f),
                        new Vector3(Mathf.Max(bodySize.x * 0.22f, 0.14f), legH, Mathf.Max(bodySize.z * 0.22f, 0.14f)), legMat);
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

        private static void GetDims(string kind, out Vector3 bodySize, out float legH, out float headSize)
        {
            switch (kind)
            {
                case "cow":
                    bodySize = new Vector3(0.75f, 0.625f, 1.125f);
                    legH = 0.75f;
                    headSize = 0.5f;
                    break;
                case "sheep":
                    bodySize = new Vector3(0.5f, 0.375f, 1.0f);
                    legH = 0.75f;
                    headSize = 0.4f;
                    break;
                case "chicken":
                    bodySize = new Vector3(0.375f, 0.5f, 0.375f);
                    legH = 0.3f;
                    headSize = 0.28f;
                    break;
                default: // pig
                    bodySize = new Vector3(0.625f, 0.5f, 1.0f);
                    legH = 0.375f;
                    headSize = 0.5f;
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

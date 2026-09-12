using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// A Minecraft-style blocky animal: procedurally built box model with pixel
    /// skin, code-driven leg animation, and a simple idle/walk wander AI that
    /// sticks to the voxel surface and avoids water.
    /// </summary>
    public class BlockyAnimal : MonoBehaviour
    {
        public WorldRoot world;
        public string species = "pig";
        public float walkSpeed = 1.4f;

        private Transform bodyRoot;
        private readonly Transform[] legs = new Transform[4];
        private Transform head;
        private float animPhase;
        private float stateTimer;
        private bool walking = true;
        private float targetYaw;

        public bool IsWalking => walking;

        /// <summary>Builds the box model. Call once after species is assigned.</summary>
        public void BuildModel()
        {
            GetDims(species, out Vector3 bodySize, out float legH, out float headSize);

            bodyRoot = new GameObject("Body").transform;
            bodyRoot.SetParent(transform, false);
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

            targetYaw = Random.Range(0f, 360f);
            transform.rotation = Quaternion.Euler(0f, targetYaw, 0f);
        }

        private static void GetDims(string kind, out Vector3 bodySize, out float legH, out float headSize)
        {
            switch (kind)
            {
                case "cow":
                    bodySize = new Vector3(0.9f, 0.65f, 1.15f);
                    legH = 0.45f;
                    headSize = 0.5f;
                    break;
                case "sheep":
                    bodySize = new Vector3(0.8f, 0.65f, 1.0f);
                    legH = 0.42f;
                    headSize = 0.42f;
                    break;
                case "chicken":
                    bodySize = new Vector3(0.42f, 0.42f, 0.55f);
                    legH = 0.3f;
                    headSize = 0.28f;
                    break;
                default: // pig
                    bodySize = new Vector3(0.8f, 0.55f, 1.05f);
                    legH = 0.36f;
                    headSize = 0.45f;
                    break;
            }
        }

        private void Update()
        {
            if (bodyRoot == null || world == null || world.sim == null)
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
                var rot = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, targetYaw, 0f), Time.deltaTime * 3f);
                transform.rotation = rot;
                Vector3 forward = transform.forward;
                var next = transform.position + forward * (walkSpeed * Time.deltaTime);

                int gx = Mathf.FloorToInt(next.x);
                int gz = Mathf.FloorToInt(next.z);
                int ground = world.sim.SurfaceHeight(gx, gz);
                var surfaceBlock = ground >= 0 ? world.sim.GetBlock(gx, ground, gz) : BlockType.Air;
                bool waterAhead = world.sim.GetBlock(gx, ground + 1, gz) == BlockType.Water;
                if (ground < VoxelMath.SeaLevel || waterAhead || surfaceBlock == BlockType.Air ||
                    surfaceBlock == BlockType.Water)
                {
                    targetYaw = Random.Range(0f, 360f); // turn away from water/cliffs
                }
                else
                {
                    next.y = ground + 1.02f;
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

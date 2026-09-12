using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Items
{
    /// <summary>
    /// A world-space dropped item: mini textured cube that pops out with an
    /// impulse, falls to the voxel surface, bobs and spins, magnet-slides to the
    /// player when close, and auto-collects into the inventory on touch.
    /// </summary>
    public class DropEntity : MonoBehaviour
    {
        public enum Kind : byte { Block = 0, Meat = 1 }

        public Kind kind;
        public BlockType block;
        public float pickupDelay = 0.45f;

        private WorldRoot world;
        private Transform player;
        private Transform visual;
        private Vector3 velocity;
        private float age;

        public void Setup(Kind itemKind, BlockType blockType, WorldRoot worldRoot, Transform playerTransform, Texture2D icon)
        {
            kind = itemKind;
            block = blockType;
            world = worldRoot;
            player = playerTransform;

            var material = ItemDrops.MaterialFor(icon);
            visual = BoxBuilder.Box(transform, "Drop", Vector3.zero, Vector3.one * 0.26f, material).transform;
            velocity = new Vector3(Random.Range(-1.3f, 1.3f), 3.4f, Random.Range(-1.3f, 1.3f));
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            age += dt;

            // Ballistic fall onto the voxel surface (tree-aware like creatures).
            velocity.y -= 18f * dt;
            Vector3 pos = transform.position + velocity * dt;
            int ground = -1;
            if (world != null && world.sim != null)
            {
                ground = world.sim.SurfaceHeight(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.z), true);
            }
            if (ground >= 0)
            {
                float floorY = ground + 1f + 0.14f;
                if (pos.y < floorY)
                {
                    pos.y = floorY;
                    if (velocity.y < 0f)
                    {
                        velocity.y = 0f;
                        velocity.x *= 0.55f;
                        velocity.z *= 0.55f;
                    }
                }
            }
            if (pos.y < 0.2f)
            {
                pos.y = 0.2f; // never fall out of the world
            }

            // Magnet + auto pickup.
            if (player != null && age > pickupDelay)
            {
                Vector3 to = player.position + Vector3.up * 0.8f - pos;
                float dist = to.magnitude;
                if (dist < 2.4f)
                {
                    pos += to.normalized * Mathf.Min(6f * dt, dist);
                }
                if (dist < 1.0f)
                {
                    Collect();
                    return;
                }
            }

            transform.position = pos;
            if (visual != null)
            {
                visual.localRotation = Quaternion.Euler(0f, age * 95f, 0f);
                visual.localPosition = new Vector3(0f, Mathf.Sin(age * 2.6f) * 0.05f, 0f);
            }
        }

        private void Collect()
        {
            if (kind == Kind.Meat)
            {
                Inventory.AddMeat(1);
            }
            else
            {
                Inventory.Add(block);
            }
            ItemDrops.ReportCollected(this);
            Destroy(gameObject);
        }
    }

    /// <summary>Spawns and tracks drop entities; context is wired once by Game.</summary>
    public static class ItemDrops
    {
        public const int MaxLive = 120;

        public static WorldRoot world;
        public static Transform player;
        public static System.Func<BlockType, Texture2D> iconOf;

        private static readonly List<DropEntity> live = new List<DropEntity>();
        private static readonly Dictionary<Texture2D, Material> materialCache = new Dictionary<Texture2D, Material>();

        public static int LiveCount => live.Count;

        public static void Spawn(BlockType type, Vector3 position)
        {
            if (type == BlockType.Air || Inventory.creative)
            {
                return;
            }
            var go = new GameObject("Drop_" + type);
            go.transform.position = position;
            var drop = go.AddComponent<DropEntity>();
            drop.Setup(DropEntity.Kind.Block, type, world, player, iconOf != null ? iconOf(type) : null);
            Register(drop);
        }

        public static void SpawnMeat(Vector3 position, int amount)
        {
            if (Inventory.creative)
            {
                return;
            }
            for (int i = 0; i < amount; i++)
            {
                var go = new GameObject("Drop_Meat");
                go.transform.position = position + new Vector3(Random.Range(-0.3f, 0.3f), 0.3f, Random.Range(-0.3f, 0.3f));
                var drop = go.AddComponent<DropEntity>();
                drop.Setup(DropEntity.Kind.Meat, BlockType.Air, world, player, ToolIcons.GetItem("meat"));
                Register(drop);
            }
        }

        private static void Register(DropEntity drop)
        {
            live.Add(drop);
            while (live.Count > MaxLive)
            {
                var oldest = live[0];
                live.RemoveAt(0);
                if (oldest != null)
                {
                    Object.Destroy(oldest.gameObject);
                }
            }
        }

        public static void ReportCollected(DropEntity drop)
        {
            live.Remove(drop);
        }

        public static Material MaterialFor(Texture2D icon)
        {
            if (icon == null)
            {
                return null;
            }
            if (materialCache.TryGetValue(icon, out var mat) && mat != null)
            {
                return mat;
            }
            var shader = Resources.Load<Shader>("Shaders/UnlitTextureShader");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Texture");
            }
            mat = new Material(shader) { mainTexture = icon };
            materialCache[icon] = mat;
            return mat;
        }

        public static void ClearAll()
        {
            foreach (var drop in live)
            {
                if (drop != null)
                {
                    Object.Destroy(drop.gameObject);
                }
            }
            live.Clear();
        }
    }
}

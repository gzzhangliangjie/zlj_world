using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.World;

namespace VoxelCraft.Creatures
{
    /// <summary>Keeps a small population of blocky animals wandering near the player.</summary>
    public class CreatureSpawner : MonoBehaviour
    {
        public WorldRoot world;
        public Transform player;
        public int targetCount = 10;
        public float spawnMinDistance = 18f;
        public float spawnMaxDistance = 42f;
        public float despawnDistance = 70f;

        private static readonly string[] Species = { "pig", "cow", "sheep", "chicken", "wolf", "fox", "mooshroom", "goat" };
        private readonly List<BlockyAnimal> animals = new List<BlockyAnimal>();
        private float scanTimer;

        private void Update()
        {
            if (world == null || world.sim == null || player == null)
            {
                return;
            }

            scanTimer -= Time.deltaTime;
            if (scanTimer > 0f)
            {
                return;
            }
            scanTimer = 1f;

            for (int i = animals.Count - 1; i >= 0; i--)
            {
                if (animals[i] == null ||
                    Vector3.Distance(animals[i].transform.position, player.position) > despawnDistance)
                {
                    if (animals[i] != null)
                    {
                        Destroy(animals[i].gameObject);
                    }
                    animals.RemoveAt(i);
                }
            }

            int attempts = 0;
            while (animals.Count < targetCount && attempts < 12)
            {
                attempts++;
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float dist = Random.Range(spawnMinDistance, spawnMaxDistance);
                int wx = Mathf.FloorToInt(player.position.x + Mathf.Cos(angle) * dist);
                int wz = Mathf.FloorToInt(player.position.z + Mathf.Sin(angle) * dist);
                int ground = world.sim.SurfaceHeight(wx, wz, ignoreTrees: true);
                if (ground < VoxelMath.SeaLevel + 1 || ground > TerrainGenerator.SnowLine - 2)
                {
                    continue;
                }
                if (world.sim.GetBlock(wx, ground, wz) != BlockType.Grass)
                {
                    continue;
                }

                var go = new GameObject($"Animal_{Species[animals.Count % Species.Length]}");
                go.transform.position = new Vector3(wx + 0.5f, ground + 1.02f, wz + 0.5f);
                var animal = go.AddComponent<BlockyAnimal>();
                animal.world = world;
                animal.playerRef = player;
                animal.species = Species[Random.Range(0, Species.Length)];
                animal.BuildModel();
                animals.Add(animal);
            }
        }
    }
}

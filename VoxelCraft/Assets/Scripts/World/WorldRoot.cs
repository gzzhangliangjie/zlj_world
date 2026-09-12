using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;

namespace VoxelCraft.World
{
    /// <summary>
    /// Unity glue for WorldSim: owns materials, applies rebuilt MeshData to
    /// MeshFilters/MeshColliders, and tears down unloaded chunks.
    /// </summary>
    public class WorldRoot : MonoBehaviour
    {
        public WorldSim sim { get; private set; }

        private Transform viewer;
        private Material solidMaterial;
        private Material waterMaterial;
        private readonly List<Chunk> remeshed = new List<Chunk>(64);
        private readonly List<Chunk> unloaded = new List<Chunk>(64);

        public void Init(int seed, int viewRadius, TextureFactory.AtlasResult atlas, Material solid, Material water, Transform viewerTransform)
        {
            sim = new WorldSim(seed)
            {
                dataRadius = viewRadius + 1,
                meshRadius = viewRadius,
                unloadRadius = viewRadius + 3,
            };
            sim.tileRects = BuildTileRects(atlas);
            solidMaterial = solid;
            waterMaterial = water;
            viewer = viewerTransform;
        }

        private static Rect[] BuildTileRects(TextureFactory.AtlasResult atlas)
        {
            int count = TextureFactory.AtlasCols * TextureFactory.AtlasRows;
            var rects = new Rect[count];
            for (int i = 0; i < count; i++)
            {
                rects[i] = atlas.TileRect((TileId)i);
            }
            return rects;
        }

        /// <summary>Applies an edit and immediately remeshes affected chunks (responsive building).</summary>
        public void SetBlockAndApply(Vector3Int pos, BlockType type)
        {
            if (sim == null)
            {
                return;
            }
            var affected = new List<Chunk>(4);
            sim.SetBlock(pos.x, pos.y, pos.z, type, affected);
            for (int i = 0; i < affected.Count; i++)
            {
                ApplyMeshes(affected[i]);
            }
        }

        /// <summary>
        /// Synchronously generates data + meshes a 3x3 chunk area around a world
        /// position so spawning has colliders immediately.
        /// </summary>
        public void PrewarmAround(Vector3 worldPos)
        {
            if (sim == null)
            {
                return;
            }
            int cx = VoxelMath.ChunkCoord(Mathf.FloorToInt(worldPos.x));
            int cz = VoxelMath.ChunkCoord(Mathf.FloorToInt(worldPos.z));
            int savedData = sim.dataRadius;
            int savedMesh = sim.meshRadius;
            int savedUnload = sim.unloadRadius;
            sim.dataRadius = 1;
            sim.meshRadius = 0;
            sim.unloadRadius = 8; // keep everything; nothing is far
            var prewarmed = new List<Chunk>(9);
            sim.Step(cx, cz, 60000f, 60000f, prewarmed, null);
            sim.dataRadius = savedData;
            sim.meshRadius = savedMesh;
            sim.unloadRadius = savedUnload;
            for (int i = 0; i < prewarmed.Count; i++)
            {
                ApplyMeshes(prewarmed[i]);
            }
        }

        private float cropTimer;

        private void Update()
        {
            if (sim == null || viewer == null)
            {
                return;
            }
            int cx = VoxelMath.ChunkCoord(Mathf.FloorToInt(viewer.position.x));
            int cz = VoxelMath.ChunkCoord(Mathf.FloorToInt(viewer.position.z));
            remeshed.Clear();
            unloaded.Clear();
            sim.Step(cx, cz, 8f, 8f, remeshed, unloaded);
            for (int i = 0; i < remeshed.Count; i++)
            {
                ApplyMeshes(remeshed[i]);
            }
            for (int i = 0; i < unloaded.Count; i++)
            {
                TearDown(unloaded[i]);
            }

            // Crop growth clock (1 Hz): advance stages + replay crops lost to chunk regeneration.
            cropTimer -= Time.deltaTime;
            if (cropTimer <= 0f)
            {
                cropTimer = 1f;
                foreach (var edit in Items.Crops.Tick(sim))
                {
                    SetBlockAndApply(edit.pos, edit.block);
                }
            }
        }

        public void ApplyMeshes(Chunk chunk)
        {
            EnsureObjects(chunk);

            chunk.solidMesh = chunk.solidMeshData.ToMesh(chunk.solidMesh);
            var solidFilter = chunk.solidObject.GetComponent<MeshFilter>();
            solidFilter.sharedMesh = chunk.solidMesh;
            var collider = chunk.solidObject.GetComponent<MeshCollider>();
            collider.sharedMesh = null;
            collider.sharedMesh = chunk.solidMesh;

            chunk.waterMesh = chunk.waterMeshData.ToMesh(chunk.waterMesh);
            chunk.waterObject.GetComponent<MeshFilter>().sharedMesh = chunk.waterMesh;
            chunk.waterObject.SetActive(!chunk.waterMeshData.IsEmpty);
        }

        private void EnsureObjects(Chunk chunk)
        {
            if (chunk.solidObject == null)
            {
                var go = new GameObject($"Chunk {chunk.cx},{chunk.cz}");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(
                    chunk.cx * VoxelMath.ChunkSize, 0f, chunk.cz * VoxelMath.ChunkSize);
                var filter = go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = solidMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                go.AddComponent<MeshCollider>();
                chunk.solidObject = go;
            }
            if (chunk.waterObject == null)
            {
                var go = new GameObject($"Chunk {chunk.cx},{chunk.cz} Water");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = new Vector3(
                    chunk.cx * VoxelMath.ChunkSize, 0f, chunk.cz * VoxelMath.ChunkSize);
                go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = waterMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                chunk.waterObject = go;
            }
        }

        public void TearDown(Chunk chunk)
        {
            if (chunk.solidObject != null)
            {
                Destroy(chunk.solidObject);
                chunk.solidObject = null;
            }
            if (chunk.waterObject != null)
            {
                Destroy(chunk.waterObject);
                chunk.waterObject = null;
            }
            if (chunk.solidMesh != null)
            {
                Destroy(chunk.solidMesh);
                chunk.solidMesh = null;
            }
            if (chunk.waterMesh != null)
            {
                Destroy(chunk.waterMesh);
                chunk.waterMesh = null;
            }
        }
    }
}

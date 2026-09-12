using UnityEngine;
using VoxelCraft.Core;

namespace VoxelCraft.World
{
    /// <summary>
    /// Pure voxel storage for one chunk column plus streaming/meshing state.
    /// GameObjects and meshes are owned by World.
    /// </summary>
    public class Chunk
    {
        public readonly int cx;
        public readonly int cz;
        public readonly byte[] blocks = new byte[VoxelMath.BlocksPerChunk];

        /// <summary>True once terrain data has been generated.</summary>
        public bool dataReady;

        /// <summary>True when the mesh needs a (re)build.</summary>
        public bool meshDirty = true;

        /// <summary>True once a mesh has been built at least once.</summary>
        public bool meshBuilt;

        // Owned by World in M3:
        public GameObject solidObject;
        public GameObject waterObject;
        public Mesh solidMesh;
        public Mesh waterMesh;
        public MeshData solidMeshData;
        public MeshData waterMeshData;

        public Chunk(int cx, int cz)
        {
            this.cx = cx;
            this.cz = cz;
        }

        public static long Key(int cx, int cz)
        {
            return ((long)cx << 32) | (uint)cz;
        }

        public long key => Key(cx, cz);

        /// <summary>Local getter; out-of-range returns Air.</summary>
        public BlockType GetLocal(int x, int y, int z)
        {
            if (x < 0 || x >= VoxelMath.ChunkSize || z < 0 || z >= VoxelMath.ChunkSize ||
                y < 0 || y >= VoxelMath.ChunkHeight)
            {
                return BlockType.Air;
            }
            return (BlockType)blocks[VoxelMath.LocalIndex(x, y, z)];
        }

        public void SetLocal(int x, int y, int z, BlockType type)
        {
            if (x < 0 || x >= VoxelMath.ChunkSize || z < 0 || z >= VoxelMath.ChunkSize ||
                y < 0 || y >= VoxelMath.ChunkHeight)
            {
                return;
            }
            blocks[VoxelMath.LocalIndex(x, y, z)] = (byte)type;
        }
    }
}

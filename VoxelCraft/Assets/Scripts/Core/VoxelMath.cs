namespace VoxelCraft.Core
{
    /// <summary>Shared voxel-space constants and pure math helpers.</summary>
    public static class VoxelMath
    {
        public const int ChunkSize = 16;     // X and Z extent of a chunk
        public const int ChunkHeight = 80;   // Y extent of a chunk column
        public const int SeaLevel = 30;      // water surface height

        public const int BlocksPerChunk = ChunkSize * ChunkSize * ChunkHeight;

        /// <summary>Flattened index inside a chunk: x/z in [0,16), y in [0,80).</summary>
        public static int LocalIndex(int x, int y, int z)
        {
            return (y * ChunkSize + z) * ChunkSize + x;
        }

        /// <summary>Floor division that stays correct for negative coordinates.</summary>
        public static int FloorDiv(int value, int divisor)
        {
            return value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
        }

        /// <summary>Modulo that always returns [0, divisor).</summary>
        public static int Mod(int value, int divisor)
        {
            int m = value % divisor;
            return m < 0 ? m + divisor : m;
        }

        /// <summary>Chunk X coordinate that owns the given world X.</summary>
        public static int ChunkCoord(int worldX) => FloorDiv(worldX, ChunkSize);
    }
}

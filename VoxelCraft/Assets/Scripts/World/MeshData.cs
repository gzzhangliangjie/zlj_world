using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace VoxelCraft.World
{
    /// <summary>CPU-side mesh buffers for one chunk layer (solid or water).</summary>
    public class MeshData
    {
        public readonly List<Vector3> Vertices = new List<Vector3>(4096);
        public readonly List<Vector2> Uvs = new List<Vector2>(4096);
        public readonly List<Color32> Colors = new List<Color32>(4096);
        public readonly List<int> Indices = new List<int>(6144);

        public bool IsEmpty => Indices.Count == 0;

        public void Clear()
        {
            Vertices.Clear();
            Uvs.Clear();
            Colors.Clear();
            Indices.Clear();
        }

        /// <summary>Uploads buffers into a (reused) Mesh. Caller owns the Mesh lifetime.</summary>
        public Mesh ToMesh(Mesh mesh)
        {
            if (mesh == null)
            {
                mesh = new Mesh();
            }
            mesh.Clear();
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(Vertices);
            mesh.SetUVs(0, Uvs);
            mesh.SetColors(Colors);
            mesh.SetIndices(Indices, MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace CityGen
{
    public class CityLODManager : MonoBehaviour
    {
        public static CityLODManager Instance { get; private set; }

        public ChunkGrid BuildingChunks = new ChunkGrid();

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
            else
                Destroy(this);
        }
    }

    public class ChunkGrid
    {
        public ChunkGrid(int gridSize = 100)
        {
            GridSize = gridSize;
        }

        public int GridSize;
        public Dictionary<(int x, int y), Chunk> Chunks = new();

        public Chunk Get(int x, int y)
        {
            if (!Chunks.TryGetValue((x, y), out var chunk))
            {
                Chunks[(x, y)] = new Chunk();
                chunk = Chunks[(x, y)];
            }
            return chunk;
        }
    }

    public class Chunk
    {
        public List<Vector3> Verts { get; } = new List<Vector3>();
        public List<int> Tris { get; } = new List<int>();
    }
}
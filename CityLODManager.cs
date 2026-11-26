using System;
using System.Collections.Generic;
using UnityEngine;

namespace CityGen
{
    public class CityLODManager : MonoBehaviour
    {
        [SerializeField] private new Camera camera;

        public static event Action Updated;

        public static CityLODManager Instance { get; private set; }

        public ChunkGrid BuildingChunks;
        public ChunkGrid StreetChunks;

        private void Awake()
        {
            if (Instance == null)
                Instance = this;
            else
                Destroy(this);

            BuildingChunks = new ChunkGrid("buildings");

            StreetChunks = new ChunkGrid("streets",
            onUpdate: grid =>
            {
                foreach (var pair in grid.Chunks)
                {
                    var chunk = pair.Value;
                    var (x, z) = pair.Key;

                    var centre = new Vector3(
                        x + grid.GridSize*0.5f, 
                        0, 
                        z + grid.GridSize*0.5f);

                    float cameraDistance = Vector3.Distance(centre, camera.transform.position);
                    float lift = Mathf.InverseLerp(0, 100, cameraDistance);

                    var pos = chunk.obj.transform.position;
                    chunk.obj.transform.position = new Vector3(pos.x, lift, pos.z);
                }
            });
        }

        private void LateUpdate()
        {
            Updated?.Invoke();
        }
    }

    public class ChunkGrid
    {
        private int i = 0;
        private string name = "grid";

        private Action<ChunkGrid> onUpdate;

        public ChunkGrid(string name = "grid", int gridSize = 100, Action<ChunkGrid> onUpdate = null)
        {
            GridSize = gridSize;
            this.name = name;

            this.onUpdate = onUpdate;
            if (onUpdate != null) CityLODManager.Updated += HandleUpdated;
        }

        public int GridSize;
        public Dictionary<(int x, int y), Chunk> Chunks = new();

        public Chunk ChunkAt(Vector3 worldPos)
        {
            int gx = Mathf.FloorToInt(worldPos.x / GridSize) * GridSize;
            int gz = Mathf.FloorToInt(worldPos.z / GridSize) * GridSize;
            return GetFromKey(gx, gz);
        }

        public Chunk GetFromKey(int x, int y)
        {
            if (!Chunks.TryGetValue((x, y), out var chunk))
            {
                Chunks[(x, y)] = new Chunk();
                chunk = Chunks[(x, y)];
            }
            return chunk;
        }

        public void Build(Material material)
        {
            foreach (var p in Chunks)
            {
                var k = p.Key;
                var chunk = p.Value;

                if (chunk.Verts.Count == 0) continue;

                if (chunk.obj == null) chunk.obj = new GameObject($"{name} {i++}");

                var mesh = new Mesh();
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

                mesh.SetVertices(chunk.Verts);
                mesh.SetTriangles(chunk.Tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                chunk.obj.AddComponent<MeshRenderer>().sharedMaterial = material;
                chunk.obj.AddComponent<MeshFilter>().sharedMesh = mesh;
            }
        }

        private void HandleUpdated() => onUpdate(this);
    }

    public class Chunk
    {
        public List<Vector3> Verts { get; } = new List<Vector3>();
        public List<int> Tris { get; } = new List<int>();

        public GameObject obj = null;
    }
}
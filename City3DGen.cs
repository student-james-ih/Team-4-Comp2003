using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CityGen
{
    // This class consumes CityMaps to generate 3D cities

    public class City3DGen : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private Material buildingMaterial;
        [SerializeField] private Material streetMaterial;
        private static int tileSize = 1;

        // Set in Consume
        private static Vector3 origin;

        public void ApplySettings(int tileSize)
        {
            City3DGen.tileSize = tileSize;
        }

        public void Consume(CityMap map, Vector3? centre = null)
        {
            origin = (centre ?? Vector3.zero) - V3(map.Width/2, map.Height/2) * tileSize;

            // BUILDINGS

            GenerateBuildings(map, tileSize);

            // STREETS

            var streetVerts = new List<Vector3>(); // may be abstracted later
            var streetTris = new List<int>();

            foreach (var street in map.Streets)
            {
                MeshStreet(street, origin, streetVerts, streetTris);
            }

            var streetsMesh = new Mesh();
            streetsMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            streetsMesh.SetVertices(streetVerts);
            streetsMesh.SetTriangles(streetTris, 0);
            streetsMesh.RecalculateNormals();
            streetsMesh.RecalculateBounds();

            var streetsObj = new GameObject("streets");
            var streetsMeshRenderer = streetsObj.AddComponent<MeshRenderer>();
            var streetsMeshFilter = streetsObj.AddComponent<MeshFilter>();

            streetsMeshRenderer.sharedMaterial = streetMaterial;
            streetsMeshFilter.sharedMesh = streetsMesh;
        }

        private void GenerateBuildings(CityMap map, int tileSize)
        {
            foreach (var b in map.Buildings)
            {
                var frame = map.Sectors[b.Sector].Frame;

                var pos = World(frame.ToWorld(b.Lot.CentreX, b.Lot.CentreY));

                var chunks = CityLODManager.Instance.BuildingChunks;

                int gridX = Mathf.FloorToInt(pos.x / chunks.GridSize) * chunks.GridSize;
                int gridZ = Mathf.FloorToInt(pos.z / chunks.GridSize) * chunks.GridSize;

                Chunk chunk = chunks.Get(gridX, gridZ);

                MeshBuilding(b, frame, chunk.Verts, chunk.Tris);
            }

            foreach (var c in CityLODManager.Instance.BuildingChunks.Chunks.Values)
            {
                var mesh = new Mesh();

                mesh.SetVertices(c.Verts);
                mesh.SetTriangles(c.Tris, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();

                var obj = new GameObject($"chunk");
                var meshRenderer = obj.AddComponent<MeshRenderer>();
                var meshFilter = obj.AddComponent<MeshFilter>();

                meshRenderer.sharedMaterial = buildingMaterial;
                meshFilter.sharedMesh = mesh;
            }
        }

        private void MeshStreet(
            StreetPiece road, Vector3 offset, 
            List<Vector3> verts, List<int> tris)
        {

            void Fill(List<Vector3> p)
            {
                var whitelist = new List<int>();
                    for (int i = 0; i < p.Count; i++)
                        whitelist.Add(i);

                while (true)
                {
                    for (int i = 0; i < whitelist.Count; i++)
                    {
                        if (i + 2 < whitelist.Count)
                        {
                            MeshTri(p[whitelist[i]], p[whitelist[i + 1]], p[whitelist[i + 2]], Vector3.up, verts, tris);
                            whitelist.RemoveAt(i + 1);
                        }
                        else if (i + 1 < whitelist.Count)
                        {
                            MeshTri(p[whitelist[0]], p[whitelist[i]], p[whitelist[i + 1]], Vector3.up, verts, tris);
                            whitelist.RemoveAt(i + 1);
                        }
                        else
                            break;
                    }
                    if (whitelist.Count <= 2)
                        break;
                }
            }

            Fill(road.Outline.Select(point => World(point)).ToList());
        }

        private void MeshBuilding(Building building, Frame frame, List<Vector3> verts, List<int> tris)
        {
            var firstTile = building.LocalTiles[0];
            int lowX = firstTile.X;
            int lowY = firstTile.Y;
            int highX = firstTile.X;
            int highY = firstTile.Y;

            foreach (var bt in building.LocalTiles)
            {
                if (bt.X < lowX) lowX = bt.X;
                if (bt.Y < lowY) lowY = bt.Y;
                if (bt.X > highX) highX = bt.X;
                if (bt.Y > highY) highY = bt.Y;
            }

            // Making every building a cube initially

            // Roof
            MeshSquare(
                    World(frame.ToWorld(lowX, lowY), building.MaxFloors),
                    World(frame.ToWorld(lowX, highY), building.MaxFloors),
                    World(frame.ToWorld(highX, highY), building.MaxFloors),
                    World(frame.ToWorld(highX, lowY), building.MaxFloors),
                    Vector3.up, verts, tris);

            // Left wall
            MeshSquare(
                    World(frame.ToWorld(lowX, lowY)),
                    World(frame.ToWorld(lowX, lowY), building.MaxFloors),
                    World(frame.ToWorld(lowX, highY), building.MaxFloors),
                    World(frame.ToWorld(lowX, highY)),
                    Vector3.left, verts, tris);

            // Right Wall
            MeshSquare(
                    World(frame.ToWorld(highX, lowY)),
                    World(frame.ToWorld(highX, lowY), building.MaxFloors),
                    World(frame.ToWorld(highX, highY), building.MaxFloors),
                    World(frame.ToWorld(highX, highY)),
                    Vector3.right, verts, tris);

            // Back Wall
            MeshSquare(
                    World(frame.ToWorld(lowX, lowY)),
                    World(frame.ToWorld(lowX, lowY), building.MaxFloors),
                    World(frame.ToWorld(highX, lowY), building.MaxFloors),
                    World(frame.ToWorld(highX, lowY)),
                    Vector3.back, verts, tris);

            // Forward Wall
            MeshSquare(
                    World(frame.ToWorld(lowX, highY)),
                    World(frame.ToWorld(lowX, highY), building.MaxFloors),
                    World(frame.ToWorld(highX, highY), building.MaxFloors),
                    World(frame.ToWorld(highX, highY)),
                    Vector3.forward, verts, tris);
        }

        private void MeshSquare(
            Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal,
            List<Vector3> verts, List<int> tris)
        {

            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f)
            {
                (d, b) = (b, d); // 'flip winding'
            }

            int i = verts.Count; // setting indes to end of verts instead of 0 as it may already contain data

            verts.Add(a);
            verts.Add(b);
            verts.Add(c);
            verts.Add(d);

            tris.Add(i); // tri 1 (verts 1, 2, 3)
            tris.Add(i + 1);
            tris.Add(i + 2);

            tris.Add(i); // tri 2 (verts 1, 3, 4)
            tris.Add(i + 2);
            tris.Add(i + 3);
        }

        private void MeshTri(
            Vector3 a, Vector3 b, Vector3 c, Vector3 normal,
            List<Vector3> verts, List<int> tris)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0f)
            {
                (a, b) = (b, a); // 'flip winding'
            }

            int i = verts.Count; // setting indes to end of verts instead of 0 as it may already contain data

            verts.Add(a);
            verts.Add(b);
            verts.Add(c);

            tris.Add(i);
            tris.Add(i + 1);
            tris.Add(i + 2);
        }

        // Converts x, y, h into Vector3 format. h = height.
        // Does not do any other conversions such as tile size
        private static Vector3 V3(double x, double y, float h = 0) => V3((x, y), h);
        private static Vector3 V3((double x, double y) point, float h = 0)
        {
            return new Vector3((float)point.x, h, (float)point.y);
        }

        // Maps x, y, h into its world position. h = height.
        // Accounts for city origin and tile size.
        private static Vector3 World((double x, double y) point, int h = 0)
        {
            return origin + V3(point.x, point.y, h)*tileSize;
        }
        private static Vector3 World(double x, double y, int h = 0) => World((x, y), h);
    }
}
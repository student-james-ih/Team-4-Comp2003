using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;

namespace CityGen
{
    // This class consumes CityMaps to generate 3D cities

    public class City3DGen : MonoBehaviour
    {
        [SerializeField] private Material buildingMaterial;

        public void Consume(CityMap map, Vector3? point = null)
        {
            Vector3 offset = point ?? Vector3.zero;
            offset -= new Vector3(map.Width / 2, 0, map.Height / 2);

            var verts = new List<Vector3>(); // may be abstracted later
            var tris = new List<int>();

            // BUILDINGS

            foreach (var b in map.Buildings)
            {
                var frame = map.Sectors.Where(s => s.Id == b.Sector).FirstOrDefault().Frame;

                MeshBuilding(b, frame, offset, verts, tris);
            }

            var buildingsMesh = new Mesh();
            buildingsMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            buildingsMesh.SetVertices(verts);
            buildingsMesh.SetTriangles(tris, 0);
            buildingsMesh.RecalculateNormals();
            buildingsMesh.RecalculateBounds();

            var obj = new GameObject("buildings");
            var meshRenderer = obj.AddComponent<MeshRenderer>();
            var meshFilter = obj.AddComponent<MeshFilter>();

            meshRenderer.sharedMaterial = buildingMaterial;
            meshFilter.sharedMesh = buildingsMesh;
        }


        private void MeshBuilding(
            Building building, Frame frame, Vector3 offset, 
            List<Vector3> verts, List<int> tris)
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
                    V3(frame.ToWorld(lowX, lowY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(lowX, highY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, highY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, lowY), building.MaxFloors) + offset,
                    Vector3.up, verts, tris);

            // Left wall
            MeshSquare(
                    V3(frame.ToWorld(lowX, lowY)) + offset,
                    V3(frame.ToWorld(lowX, lowY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(lowX, highY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(lowX, highY)) + offset,
                    Vector3.left, verts, tris);

            // Right Wall
            MeshSquare(
                    V3(frame.ToWorld(highX, lowY)) + offset,
                    V3(frame.ToWorld(highX, lowY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, highY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, highY)) + offset,
                    Vector3.right, verts, tris);

            // Back Wall
            MeshSquare(
                    V3(frame.ToWorld(lowX, lowY)) + offset,
                    V3(frame.ToWorld(lowX, lowY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, lowY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, lowY)) + offset,
                    Vector3.back, verts, tris);

            // Forward Wall
            MeshSquare(
                    V3(frame.ToWorld(lowX, highY)) + offset,
                    V3(frame.ToWorld(lowX, highY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, highY), building.MaxFloors) + offset,
                    V3(frame.ToWorld(highX, highY)) + offset,
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

            int i = verts.Count; // setting indes to end of verts instead of 0 as it may already contain data later

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


        private static Vector3 V3(double x, double y, float h = 0) => V3((x, y), h);
        private static Vector3 V3((double x, double y) point, float h = 0)
        {
            return new Vector3((float)point.x, h, (float)point.y);
        }
    }
}
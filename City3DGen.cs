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

        private CityLODManager LOD => CityLODManager.Instance;

        // Set in Consume
        private static Vector3 origin;
        private static Terrain terrain;
        private static float[,] heightMap;

        public void ApplySettings(int tileSize)
        {
            City3DGen.tileSize = tileSize;
        }

        public void Consume(CityMap map, Terrain terrain, float[,] heightMap, Vector3? centre = null)
        {
            origin = (centre ?? Vector3.zero) - V3(map.Width/2, map.Height/2) * tileSize;
            City3DGen.terrain = terrain;
            City3DGen.heightMap = heightMap;

            GenerateBuildings(map, tileSize);

            streetVertLookup.Clear();

            GenerateStreets(map);

            GenerateSignificantRoads(map);

            LOD.StreetChunks.Build(streetMaterial);
        }

        #region Buildings

        private void GenerateBuildings(CityMap map, int tileSize)
        {
            foreach (var b in map.Buildings)
            {
                var frame = map.Sectors[b.Sector].Frame;

                var pos = World(frame.ToWorld(b.Lot.CentreX, b.Lot.CentreY));

                var chunks = LOD.BuildingChunks;

                Chunk chunk = LOD.BuildingChunks.ChunkAt(pos);

                MeshBuilding(b, frame, chunk.Verts, chunk.Tris);
            }

            LOD.BuildingChunks.Build(buildingMaterial);
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

            (double x, double y) W(double x, double y) => frame.ToWorld(x, y);

            // Roof
            MeshSquare(
                    Terrain(W(lowX, lowY), building.MaxFloors),
                    Terrain(W(lowX, highY), building.MaxFloors),
                    Terrain(W(highX, highY), building.MaxFloors),
                    Terrain(W(highX, lowY), building.MaxFloors),
                    Vector3.up, verts, tris);

            // Left wall
            MeshSquare(
                    Terrain(W(lowX, lowY)),
                    Terrain(W(lowX, lowY), building.MaxFloors),
                    Terrain(W(lowX, highY), building.MaxFloors),
                    Terrain(W(lowX, highY)),
                    Vector3.left, verts, tris);

            // Right Wall
            MeshSquare(
                    Terrain(W(highX, lowY)),
                    Terrain(W(highX, lowY), building.MaxFloors),
                    Terrain(W(highX, highY), building.MaxFloors),
                    Terrain(W(highX, highY)),
                    Vector3.right, verts, tris);

            // Back Wall
            MeshSquare(
                    Terrain(W(lowX, lowY)),
                    Terrain(W(lowX, lowY), building.MaxFloors),
                    Terrain(W(highX, lowY), building.MaxFloors),
                    Terrain(W(highX, lowY)),
                    Vector3.back, verts, tris);

            // Forward Wall
            MeshSquare(
                    Terrain(W(lowX, highY)),
                    Terrain(W(lowX, highY), building.MaxFloors),
                    Terrain(W(highX, highY), building.MaxFloors),
                    Terrain(W(highX, highY)),
                    Vector3.forward, verts, tris);
        }

        #endregion

        private void GenerateSignificantRoads(CityMap map)
        {
            var streetChunks = LOD.StreetChunks;

            foreach (var road in map.SignificantRoads)
                MeshSignificantRoad(road, streetChunks);
        }

        private void MeshSignificantRoad(SignificantRoad road, ChunkGrid chunks)
        {
            var p = road.Points;

            Vector2 dir = new Vector2((float)(p[1].X - p[0].X), (float)(p[1].Y - p[0].Y));
            Vector2 normal = new Vector2(-dir.y, dir.x).normalized;
            (double X, double Y) n = (normal.x, normal.y);


            List<(double X, double Y)> outline = new List<(double x, double y)>()
            {
                (p[0].X + (n.X*road.Width*0.5f), p[0].Y + (n.Y*road.Width*0.5f)),
                (p[0].X - (n.X*road.Width*0.5f), p[0].Y - (n.Y*road.Width*0.5f)),
                (p[1].X + (n.X*road.Width*0.5f), p[1].Y + (n.Y*road.Width*0.5f)),
                (p[1].X - (n.X*road.Width*0.5f), p[1].Y - (n.Y*road.Width*0.5f))
            };

            var poly = CleanOutline(outline.Select(p => new D2(p.X, p.Y)).ToList());
            if (poly.Count < 3)
                return;

            var grid = GetRoadGrid();

            // Cut every triangle along the grid, collecting the pieces by cell, then mesh each cell once.
            var cells = new Dictionary<(int x, int y), List<List<D2>>>();

            foreach (var (a, b, c) in EarClip(poly))
                ClipTriToCells(a, b, c, grid, cells);

            foreach (var cell in cells)
                MeshCell(cell.Key, cell.Value, grid, chunks);
        }

        #region Streets

        private void GenerateStreets(CityMap map)
        {
            var streetChunks = LOD.StreetChunks;

            foreach (var street in map.Streets)
                MeshStreet(street, streetChunks);
        }

        // Vertex welding: for each street chunk, a map from a rounded 2D point to its vertex index, so
        // triangles that share a corner share one vertex instead of each adding their own copy.
        private readonly Dictionary<Chunk, Dictionary<(long, long), int>> streetVertLookup = new();

        // Points closer than about 1 / WeldScale map units count as the same vertex.
        private const double WeldScale = 1e6;

        private static int StreetVertex(Chunk chunk, Dictionary<(long, long), int> lookup, D2 p, in CellSurface surface)
        {
            var key = ((long)System.Math.Round(p.x * WeldScale), (long)System.Math.Round(p.y * WeldScale));
            if (!lookup.TryGetValue(key, out int index))
            {
                index = chunk.Verts.Count;
                chunk.Verts.Add(surface.Position(p));
                lookup[key] = index;
            }
            return index;
        }

        // The terrain height at a street cell's four corners. Every road vertex inside the cell is
        // blended between them instead of being sampled separately. Along a shared cell edge the blend
        // only uses that edge's two corners, so neighbouring cells always agree and there are no cracks,
        // even when a street cell covers several terrain cells.
        private readonly struct CellSurface
        {
            private readonly double left, bottom, width, height;
            private readonly float h00, h10, h01, h11; // world y at (left,bottom) (right,bottom) (left,top) (right,top)
            private readonly float lift;

            public CellSurface(double left, double bottom, double width, double height, float lift)
            {
                this.left = left; this.bottom = bottom; this.width = width; this.height = height; this.lift = lift;
                h00 = Terrain((left, bottom)).y;
                h10 = Terrain((left + width, bottom)).y;
                h01 = Terrain((left, bottom + height)).y;
                h11 = Terrain((left + width, bottom + height)).y;
            }

            public Vector3 Position(D2 p)
            {
                float u = Mathf.Clamp01((float)((p.x - left) / width));
                float v = Mathf.Clamp01((float)((p.y - bottom) / height));
                float y = Mathf.Lerp(Mathf.Lerp(h00, h10, u), Mathf.Lerp(h01, h11, u), v);

                Vector3 w = World(p.x, p.y);
                w.y = y + lift;
                return w;
            }
        }

        [Header("Streets")]

        // How many terrain heightmap cells one street cell covers, in each direction.
        // 1 = road follows the terrain exactly. 2 = about a quarter of the vertices, but on bumpy ground
        // the road can dip under small bumps between corners, so raise roadLift to match.
        [SerializeField, Min(1)] private int streetGridStep = 1;

        // Small lift above the terrain so the road doesn't z-fight with it.
        [SerializeField] private float roadLift = 0.03f;

        // The street grid, in Outline (map) units. Its lines always fall on terrain heightmap lines.
        private struct RoadGrid
        {
            public double cellX, cellY;     // size of one street cell
            public double originX, originY; // where the grid starts
        }

        private void MeshStreet(StreetPiece road, ChunkGrid chunks)
        {
            var poly = CleanOutline(road.Outline.Select(p => new D2(p.X, p.Y)).ToList());
            if (poly.Count < 3)
                return;

            var grid = GetRoadGrid();

            // Cut every triangle along the grid, collecting the pieces by cell, then mesh each cell once.
            var cells = new Dictionary<(int x, int y), List<List<D2>>>();

            foreach (var (a, b, c) in EarClip(poly))
                ClipTriToCells(a, b, c, grid, cells);

            foreach (var cell in cells)
                MeshCell(cell.Key, cell.Value, grid, chunks);
        }

        // Map points become world points with origin + point * tileSize (see World()),
        // so the terrain's grid is converted back the other way.
        private RoadGrid GetRoadGrid()
        {
            var td = terrain.terrainData;
            int res = td.heightmapResolution;
            Vector3 tp = terrain.transform.position;
            int step = Mathf.Max(1, streetGridStep);

            return new RoadGrid
            {
                cellX = td.size.x / (res - 1) / tileSize * step,
                cellY = td.size.z / (res - 1) / tileSize * step,
                originX = (tp.x - origin.x) / tileSize,
                originY = (tp.z - origin.z) / tileSize,
            };
        }

        private readonly struct D2
        {
            public readonly double x, y;
            public D2(double x, double y) { this.x = x; this.y = y; }
            public static D2 operator -(D2 a, D2 b) => new D2(a.x - b.x, a.y - b.y);
            public static double Cross(D2 a, D2 b) => a.x * b.y - a.y * b.x;
            public static D2 Lerp(D2 a, D2 b, double t) => new D2(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
        }

        private static double SignedArea(List<D2> p)
        {
            double a = 0;
            for (int i = 0; i < p.Count; i++)
                a += D2.Cross(p[i], p[(i + 1) % p.Count]);
            return a * 0.5;
        }

        // Removes repeated points (including a closing point equal to the first), collinear points
        // and zero-width spikes, then makes the winding counter-clockwise.
        private static List<D2> CleanOutline(List<D2> pts)
        {
            const double minDist = 1e-6;
            const double minSin = 1e-9;

            var r = new List<D2>(pts);
            bool changed = true;
            while (changed && r.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < r.Count && r.Count >= 3; i++)
                {
                    D2 prev = r[(i - 1 + r.Count) % r.Count], cur = r[i], next = r[(i + 1) % r.Count];
                    D2 d1 = cur - prev, d2 = next - cur;
                    double l1 = System.Math.Sqrt(d1.x * d1.x + d1.y * d1.y);
                    double l2 = System.Math.Sqrt(d2.x * d2.x + d2.y * d2.y);

                    if (l1 < minDist || l2 < minDist || System.Math.Abs(D2.Cross(d1, d2)) <= minSin * l1 * l2)
                    {
                        r.RemoveAt(i);
                        i--;
                        changed = true;
                    }
                }
            }

            if (r.Count >= 3 && SignedArea(r) < 0)
                r.Reverse();
            return r;
        }

        // Inclusive point-in-triangle test for a counter-clockwise triangle.
        private static bool InTriangle(D2 p, D2 a, D2 b, D2 c) =>
            D2.Cross(b - a, p - a) >= 0 &&
            D2.Cross(c - b, p - b) >= 0 &&
            D2.Cross(a - c, p - c) >= 0;

        // Ear clipping for a simple, counter-clockwise polygon. Returns counter-clockwise triangles.
        private static List<(D2, D2, D2)> EarClip(List<D2> poly)
        {
            var result = new List<(D2, D2, D2)>(poly.Count);
            var idx = Enumerable.Range(0, poly.Count).ToList();

            while (idx.Count > 3)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ip = idx[(i - 1 + idx.Count) % idx.Count];
                    int ic = idx[i];
                    int ix = idx[(i + 1) % idx.Count];
                    D2 a = poly[ip], b = poly[ic], c = poly[ix];

                    if (D2.Cross(b - a, c - b) <= 0)
                        continue; // reflex corner, not an ear

                    bool empty = true;
                    foreach (int j in idx)
                    {
                        if (j == ip || j == ic || j == ix) continue;
                        if (InTriangle(poly[j], a, b, c)) { empty = false; break; }
                    }
                    if (!empty)
                        continue;

                    result.Add((a, b, c));
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }

                if (!clipped)
                {
                    // Only happens with a self-intersecting outline: drop the flattest corner
                    // rather than looping forever.
                    int worst = 0;
                    double best = double.MaxValue;
                    for (int i = 0; i < idx.Count; i++)
                    {
                        D2 a = poly[idx[(i - 1 + idx.Count) % idx.Count]], b = poly[idx[i]], c = poly[idx[(i + 1) % idx.Count]];
                        double area = System.Math.Abs(D2.Cross(b - a, c - b));
                        if (area < best) { best = area; worst = i; }
                    }
                    idx.RemoveAt(worst);
                }
            }

            if (idx.Count == 3)
                result.Add((poly[idx[0]], poly[idx[1]], poly[idx[2]]));
            return result;
        }

        // Cuts a convex polygon along one axis-aligned line, keeping the side where
        // sign * (coord - value) >= 0. The result is still convex (or empty).
        private static List<D2> ClipAxis(List<D2> poly, bool useX, double value, double sign)
        {
            var outp = new List<D2>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                D2 p = poly[i], q = poly[(i + 1) % poly.Count];
                double dp = sign * ((useX ? p.x : p.y) - value);
                double dq = sign * ((useX ? q.x : q.y) - value);

                if (dp >= 0)
                    outp.Add(p);

                if ((dp >= 0) != (dq >= 0))
                {
                    D2 hit = D2.Lerp(p, q, dp / (dp - dq));
                    // Snap exactly onto the grid line so neighbouring cells share the vertex.
                    outp.Add(useX ? new D2(value, hit.y) : new D2(hit.x, value));
                }
            }
            return outp;
        }

        // Cuts one 2D triangle along the street grid and adds each piece to the list for its cell.
        private static void ClipTriToCells(D2 a, D2 b, D2 c, RoadGrid g, Dictionary<(int x, int y), List<List<D2>>> cells)
        {
            int gx0 = (int)System.Math.Floor((System.Math.Min(a.x, System.Math.Min(b.x, c.x)) - g.originX) / g.cellX);
            int gx1 = (int)System.Math.Floor((System.Math.Max(a.x, System.Math.Max(b.x, c.x)) - g.originX) / g.cellX);
            int gy0 = (int)System.Math.Floor((System.Math.Min(a.y, System.Math.Min(b.y, c.y)) - g.originY) / g.cellY);
            int gy1 = (int)System.Math.Floor((System.Math.Max(a.y, System.Math.Max(b.y, c.y)) - g.originY) / g.cellY);

            var tri = new List<D2>(3) { a, b, c };
            double minArea = 1e-10 * g.cellX * g.cellY;

            for (int gx = gx0; gx <= gx1; gx++)
            {
                double left = g.originX + gx * g.cellX, right = left + g.cellX;

                // Clip to the column first, then cut that strip into rows.
                var column = ClipAxis(ClipAxis(tri, true, left, +1), true, right, -1);
                if (column.Count < 3)
                    continue;

                for (int gy = gy0; gy <= gy1; gy++)
                {
                    double bottom = g.originY + gy * g.cellY, top = bottom + g.cellY;

                    var piece = ClipAxis(ClipAxis(column, false, bottom, +1), false, top, -1);
                    if (piece.Count < 3 || SignedArea(piece) < minArea)
                        continue;

                    if (!cells.TryGetValue((gx, gy), out var list))
                        cells[(gx, gy)] = list = new List<List<D2>>();
                    list.Add(piece);
                }
            }
        }

        // Meshes all the road pieces in one street cell. If they cover the whole cell, it is drawn as a
        // single quad (2 triangles, like the terrain) instead of all the small pieces.
        private void MeshCell((int x, int y) cell, List<List<D2>> pieces, RoadGrid g, ChunkGrid chunks)
        {
            double left = g.originX + cell.x * g.cellX, right = left + g.cellX;
            double bottom = g.originY + cell.y * g.cellY, top = bottom + g.cellY;
            double cellArea = g.cellX * g.cellY;
            double minArea = 1e-10 * cellArea;

            // Ear-clip triangles never overlap, so their pieces add up to exactly how much of the cell is road.
            double covered = 0;
            foreach (var piece in pieces)
                covered += SignedArea(piece);

            if (covered >= cellArea * (1 - 1e-7))
            {
                pieces = new List<List<D2>>
                {
                    new List<D2> { new D2(left, bottom), new D2(right, bottom), new D2(right, top), new D2(left, top) }
                };
            }

            // The whole cell goes into one chunk, picked by its centre.
            Chunk chunk = chunks.ChunkAt(World((left + right) * 0.5, (bottom + top) * 0.5));

            if (!streetVertLookup.TryGetValue(chunk, out var lookup))
                streetVertLookup[chunk] = lookup = new Dictionary<(long, long), int>();

            var surface = new CellSurface(left, bottom, g.cellX, g.cellY, roadLift);

            foreach (var piece in pieces)
            {
                var idx = new int[piece.Count];
                for (int i = 0; i < piece.Count; i++)
                    idx[i] = StreetVertex(chunk, lookup, piece[i], surface);

                // Each piece is convex, so fan it. Skip slivers and triangles that welding collapsed.
                for (int k = 1; k + 1 < piece.Count; k++)
                {
                    if (D2.Cross(piece[k] - piece[0], piece[k + 1] - piece[0]) < minArea)
                        continue;
                    if (idx[0] == idx[k] || idx[k] == idx[k + 1] || idx[0] == idx[k + 1])
                        continue;

                    // Pieces are counter-clockwise in 2D. Unity treats clockwise as front-facing when
                    // seen from above, so write them as 0, k+1, k. Moving points straight up onto the
                    // terrain never changes the order seen from above, so this always faces up.
                    chunk.Tris.Add(idx[0]);
                    chunk.Tris.Add(idx[k + 1]);
                    chunk.Tris.Add(idx[k]);
                }
            }
        }

        #endregion


        #region General Mesh

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

        #endregion

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
            return origin + V3(point.x * tileSize, point.y * tileSize, h);
        }
        private static Vector3 World(double x, double y, int h = 0) => World((x, y), h);

        // Maps a Frame's local point (x, y) & h into a final Vector3 world position, accounting for the current
        // heightmap and terrain.
        // (x, y) = local tile's point in frame
        // h = height: additional vertical offset applied to vector
        // frame = the frame the point belongs to
        private static Vector3 Terrain((double x, double y) point, int h = 0)
        {
            var wrld = World(point.x, point.y, h);

            return wrld + Vector3.up * CityBuilder.GetTerrainHeight(terrain, heightMap, wrld.x, wrld.z);
        }
        private static Vector3 Terrain(double x, double y, int h = 0) =>
            Terrain((x, y), h);
    }
}
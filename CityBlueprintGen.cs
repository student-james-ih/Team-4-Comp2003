// =====================================================================
//  2D CITY BLUEPRINT GENERATOR
//
//  HOW THE DATA MAPS TO 3D LATER
//  The console shows everything snapped to square tiles, but the exact
//  shapes are kept alongside, so 3D never has to be built from the squares.
//
//  Building the city in 3D, layer by layer (all in map units; in a Y-up
//  engine use world = (x * tileSize, 0, y * tileSize)):
//   1. Ground: a grass plane under the whole city. Leftover corners beside
//      the avenues are grass, so they need nothing extra.
//   2. Main road and avenues: CityMap.SignificantRoads - exact lines with Width,
//      plus SidewalkWidth of pavement each side.
//   3. Sector streets: CityMap.Streets - each piece's Outline is the exact
//      road surface (already trimmed to stay on its own side of the avenues).
//   4. Ground surfaces: CityMap.Surfaces - pavement, yards, gardens, parks,
//      car parks, as rectangles on their sector's grid.
//   5. Buildings: Building.LocalTiles - unit squares on the sector's grid,
//      each with its own height in floors (so tiered towers come out right).
//  A sector's grid is turned and placed by its Frame (CityMap.Sectors):
//  a local position (u, v) is at Frame.ToWorld(u, v); a local tile (u, v)
//  is the unit square from (u, v) to (u + 1, v + 1), rotated by
//  Frame.AngleDegrees. Anything snapped to map tiles (Cell, Building.Tiles,
//  the stub X / Y) is only for the console view.
//
//  Other data:
//  - Stubs (CityMap.Stubs): ExactX / ExactY is the exact end point and Angle
//    the exact heading, ready to join a road outside the city. AtObstacle
//    means a blocked area stopped it (a place for a bridge or tunnel).
//  - Every building has a Kind (office, shop, terrace, house...) and a
//    District; every block's district is in CityMap.SectorBlocks.
//  - CityMap.Parks / LargePlots list multi-block areas (rects on their
//    sector's grid). Obstacles are kept as exact shapes too.
//  - BLOCKED AREAS: the city never builds in them. Add Obstacle shapes to
//    CitySettings.Obstacles, or set CitySettings.IsBlocked to mark tiles
//    from your own data. Blocks and their surrounding roads must be wholly
//    clear of them, so nothing is ever cut.
// =====================================================================

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace CityGen
{

    // =========================== DATA MODEL ===========================

    // Empty = countryside, Ground = paved/open yard, Garden = private gardens
    // Blocked = an area the city must keep out of (see Obstacle / CitySettings.IsBlocked)
    public enum TileType { Empty, Road, Sidewalk, Ground, Building, Park, CarPark, Garden, Blocked }

    [Flags]
    public enum RoadFlags
    {
        None = 0,
        Horizontal = 1,     // runs left-right
        Vertical = 2,       // runs up-down
        CentreLine = 4,     // middle lane of the road (for markings)
        Major = 8,          // major road; otherwise minor
        Stub = 16,          // part of a stub leaving the city
        StubEnd = 32,       // the outermost centre tile of a stub
        Main = 64,          // the straight main road (its centre tile is a grass median)
        Angled = 128        // part of a diagonal avenue or a road on a rotated grid (see Cell.RoadAngle)
    }

    public enum BuildingKind { Office, Shop, Apartment, Terrace, House, Warehouse, IndustrialUnit }

    public enum District { Downtown, Commercial, Residential, Suburban, Industrial }

    public enum PlotKind { Warehouse, IndustrialEstate, CarPark }

    public enum Compass { North, East, South, West }

    public enum BuildingShape { Rectangle, LShape, UShape, Courtyard, Cross, Tiered }

    public enum HeightTier { None, LowRise, MidRise, HighRise, Skyscraper }

    public enum ViewMode { Tiers, Heights }

    public struct Cell
    {
        public TileType Type;
        public RoadFlags Road;    // only used when Type == Road
        public int Floors;        // storeys; 0 for flat tiles
        public int BuildingId;    // index into CityMap.Buildings, -1 if none
        public short RoadAngle;   // angled roads: direction in degrees (0 = left-right, 90 = up-down)

        public static readonly Cell Countryside = new() { Type = TileType.Empty, BuildingId = -1 };
    }

    readonly public struct Rect : IEquatable<Rect>
    {
        public int X { get; }
        public int Y { get; }
        public int W { get; }
        public int H { get; }

        public Rect(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public int Right => X + W;     // exclusive
        public int Bottom => Y + H;    // exclusive
        public double CentreX => X + W / 2.0;
        public double CentreY => Y + H / 2.0;
        public Rect Shrink(int n) => new(X + n, Y + n, W - 2 * n, H - 2 * n);

        public bool Equals(Rect other)
        {
            return X == other.X &&
                Y == other.Y &&
                W == other.W &&
                H == other.H;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, W, H);
        }

        public override bool Equals(Object otherObj) => otherObj is Rect otherRect && Equals(otherRect);

        public static bool operator ==(Rect a, Rect b) { return a.Equals(b); }
        public static bool operator !=(Rect a, Rect b) { return !a.Equals(b); }
    }

    // One straight road as laid out (before trimming to the city)
    readonly public struct RoadSegment : IEquatable<RoadSegment>
    {
        public Rect Area { get; }
        public bool Horizontal { get; }
        public bool Major { get; }
        public bool Main { get; }

        public RoadSegment(Rect area, bool horizontal, bool major, bool main = false)
        {
            Area = area;
            Horizontal = horizontal;
            Major = major;
            Main = main;
        }

        public int Width => Horizontal ? Area.H : Area.W;

        public bool Equals(RoadSegment other)
        {
            return Area == other.Area &&
                Horizontal == other.Horizontal &&
                Major == other.Major &&
                Main == other.Main;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Area, Horizontal, Major, Main);
        }

        public override bool Equals(Object otherObj) => otherObj is RoadSegment otherRoadSegment && Equals(otherRoadSegment);

        public static bool operator ==(RoadSegment a, RoadSegment b) { return a.Equals(b); }
        public static bool operator !=(RoadSegment a, RoadSegment b) { return !a.Equals(b); }
    }

    // Where a major road leaves the city. (X, Y) is the outermost centre tile;
    // a road outside the city should connect there, heading in Direction.
    // Angle is the exact heading in degrees (0 = east, 90 = south). Avenues leave
    // at any angle; Direction is just the nearest compass point.
    // AtObstacle: the road stops here because a blocked area is in the way
    // (somewhere to add a bridge, tunnel etc. later), not at the city edge.
    readonly public struct RoadStub : IEquatable<RoadStub>
    {
        public int X { get; }
        public int Y { get; }
        public Compass Direction { get; }
        public bool Major { get; }
        public int Width { get; }
        public Rect Area { get; }
        public double Angle { get; }
        public bool AtObstacle { get; }
        public double ExactX { get; }
        public double ExactY { get; }

        public RoadStub(int x, int y, Compass direction, bool major, int width, Rect area,
                                    double angle, bool atObstacle = false,
                                    double exactX = 0, double exactY = 0) // exact end point (map units)
        {
            X = x;
            Y = y;
            Direction = direction;
            Major = major;
            Width = width;
            Area = area;
            Angle = angle;
            AtObstacle = atObstacle;
            ExactX = exactX;
            ExactY = exactY;
        }

        public bool Equals(RoadStub other)
        {
            return X == other.X &&
                Y == other.Y &&
                Direction == other.Direction &&
                Major == other.Major &&
                Width == other.Width &&
                Area == other.Area &&
                Angle == other.Angle &&
                AtObstacle == other.AtObstacle &&
                ExactX == other.ExactX &&
                ExactY == other.ExactY;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(  // HashCode.Combine supports a max of 8 arguments so I have nested it (otherwise I would have to do .Add 10 times and I think this is cleaner)
                HashCode.Combine(X, Y, Direction, Major, Width, Area, Angle, AtObstacle),
                ExactX, ExactY);
        }

        public override bool Equals(Object otherObj) => otherObj is RoadStub otherRoadStub && Equals(otherRoadStub);

        public static bool operator ==(RoadStub a, RoadStub b) { return a.Equals(b); }
        public static bool operator !=(RoadStub a, RoadStub b) { return !a.Equals(b); }
    }

    public enum ObstacleShape { Ellipse, Line }

    // An area the city must not build in (water, cliffs, a reserve...). Line is
    // a straight band with rounded ends; Ellipse is an oval. Positions and
    // sizes are in map tiles; the angle turns its length away from left-right.
    public class Obstacle
    {
        public ObstacleShape Shape = ObstacleShape.Ellipse;
        public double CentreX, CentreY;
        public double Length, Thickness;
        public double AngleDegrees;

        public bool Contains(double x, double y)
        {
            double a = AngleDegrees * Math.PI / 180;
            double dx = x - CentreX, dy = y - CentreY;
            double along = dx * Math.Cos(a) + dy * Math.Sin(a);
            double across = -dx * Math.Sin(a) + dy * Math.Cos(a);
            double halfLength = Length / 2, halfThickness = Thickness / 2;
            if (Shape == ObstacleShape.Ellipse)
                return Math.Pow(along / halfLength, 2) + Math.Pow(across / halfThickness, 2) <= 1;
            double beyond = Math.Max(0, Math.Abs(along) - (halfLength - halfThickness));
            return beyond * beyond + across * across <= halfThickness * halfThickness;
        }
    }

    // A road that isn't on the grid (diagonal avenue), as a polyline
    // in tile coordinates, ready to become a smooth spline in 3D
    public class SignificantRoad
    {
        public string Kind = "";
        public int Width;
        public int SidewalkWidth;          // pavement along each side (in the city)
        public List<(double X, double Y)> Points = new();
    }

    // A straight piece of street inside a sector, exactly. Area is a rectangle on
    // the sector's grid (AlongU = it runs along the grid's u axis). Outline is the
    // same rectangle in map units, trimmed so it stays on its own side of the
    // main road / avenues; use it to build the road surface in 3D.
    public class StreetPiece
    {
        public int Sector;
        public Rect Area;
        public bool AlongU;
        public bool Major;
        public bool Stub;                  // part of a stub leaving the city
        public List<(double X, double Y)> Outline = new();
        public int Width => AlongU ? Area.H : Area.W;
    }

    // A patch of ground surface inside a sector, exactly: a rectangle on the
    // sector's grid with one surface type (Sidewalk, Ground, Garden, Park or
    // CarPark). Together with the streets and buildings these cover each sector.
    public class SurfacePatch
    {
        public int Sector;
        public TileType Type;
        public Rect Area;
    }

    // Turns a sector's local grid into map positions: local (u, v) is rotated by
    // AngleDegrees and moved to (OriginX, OriginY). In 3D, a local tile (u, v)
    // is a unit square centred on ToWorld(u + 0.5, v + 0.5), rotated by the angle.
    readonly public struct Frame : IEquatable<Frame>
    {
        public double OriginX { get; }
        public double OriginY { get; }
        public double AngleDegrees { get; }

        public Frame(double originX, double originY, double angleDegrees)
        {
            OriginX = originX;
            OriginY = originY;
            AngleDegrees = angleDegrees;

            Cos = Math.Cos(AngleDegrees * Math.PI / 180);
            Sin = Math.Sin(AngleDegrees * Math.PI / 180);
        }

        readonly double Cos;
        readonly double Sin;

        public (double X, double Y) ToWorld(double u, double v) =>
            (OriginX + u * Cos - v * Sin, OriginY + u * Sin + v * Cos);

        public (double U, double V) ToLocal(double x, double y)
        {
            double dx = x - OriginX, dy = y - OriginY;
            return (dx * Cos + dy * Sin, -dx * Sin + dy * Cos);
        }

        public bool Equals(Frame other)
        {
            return OriginX == other.OriginX &&
                OriginY == other.OriginY &&
                AngleDegrees == other.AngleDegrees;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(OriginX, OriginY, AngleDegrees);
        }

        public override bool Equals(Object otherObj) => otherObj is Frame otherFrame && Equals(otherFrame);

        public static bool operator ==(Frame a, Frame b) { return a.Equals(b); }
        public static bool operator !=(Frame a, Frame b) { return !a.Equals(b); }
    }

    // A part of the city between the main road / avenues, with its own grid angle
    public class Sector
    {
        public int Id;
        public int Key;                    // which side of each boundary road it's on
        public double Angle;               // grid angle in degrees (0 = square to the map)
        public Frame Frame;                // local grid -> map positions
        public int LocalWidth, LocalHeight;
    }

    readonly public struct BuildingTile : IEquatable<BuildingTile>
    {
        public int X { get; }
        public int Y { get; }
        public int Floors { get; }

        public BuildingTile(int x, int y, int floors)
        {
            X = x;
            Y = y;
            Floors = floors;
        }

        public bool Equals(BuildingTile other)
        {
            return X == other.X &&
                Y == other.Y &&
                Floors == other.Floors;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Floors);
        }

        public override bool Equals(Object otherObj) => otherObj is BuildingTile otherBuildingTile && Equals(otherBuildingTile);

        public static bool operator ==(BuildingTile a, BuildingTile b) { return a.Equals(b); }
        public static bool operator !=(BuildingTile a, BuildingTile b) { return !a.Equals(b); }
    }

    public class Building
    {
        public int Id;
        public BuildingShape Shape;
        public BuildingKind Kind = BuildingKind.Office;
        public District District;
        public Rect Lot;                              // the plot it sits on (sector's local grid)
        public int MaxFloors;
        public List<BuildingTile> Tiles = new();      // every map tile it covers + its height
        public int Sector;                            // which sector (and so which grid angle)
        public List<BuildingTile> LocalTiles = new(); // exact footprint on the sector's grid (use for 3D)
        public HeightTier Tier => HeightTiers.Of(MaxFloors);
    }

    // A park made of one or more neighbouring blocks. Joins are the road strips
    // that used to separate those blocks and are now part of the park.
    public class Park
    {
        public int Sector;                 // rects are on this sector's local grid
        public List<Rect> Blocks = new();
        public List<Rect> Joins = new();
    }

    // A plot bigger than one block (several blocks in a row, with the minor
    // roads between them removed). Area covers all of it, including its sidewalk.
    public class LargePlot
    {
        public int Sector;                 // rects are on this sector's local grid
        public PlotKind Kind;
        public Rect Area;
        public List<Rect> Blocks = new();
    }

    static public class HeightTiers
    {
        public static HeightTier Of(int floors) => floors switch
        {
            <= 0 => HeightTier.None,
            <= 4 => HeightTier.LowRise,
            <= 15 => HeightTier.MidRise,
            <= 39 => HeightTier.HighRise,
            _ => HeightTier.Skyscraper
        };

        public static string Range(HeightTier tier) => tier switch
        {
            HeightTier.LowRise => "1-4 floors",
            HeightTier.MidRise => "5-15 floors",
            HeightTier.HighRise => "16-39 floors",
            HeightTier.Skyscraper => "40+ floors",
            _ => ""
        };
    }

    public class CityMap
    {
        public readonly int Width, Height, Seed;
        public readonly Cell[,] Cells;
        public readonly CityShape Shape;                    // the city outline
        public readonly List<Building> Buildings = new();
        public readonly List<RoadSegment> Roads = new();    // full layout, before trimming
        public readonly List<RoadStub> Stubs = new();
        public readonly List<Rect> Blocks = new();          // every block in the layout
        public readonly List<Rect> CityBlocks = new();      // blocks that are part of the city
        public readonly List<Park> Parks = new();
        public readonly List<LargePlot> LargePlots = new();
        public readonly Dictionary<Rect, District> BlockDistricts = new();   // (while laying out a sector)
        public readonly List<SignificantRoad> SignificantRoads = new();
        public readonly List<StreetPiece> Streets = new();         // exact sector streets
        public readonly List<SurfacePatch> Surfaces = new();       // exact sector ground surfaces
        public readonly List<Sector> Sectors = new();
        public readonly List<(int Sector, Rect Block, District District)> SectorBlocks = new();

        // Smooth random fields (0-1) used for heights and district layout
        public SmoothNoise HeightField = null!, DistrictField = null!, IndustryField = null!;

        // While laying out a sector, this map is the sector's local grid:
        // Frame turns local positions into map positions, Allowed says whether a
        // map position is inside the sector (and clear of the boundary roads),
        // and BoundaryGap is the distance from a map position to the nearest
        // boundary road's edge. For the finished map they do nothing.
        public Frame Frame = new(0, 0, 0);
        public Func<double, double, bool> Allowed = (_, _) => true;
        public Func<double, double, double> BoundaryGap = (_, _) => double.MaxValue;

        // Blocked areas. Blocked is exact; BlockedNear is grown by the clearance
        // (plus a tile, so a turned grid sampled tile by tile can't slip past).
        // BlockedAt tests a map position against BlockedNear. BlockMargin is how
        // far round a block must also be clear, so the roads around it fit.
        public bool[,] Blocked = null!, BlockedNear = null!;
        public readonly List<Obstacle> Obstacles = new();
        public Func<double, double, bool> BlockedAt = (_, _) => false;
        public int BlockMargin;

        // True if no block anywhere inside this local rectangle could be used, so
        // the road splitting can skip it. Set while laying out a sector.
        public Func<Rect, bool> Unusable = _ => false;

        // True if the local tile is in or too close to a blocked area
        public bool LocalBlocked(int lx, int ly)
        {
            var (x, y) = ToWorld(lx + 0.5, ly + 0.5);
            return BlockedAt(x, y);
        }

        public (double X, double Y) ToWorld(double lx, double ly) => Frame.ToWorld(lx, ly);

        // Distance from the city centre (0) to the edge (1) for a local position;
        // anywhere outside the sector counts as far outside the city
        public double EdgeAt(double lx, double ly)
        {
            var (x, y) = ToWorld(lx, ly);
            return Allowed(x, y) ? Shape.EdgeDistance(x, y) : 99;
        }

        public double HeightAt(double lx, double ly) { var (x, y) = ToWorld(lx, ly); return HeightField.At(x, y); }
        public double DistrictAt(double lx, double ly) { var (x, y) = ToWorld(lx, ly); return DistrictField.At(x, y); }
        public double IndustryAt(double lx, double ly) { var (x, y) = ToWorld(lx, ly); return IndustryField.At(x, y); }

        // True if the whole block lies inside the sector, clear of boundary roads
        // (the sector minus those roads is convex, so the corners are enough)
        public bool FitsBlock(Rect b)
        {
            foreach (var (u, v) in new (double, double)[] { (b.X, b.Y), (b.Right, b.Y), (b.X, b.Bottom), (b.Right, b.Bottom) })
            {
                var (x, y) = ToWorld(u, v);
                if (!Allowed(x, y)) return false;
            }
            // ...and it, plus room for the roads round it, is clear of blocked areas
            for (int lx = b.X - BlockMargin; lx < b.Right + BlockMargin; lx++)
                for (int ly = b.Y - BlockMargin; ly < b.Bottom + BlockMargin; ly++)
                    if (LocalBlocked(lx, ly)) return false;
            return true;
        }

        public CityMap(int width, int height, int seed, CityShape shape)
        {
            Width = width; Height = height; Seed = seed; Shape = shape;
            Cells = new Cell[width, height];
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    Cells[x, y].BuildingId = -1;
        }

        public ref Cell this[int x, int y] => ref Cells[x, y];
        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    }

    [System.Serializable]
    public class CitySettings
    {
        public int Width = 200;
        public int Height = 100;
        public int? Seed = null;

        // City outline
        public double CitySize = 0.92;        // how much of the map the city can fill (0-1)
        public double EdgeRoughness = 0.18;   // 0 = oval, 0.3+ = very lumpy with lobes
        public double EdgeJitter = 0.12;      // randomness in which edge blocks get included
        public double OutskirtsVacancy = 0.4; // chance an edge lot is left as countryside

        // Roads
        public bool StraightMainRoad = false; // a dead-straight main road through the middle of the city
        public int MainRoadWidth = 7;         // odd widths get a centre line (a grass median here)
        public int MajorRoadWidth = 5;        // odd widths get a centre line
        public int MinorRoadWidth = 3;
        public int MajorRoadDepth = 2;        // roads from the first N+1 levels of cuts are major
        public int StubLength = 3;            // how far major roads poke out of the city
        public int SidewalkWidth = 1;

        public double CrossroadChance = 0.6;  // chance side streets line up across a road (crossroads)

        // Roads at angles
        public int DiagonalAvenues = 2;       // straight avenues crossing the city at an angle (0 = none)
        public double AvenueMinAngle = 25;    // how far off the grid they run, in degrees (25-65 looks
        public double AvenueMaxAngle = 65;    //   best; near 0 or 90 they just run alongside grid roads)
        public double AvenueOffset = 0.25;    // how far from the centre they may pass (0 = all through the centre)
        public double MinAvenueAngleGap = 25; // roads closer in direction than this (degrees) must be spaced apart...
        public double MinAvenueSpacing = 20;  // ...by at least this many tiles everywhere inside the city

        // Sectors: the main road and avenues split the city into sectors, each
        // with its own grid angle. Most line up with a road running along them.
        public double SectorFreeAngleChance = 0.2;   // chance a sector picks its own random angle instead
        public double ConnectorSpacing = 10;  // a sector street joins each avenue / main road at least this
                                              //   often (in tiles along it); lower = more junctions
        public int MaxConnectorLength = 30;   // furthest a street is extended to reach one

        // Blocked areas: the city never builds in these (water, cliffs, reserves...).
        // Add shapes to Obstacles, and/or set IsBlocked to mark tiles from your own
        // data, e.g.  IsBlocked = (x, y) => myTerrain.IsWater(x, y)
        public List<Obstacle> Obstacles = new();
        public Func<int, int, bool>? IsBlocked = null;
        public int ObstacleClearance = 1;     // gap kept between the city and a blocked area (tiles)

        // A random test obstacle, to try the avoidance out
        public bool TestObstacle = false;
        public double TestObstacleDistance = 0.4;   // from the city centre (0 = centre, 1 = city edge)
        public double TestObstacleMinLength = 0.3, TestObstacleMaxLength = 0.8;     // fraction of the city's radius
        public double TestObstacleMinThickness = 4, TestObstacleMaxThickness = 12;  // tiles

        // Blocks (area enclosed by roads, incl. sidewalk). Sizes blend from the
        // centre values to the edge values, so the core is tight and suburbs loose.
        public int MinBlockSizeCentre = 6, MaxBlockSizeCentre = 11;
        public int MinBlockSizeEdge = 9, MaxBlockSizeEdge = 20;

        // Districts (measured from the centre: 0 = centre, 1 = city edge)
        public double DowntownSize = 0.3;     // offices and towers inside this
        public double InnerCitySize = 0.62;   // terraces / apartments inside this, suburbs outside
        public double IndustrialShare = 0.25; // roughly how much of the outer city is industrial
        public double RibbonReach = 0.45;     // how far past the edge houses line major roads (0 = off)
        public double PlazaChance = 0.06;     // downtown lot left as an open plaza

        // Parks and large plots (both made by merging neighbouring city blocks)
        public double ParkRatio = 20;         // city blocks per park block (0 = no multi-block parks)
        public int MinParkBlocks = 2, MaxParkBlocks = 6;
        public double LargePlotRatio = 15;    // city blocks per large-plot block (0 = none)
        public int MinLargePlotBlocks = 2, MaxLargePlotBlocks = 4;   // warehouses, industrial estates, car parks
    }

    // Smooth 2D value noise: random values on a coarse grid, blended smoothly
    // between grid points, with a second finer layer for detail. At() returns 0-1.
    public class SmoothNoise
    {
        readonly double[,] coarse, fine;
        readonly double cell;

        public SmoothNoise(int width, int height, double cellSize, Random rng)
        {
            cell = cellSize;
            coarse = RandomGrid(width / cellSize, height / cellSize, rng);
            fine = RandomGrid(width / (cellSize / 2.5), height / (cellSize / 2.5), rng);
        }

        static double[,] RandomGrid(double w, double h, Random rng)
        {
            var g = new double[(int)w + 3, (int)h + 3];
            for (int x = 0; x < g.GetLength(0); x++)
                for (int y = 0; y < g.GetLength(1); y++)
                    g[x, y] = rng.NextDouble();
            return g;
        }

        static double Sample(double[,] g, double gx, double gy)
        {
            int x0 = Math.Clamp((int)Math.Floor(gx), 0, g.GetLength(0) - 2);
            int y0 = Math.Clamp((int)Math.Floor(gy), 0, g.GetLength(1) - 2);
            double fx = Math.Clamp(gx - x0, 0, 1), fy = Math.Clamp(gy - y0, 0, 1);
            fx = fx * fx * (3 - 2 * fx);                  // smoothstep
            fy = fy * fy * (3 - 2 * fy);
            double top = g[x0, y0] + (g[x0 + 1, y0] - g[x0, y0]) * fx;
            double bottom = g[x0, y0 + 1] + (g[x0 + 1, y0 + 1] - g[x0, y0 + 1]) * fx;
            return top + (bottom - top) * fy;
        }

        public double At(double x, double y)
        {
            double v = Sample(coarse, x / cell, y / cell) * 0.7 +
                       Sample(fine, x / (cell / 2.5), y / (cell / 2.5)) * 0.3;
            return Math.Clamp((v - 0.5) * 1.8 + 0.5, 0, 1);   // stretch back to a full 0-1 range
        }
    }

    // Irregular city outline: a circle (stretched to the map) whose radius
    // wobbles with angle, using a few random sine waves.
    public class CityShape
    {
        public readonly double CentreX, CentreY, RadiusX, RadiusY;
        public readonly double MaxWobble;
        readonly (int Freq, double Amp, double Phase)[] waves;

        public CityShape(int width, int height, double size, double roughness, Random rng)
        {
            CentreX = width / 2.0;
            CentreY = height / 2.0;

            var list = new List<(int, double, double)>();
            for (int k = 1; k <= 6; k++)      // big lobes and bulges
                list.Add((k, roughness * rng.NextDouble() / Math.Max(1, k - 1), rng.NextDouble() * Math.PI * 2));
            for (int k = 7; k <= 13; k++)     // small ragged detail
                list.Add((k, roughness * 0.15 * rng.NextDouble(), rng.NextDouble() * Math.PI * 2));
            waves = list.ToArray();

            // Scale so the biggest bulge still fits inside the map
            double maxWobble = 0;
            for (int i = 0; i < 360; i++)
                maxWobble = Math.Max(maxWobble, Wobble(i * Math.PI / 180));
            RadiusX = (width / 2.0 - 1) * size / maxWobble;
            RadiusY = (height / 2.0 - 1) * size / maxWobble;
            MaxWobble = maxWobble;
        }

        double Wobble(double angle)
        {
            double r = 1;
            foreach (var (freq, amp, phase) in waves)
                r += amp * Math.Sin(freq * angle + phase);
            return Math.Max(0.3, r);
        }

        // 0 at the centre, 1 on the city edge, > 1 outside. Takes map coordinates
        // (tile centres are x + 0.5, y + 0.5).
        public double EdgeDistance(double x, double y)
        {
            double nx = (x - CentreX) / RadiusX;
            double ny = (y - CentreY) / RadiusY;
            return Math.Sqrt(nx * nx + ny * ny) / Wobble(Math.Atan2(ny, nx));
        }

        public bool Contains(int x, int y) => EdgeDistance(x + 0.5, y + 0.5) <= 1;

        // The point at the given angle from the centre (radians, 0 = east,
        // pi/2 = south), at a fraction of the way to the city edge
        public (double X, double Y) PointAt(double angle, double fraction)
        {
            double r = Wobble(angle) * fraction;
            return (CentreX + Math.Cos(angle) * RadiusX * r, CentreY + Math.Sin(angle) * RadiusY * r);
        }
    }


    // =========================== GENERATOR ===========================

    static class BlueprintGenerator
    {
        // ---------------- Per-block plans ----------------

        public enum ParcelType { Tower, Shop, Terrace, Apartment, House, IndustrialUnit, Plaza, Vacant }

        sealed class Parcel
        {
            public Rect Area;
            public ParcelType Type;
            public Compass Street;      // which side faces the road (perimeter layouts)
        }

        sealed class BlockPlan
        {
            public Rect Block;
            public District District;
            public bool Ribbon;             // outside the outline, houses along one major road
            public Compass RibbonSide;      // the side facing that road
            public List<Parcel> Parcels = new();
            public bool BuildsSomething =>
                Parcels.Any(p => p.Type != ParcelType.Vacant && p.Type != ParcelType.Plaza);
        }

        // A straight road splitting the city into sectors (the main road and
        // the diagonal avenues). Drawn across the city with a stub at each end.
        // One drawn stretch of a boundary road (it's split where a blocked area
        // is in the way). Stub = the city edge at that end; Blocked = cut short
        // by a blocked area at that end.
        sealed class BoundaryPiece
        {
            public SignificantRoad Road = null!;
            public bool StubAtStart, StubAtEnd, BlockedAtStart, BlockedAtEnd;
        }

        sealed class BoundaryRoad
        {
            public List<BoundaryPiece> Pieces = new();
            public double PX, PY, DX, DY;      // a point on it and its direction
            public double Degrees;             // direction, 0 = left-right, 90 = up-down
            public double Half;                // half its width
            public bool Main;

            // Signed distance from the road's centre line (the sign says which side)
            public double SignedDistance(double x, double y) => (x - PX) * -DY + (y - PY) * DX;
        }

        // Which side of each boundary road a point is on, as a bit pattern
        static int SideKey(List<BoundaryRoad> boundaries, double x, double y)
        {
            int key = 0;
            for (int i = 0; i < boundaries.Count; i++)
                if (boundaries[i].SignedDistance(x, y) > 0) key |= 1 << i;
            return key;
        }

        public static CityMap Generate(CitySettings s)
        {
            int seed = s.Seed ?? Environment.TickCount;
            var rng = new Random(seed);

            int width = Math.Max(s.Width, 40), height = Math.Max(s.Height, 30);
            var shape = new CityShape(width, height, s.CitySize, s.EdgeRoughness, rng);
            var world = new CityMap(width, height, seed, shape)
            {
                HeightField = new SmoothNoise(width, height, 14, rng),     // skyline peaks & dips
                DistrictField = new SmoothNoise(width, height, 22, rng),   // wobbles district boundaries
                IndustryField = new SmoothNoise(width, height, 30, rng),   // where industry clusters
            };

            // 0. Areas the city must keep out of
            BuildBlockedMask(world, s, rng);

            // 1. The straight roads that split the city into sectors
            var boundaries = LayOutBoundaryRoads(world, s, rng);

            // 2. Group the tiles into sectors by which side of each road they're
            //    on, and note which roads run along each sector. Only tiles the
            //    city could reach count (edge blocks / houses along roads out of town).
            double reachLimit = 1 + Math.Max(s.RibbonReach, s.EdgeJitter);
            var keyOf = new int[width, height];
            var inReach = new bool[width, height];
            var sectorKeys = new List<int>();
            var nearRoads = new Dictionary<int, HashSet<int>>();
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                {
                    double px = x + 0.5, py = y + 0.5;
                    int key = SideKey(boundaries, px, py);
                    keyOf[x, y] = key;
                    if (shape.EdgeDistance(px, py) > reachLimit) continue;
                    inReach[x, y] = true;

                    if (!nearRoads.ContainsKey(key)) { nearRoads[key] = new HashSet<int>(); sectorKeys.Add(key); }
                    for (int i = 0; i < boundaries.Count; i++)
                        if (Math.Abs(boundaries[i].SignedDistance(px, py)) < boundaries[i].Half + 15)
                            nearRoads[key].Add(i);
                }

            // 3. Give each sector its grid angle
            var sectorByKey = new Dictionary<int, Sector>();
            foreach (int key in sectorKeys)
            {
                var sector = new Sector
                {
                    Id = world.Sectors.Count,
                    Key = key,
                    Angle = PickSectorAngle(s, rng, nearRoads[key].Select(i => boundaries[i]).ToList())
                };
                world.Sectors.Add(sector);
                sectorByKey[key] = sector;
            }

            // 4. Size each sector's grid to just the sector. One pass over the map
            //    finds how far each sector's tiles reach along its own turned axes.
            int count = world.Sectors.Count;
            var minU = Enumerable.Repeat(double.MaxValue, count).ToArray();
            var minV = Enumerable.Repeat(double.MaxValue, count).ToArray();
            var maxU = Enumerable.Repeat(double.MinValue, count).ToArray();
            var maxV = Enumerable.Repeat(double.MinValue, count).ToArray();
            var cosA = world.Sectors.Select(sc => Math.Cos(sc.Angle * Math.PI / 180)).ToArray();
            var sinA = world.Sectors.Select(sc => Math.Sin(sc.Angle * Math.PI / 180)).ToArray();

            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                {
                    if (!inReach[x, y]) continue;
                    int id = sectorByKey[keyOf[x, y]].Id;
                    double cx = x + 0.5, cy = y + 0.5;
                    double u = cx * cosA[id] + cy * sinA[id];          // position along the sector's turned axes
                    double v = -cx * sinA[id] + cy * cosA[id];
                    minU[id] = Math.Min(minU[id], u); maxU[id] = Math.Max(maxU[id], u);
                    minV[id] = Math.Min(minV[id], v); maxV[id] = Math.Max(maxV[id], v);
                }

            // Margin round the sector: room for edge blocks, the roads round them
            // and stubs, and so blocks touching the grid's edge are never ones we need
            int pad = 2 * s.MaxBlockSizeEdge + 2 * Math.Max(s.MajorRoadWidth, s.MainRoadWidth) + s.StubLength + 2;
            foreach (Sector sector in world.Sectors)
            {
                int id = sector.Id;
                var (ox, oy) = new Frame(0, 0, sector.Angle).ToWorld(minU[id] - pad, minV[id] - pad);
                sector.Frame = new Frame(ox, oy, sector.Angle);
                sector.LocalWidth = (int)Math.Ceiling(maxU[id] - minU[id]) + 2 * pad + 1;
                sector.LocalHeight = (int)Math.Ceiling(maxV[id] - minV[id]) + 2 * pad + 1;
            }

            // 5. Lay each sector out on its own grid
            var locals = new List<CityMap>();
            foreach (Sector sector in world.Sectors)
                locals.Add(GenerateSector(world, s, rng, sector, boundaries));

            // 6. Copy every sector onto the map in a single pass
            CopySectorsToWorld(world, locals, keyOf, sectorByKey, boundaries);

            // 7. Draw the boundary roads over the top, with pavements and stubs
            DrawBoundaryRoads(world, s, boundaries);

            // 8. Corners left between a turned grid and a boundary road become
            //    small green spaces instead of empty ground
            FillBoundaryGaps(world, s, boundaries);

            // 9. Mark the blocked areas (nothing is ever built in them)
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    if (world.Blocked[x, y])
                    {
                        world[x, y] = Cell.Countryside;
                        world[x, y].Type = TileType.Blocked;
                    }

            return world;
        }

        static List<BoundaryRoad> LayOutBoundaryRoads(CityMap world, CitySettings s, Random rng)
        {
            var list = new List<BoundaryRoad>();
            CityShape shape = world.Shape;

            if (s.StraightMainRoad)
            {
                // Along the map's long side, through (about) the centre
                double degrees = world.Width >= world.Height ? 0 : 90;
                double nudge = rng.Next(-2, 3);
                double px = shape.CentreX + (degrees == 90 ? nudge : 0);
                double py = shape.CentreY + (degrees == 0 ? nudge : 0);
                AddBoundaryRoad(world, s, list, px, py, degrees, s.MainRoadWidth, main: true);
            }

            double cityRadius = Math.Min(shape.RadiusX, shape.RadiusY);
            for (int i = 0; i < s.DiagonalAvenues; i++)
            {
                // Try a few random placements until one is well clear of the roads
                // already placed; give up on this avenue if none is
                const int attempts = 40;
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    // Alternate leaning one way then the other, so two avenues form an X
                    double lean = s.AvenueMinAngle + rng.NextDouble() * (s.AvenueMaxAngle - s.AvenueMinAngle);
                    double degrees = i % 2 == 0 ? lean : 180 - lean;
                    double a = degrees * Math.PI / 180;
                    double dx = Math.Cos(a), dy = Math.Sin(a);

                    // Pass a little to one side of the centre so they don't all meet
                    // at one point. Later attempts may pass further out, so a second
                    // avenue leaning the same way can find room beside the first.
                    double spread = Math.Min(0.7, s.AvenueOffset + attempt * 0.015);
                    double offset = (rng.NextDouble() * 2 - 1) * spread * cityRadius;
                    double px = shape.CentreX - dy * offset, py = shape.CentreY + dx * offset;
                    if (shape.EdgeDistance(px, py) >= 0.9) continue;
                    if (RunsAlongside(world, s, list, px, py, degrees)) continue;

                    AddBoundaryRoad(world, s, list, px, py, degrees, s.MajorRoadWidth, main: false);
                    break;
                }
            }
            return list;
        }

        // True if a new road through (px, py) at this angle would run alongside a
        // road already placed: within MinAvenueAngleGap of its direction and closer
        // than MinAvenueSpacing to it anywhere inside the city. (Roads crossing
        // at a clear angle are fine - they just meet at a junction.)
        static bool RunsAlongside(CityMap world, CitySettings s, List<BoundaryRoad> existing,
                                  double px, double py, double degrees)
        {
            double a = degrees * Math.PI / 180;
            double dx = Math.Cos(a), dy = Math.Sin(a);
            double reach = Math.Max(world.Width, world.Height);

            foreach (BoundaryRoad b in existing)
            {
                double diff = Math.Abs(degrees - b.Degrees) % 180;
                diff = Math.Min(diff, 180 - diff);
                if (diff >= s.MinAvenueAngleGap) continue;

                // Measure both ways (along the new road to the old one, and along
                // the old road to the new one): when they aren't quite parallel the
                // two give slightly different answers
                for (double t = -reach; t <= reach; t += 1)
                {
                    double x = px + dx * t, y = py + dy * t;
                    if (world.Shape.EdgeDistance(x, y) <= 1 && Math.Abs(b.SignedDistance(x, y)) < s.MinAvenueSpacing)
                        return true;

                    double bx = b.PX + b.DX * t, by = b.PY + b.DY * t;
                    double toNew = (bx - px) * -dy + (by - py) * dx;
                    if (world.Shape.EdgeDistance(bx, by) <= 1 && Math.Abs(toNew) < s.MinAvenueSpacing)
                        return true;
                }
            }
            return false;
        }

        static void AddBoundaryRoad(CityMap world, CitySettings s, List<BoundaryRoad> list,
                                    double px, double py, double degrees, int width, bool main)
        {
            double a = degrees * Math.PI / 180;
            double dx = Math.Cos(a), dy = Math.Sin(a);

            // Run both ways until leaving the city, then a stub's length further
            var (ax, ay, aOut) = WalkToEdge(world, px, py, -dx, -dy, s.StubLength + 1);
            var (bx, by, bOut) = WalkToEdge(world, px, py, dx, dy, s.StubLength + 1);
            double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (length < 20) return;

            // Split it wherever a blocked area is in the way (keeping the road and
            // its pavement clear of it); drop pieces too short to be worth a road
            double clearance = width / 2.0 + s.SidewalkWidth + s.ObstacleClearance;
            var runs = new List<(double T0, double T1)>();
            double? runStart = null;
            for (double t = 0; t <= length; t += 0.5)
            {
                bool clear = !NearBlocked(world, ax + dx * t, ay + dy * t, clearance);
                if (clear && runStart == null) runStart = t;
                if (!clear && runStart != null) { runs.Add((runStart.Value, t - 0.5)); runStart = null; }
            }
            if (runStart != null) runs.Add((runStart.Value, length));

            var boundary = new BoundaryRoad
            {
                PX = px,
                PY = py,
                DX = dx,
                DY = dy,
                Degrees = degrees,
                Half = width / 2.0,
                Main = main
            };
            foreach (var (t0, t1) in runs)
            {
                if (t1 - t0 < 8) continue;
                var road = new SignificantRoad { Kind = main ? "Main" : "Avenue", Width = width, SidewalkWidth = s.SidewalkWidth };
                road.Points.Add((ax + dx * t0, ay + dy * t0));
                road.Points.Add((ax + dx * t1, ay + dy * t1));
                bool atStart = t0 <= 0.001, atEnd = t1 >= length - 0.001;
                boundary.Pieces.Add(new BoundaryPiece
                {
                    Road = road,
                    StubAtStart = atStart && aOut,
                    BlockedAtStart = !atStart,
                    StubAtEnd = atEnd && bOut,
                    BlockedAtEnd = !atEnd
                });
            }
            if (boundary.Pieces.Count > 0) list.Add(boundary);
        }

        // True if any blocked tile lies within radius of the point
        static bool NearBlocked(CityMap world, double x, double y, double radius)
        {
            int x0 = Math.Max(0, (int)Math.Floor(x - radius)), x1 = Math.Min(world.Width - 1, (int)Math.Ceiling(x + radius));
            int y0 = Math.Max(0, (int)Math.Floor(y - radius)), y1 = Math.Min(world.Height - 1, (int)Math.Ceiling(y + radius));
            for (int tx = x0; tx <= x1; tx++)
                for (int ty = y0; ty <= y1; ty++)
                {
                    if (!world.Blocked[tx, ty]) continue;
                    double ddx = tx + 0.5 - x, ddy = ty + 0.5 - y;
                    if (ddx * ddx + ddy * ddy <= radius * radius) return true;
                }
            return false;
        }

        // Builds the blocked-area masks from the settings' obstacles, the
        // IsBlocked callback and (if on) a random test obstacle
        static void BuildBlockedMask(CityMap world, CitySettings s, Random rng)
        {
            world.Obstacles.AddRange(s.Obstacles);
            if (s.TestObstacle)
            {
                CityShape shape = world.Shape;
                double cityRadius = Math.Min(shape.RadiusX, shape.RadiusY);
                var (cx, cy) = shape.PointAt(rng.NextDouble() * Math.PI * 2, s.TestObstacleDistance);
                world.Obstacles.Add(new Obstacle
                {
                    Shape = rng.Next(2) == 0 ? ObstacleShape.Line : ObstacleShape.Ellipse,
                    CentreX = cx,
                    CentreY = cy,
                    Length = cityRadius * Lerp(s.TestObstacleMinLength, s.TestObstacleMaxLength, rng.NextDouble()),
                    Thickness = Lerp(s.TestObstacleMinThickness, s.TestObstacleMaxThickness, rng.NextDouble()),
                    AngleDegrees = rng.NextDouble() * 180
                });
            }

            int w = world.Width, h = world.Height;
            world.Blocked = new bool[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    world.Blocked[x, y] = (s.IsBlocked?.Invoke(x, y) ?? false) ||
                                          world.Obstacles.Any(o => o.Contains(x + 0.5, y + 0.5));

            int grow = s.ObstacleClearance + 1;
            var near = new bool[w, h];
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    if (!world.Blocked[x, y]) continue;
                    for (int gx = Math.Max(0, x - grow); gx <= Math.Min(w - 1, x + grow); gx++)
                        for (int gy = Math.Max(0, y - grow); gy <= Math.Min(h - 1, y + grow); gy++)
                            near[gx, gy] = true;
                }
            world.BlockedNear = near;
            world.BlockedAt = (x, y) =>
            {
                int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
                return ix >= 0 && iy >= 0 && ix < w && iy < h && near[ix, iy];
            };
        }

        // A sector's grid usually lines up with one of the roads running along it
        // (as real grids line up with the avenue they grew from); sometimes it's
        // square to the map, and sometimes at its own angle.
        static double PickSectorAngle(CitySettings s, Random rng, List<BoundaryRoad> alongside)
        {
            if (rng.NextDouble() < s.SectorFreeAngleChance)
                return rng.NextDouble() * 90;
            var options = alongside.Select(b => b.Degrees % 90).ToList();
            options.Add(0);
            return options[rng.Next(options.Count)];
        }

        // Lays out one sector with the usual block / district / building rules,
        // on a grid turned to the sector's angle. Only blocks lying wholly inside
        // the sector, clear of the boundary roads and their pavements, are used,
        // so nothing is ever cut by a boundary road.
        static CityMap GenerateSector(CityMap world, CitySettings s, Random rng, Sector sector, List<BoundaryRoad> boundaries)
        {
            var map = new CityMap(sector.LocalWidth, sector.LocalHeight, world.Seed, world.Shape)
            {
                HeightField = world.HeightField,
                DistrictField = world.DistrictField,
                IndustryField = world.IndustryField,
                Frame = sector.Frame,
                Allowed = (x, y) => SideKey(boundaries, x, y) == sector.Key &&
                                    boundaries.All(b => Math.Abs(b.SignedDistance(x, y)) > b.Half + s.SidewalkWidth),
                BoundaryGap = (x, y) => boundaries.Count == 0
                    ? double.MaxValue
                    : boundaries.Min(b => Math.Abs(b.SignedDistance(x, y)) - b.Half),
                BlockedAt = world.BlockedAt,
                BlockMargin = s.MajorRoadWidth + s.SidewalkWidth,
                Unusable = r =>
                {
                    // The rectangle's corners on the map
                    var corners = new[] { (r.X, r.Y), (r.Right, r.Y), (r.X, r.Bottom), (r.Right, r.Bottom) }
                        .Select(p => sector.Frame.ToWorld(p.Item1, p.Item2)).ToArray();

                    // Entirely on the wrong side of one boundary road, or within its
                    // road and pavement (straight line, so the corners decide it)
                    for (int i = 0; i < boundaries.Count; i++)
                    {
                        BoundaryRoad road = boundaries[i];
                        double towardSector = ((sector.Key >> i) & 1) == 1 ? 1 : -1;
                        double clear = road.Half + s.SidewalkWidth;
                        if (corners.All(c => towardSector * road.SignedDistance(c.X, c.Y) <= clear))
                            return true;
                    }

                    // Entirely further from the city centre than the outline's biggest
                    // bulge, plus how far edge blocks / houses along roads out of town reach
                    var (cu, cv) = sector.Frame.ToLocal(world.Shape.CentreX, world.Shape.CentreY);
                    double du = Math.Max(0, Math.Max(r.X - cu, cu - r.Right));
                    double dv = Math.Max(0, Math.Max(r.Y - cv, cv - r.Bottom));
                    double nearest = Math.Sqrt(du * du + dv * dv);
                    double furthest = Math.Max(world.Shape.RadiusX, world.Shape.RadiusY) * world.Shape.MaxWobble
                                      * (1 + Math.Max(s.EdgeJitter, s.RibbonReach)) * 1.05;   // 5% safety margin
                    return nearest > furthest;
                }
            };

            // 1. Lay out a full road grid over the whole local map
            SplitBlock(map, s, rng, new Rect(0, 0, map.Width, map.Height), depth: 1);

            // 2. Choose the sector's blocks, give each a district and plan its plots.
            //    Blocks just outside the outline along a major road may get
            //    ribbon development: a line of houses facing that road.
            var plans = new List<BlockPlan>();
            var ribbonCandidates = new List<(Rect Block, Compass Side)>();
            foreach (Rect b in map.Blocks)
            {
                if (TouchesMapEdge(map, b) || !map.FitsBlock(b)) continue;
                double edge = map.EdgeAt(b.CentreX, b.CentreY);
                double limit = 1 + (rng.NextDouble() * 2 - 1) * s.EdgeJitter;

                if (edge < limit)
                {
                    BlockPlan plan = PlanBlock(map, s, rng, b);
                    if (plan.BuildsSomething) plans.Add(plan);
                }
                else if (s.RibbonReach > 0 && edge < 1 + s.RibbonReach && MajorRoadSide(map, b) is Compass side)
                {
                    ribbonCandidates.Add((b, side));
                }
            }
            AddRibbonDevelopment(map, s, rng, plans, ribbonCandidates);

            // 3. Keep only whole road segments that border a city block (full width,
            //    junction to junction). Major roads also bridge gaps between lobes.
            var keep = new bool[map.Width, map.Height];
            foreach (BlockPlan plan in plans)
            {
                if (plan.Ribbon) KeepRoadOnSide(map, plan.Block, plan.RibbonSide, keep);
                else KeepRoadsAround(map, plan.Block, keep);
            }
            FillMajorRoadGaps(map, keep);

            for (int x = 0; x < map.Width; x++)
                for (int y = 0; y < map.Height; y++)
                    if (map[x, y].Type == TileType.Road && !keep[x, y])
                        map[x, y] = Cell.Countryside;

            // 3b. Join the sector's streets to the main road / avenues along its
            //     edges, then remove any road network still cut off from the city
            ConnectToBoundaries(map, s, boundaries);
            RemoveDisconnectedRoads(map, s);

            plans = plans.Where(p => HasRoadBeside(map, p.Block)).ToList();
            foreach (BlockPlan plan in plans)
            {
                map.CityBlocks.Add(plan.Block);
                map.BlockDistricts[plan.Block] = plan.District;
            }

            // 4. Merge neighbouring blocks into large plots and multi-block parks.
            //    Only the minor road between two merged blocks is removed, one road
            //    per added block, so the road network stays connected.
            var merged = new HashSet<Rect>();
            ChooseLargePlots(map, s, rng, plans, merged);
            ChooseParks(map, s, rng, plans, merged);

            // 5. Short stubs where major roads would have carried on out of the city
            AddStubs(map, s);

            // 6. Fill everything in
            foreach (BlockPlan plan in plans)
                if (!merged.Contains(plan.Block))
                    BuildBlock(map, s, rng, plan);
            foreach (LargePlot plot in map.LargePlots)
                BuildLargePlot(map, s, rng, plot);
            foreach (Park park in map.Parks)
                BuildPark(map, s, park);

            // 7. Any other block now fully ringed by road (a hole in the city)
            //    becomes a small park instead of a patch of countryside
            var cityBlocks = new HashSet<Rect>(map.CityBlocks);
            foreach (Rect b in map.Blocks)
            {
                if (cityBlocks.Contains(b) || TouchesMapEdge(map, b) || !map.FitsBlock(b) || !RoadOnAllSides(map, b)) continue;
                var pocket = new Park();
                pocket.Blocks.Add(b);
                map.Parks.Add(pocket);
                BuildPark(map, s, pocket);
            }

            return map;
        }

        // Extends some of the sector's streets straight on until they meet the
        // main road or an avenue, so the sector is joined to them regularly
        // (about every ConnectorSpacing tiles along each one), not just where a
        // street happens to touch. A street only ever extends along its own
        // original line, which never crosses a block, so nothing built is affected.
        static void ConnectToBoundaries(CityMap map, CitySettings s, List<BoundaryRoad> boundaries)
        {
            if (boundaries.Count == 0) return;
            var candidates = new List<(RoadSegment Road, int From, int To, int Boundary, double Along)>();

            foreach (RoadSegment road in map.Roads)
            {
                var (lo, hi) = KeptSpan(road, (x, y) => map[x, y].Type == TileType.Road);
                if (lo < 0) continue;
                int start = road.Horizontal ? road.Area.X : road.Area.Y;
                int end = (road.Horizontal ? road.Area.Right : road.Area.Bottom) - 1;

                foreach (int dir in new[] { +1, -1 })
                {
                    int from = dir > 0 ? hi + 1 : lo - 1;
                    for (int p = from, steps = 0; p >= start && p <= end && steps < s.MaxConnectorLength; p += dir, steps++)
                    {
                        var (cx, cy) = CentreTile(road, p);
                        var (wx, wy) = map.ToWorld(cx + 0.5, cy + 0.5);
                        if (AnyBlocked(map, SubRect(road, p, p))) break;    // a blocked area is in the way

                        int hit = boundaries.FindIndex(b => Math.Abs(b.SignedDistance(wx, wy)) <= b.Half);
                        if (hit >= 0)
                        {
                            BoundaryRoad met = boundaries[hit];
                            double along = (wx - met.PX) * met.DX + (wy - met.PY) * met.DY;
                            candidates.Add((road, Math.Min(from, p), Math.Max(from, p), hit, along));
                            break;
                        }
                        if (map.Shape.EdgeDistance(wx, wy) > 1) break;       // would leave the city first
                    }
                }
            }

            // Along each boundary road, keep connections at least ConnectorSpacing
            // apart (major streets always connect)
            foreach (var group in candidates.GroupBy(c => c.Boundary))
            {
                double last = double.MinValue;
                foreach (var c in group.OrderBy(c => c.Along))
                {
                    if (!c.Road.Major && c.Along - last < s.ConnectorSpacing) continue;
                    DrawRoad(map, SubRect(c.Road, c.From, c.To), c.Road.Horizontal, c.Road.Major);
                    last = c.Along;
                }
            }
        }

        // Copies every sector's local layout onto the map in one pass: each map
        // tile looks up its own sector, then the local tile under its centre
        static void CopySectorsToWorld(CityMap world, List<CityMap> locals, int[,] keyOf,
                                       Dictionary<int, Sector> sectorByKey, List<BoundaryRoad> boundaries)
        {
            var buildings = world.Sectors.Select(_ => new Dictionary<int, Building>()).ToArray();

            for (int x = 0; x < world.Width; x++)
                for (int y = 0; y < world.Height; y++)
                {
                    if (world.Blocked[x, y] || !sectorByKey.TryGetValue(keyOf[x, y], out Sector? sector)) continue;
                    CityMap local = locals[sector.Id];
                    var (u, v) = sector.Frame.ToLocal(x + 0.5, y + 0.5);
                    int lx = (int)Math.Floor(u), ly = (int)Math.Floor(v);
                    if (!local.InBounds(lx, ly)) continue;

                    Cell lc = local[lx, ly];
                    if (lc.Type == TileType.Empty) continue;

                    ref Cell c = ref world[x, y];
                    c = lc;
                    c.BuildingId = -1;

                    // Roads on a turned grid keep their direction as an angle
                    if (lc.Type == TileType.Road && sector.Angle != 0)
                    {
                        bool vertical = (lc.Road & RoadFlags.Vertical) != 0;
                        c.Road = (lc.Road & ~(RoadFlags.Horizontal | RoadFlags.Vertical)) | RoadFlags.Angled;
                        c.RoadAngle = (short)Math.Round(((vertical ? 90 : 0) + sector.Angle) % 180);
                    }

                    if (lc.BuildingId >= 0)
                    {
                        var sectorBuildings = buildings[sector.Id];
                        if (!sectorBuildings.TryGetValue(lc.BuildingId, out Building? b))
                        {
                            Building lb = local.Buildings[lc.BuildingId];
                            b = new Building
                            {
                                Id = world.Buildings.Count,
                                Shape = lb.Shape,
                                Kind = lb.Kind,
                                District = lb.District,
                                Lot = lb.Lot,
                                Sector = sector.Id,
                                LocalTiles = lb.Tiles
                            };
                            world.Buildings.Add(b);
                            sectorBuildings[lc.BuildingId] = b;
                        }
                        c.BuildingId = b.Id;
                        b.Tiles.Add(new BuildingTile(x, y, lc.Floors));
                        b.MaxFloors = Math.Max(b.MaxFloors, lc.Floors);
                    }
                }

            // Then each sector's stubs, areas, districts and exact geometry
            foreach (Sector sector in world.Sectors)
                CopySectorExtras(world, sector, locals[sector.Id], boundaries);
        }

        // A sector's stubs, areas, districts and exact geometry (its tiles are
        // copied by CopySectorsToWorld)
        static void CopySectorExtras(CityMap world, Sector sector, CityMap local, List<BoundaryRoad> boundaries)
        {
            // Stubs: move their end points onto the map and turn their headings
            foreach (RoadStub st in local.Stubs)
            {
                var (wx, wy) = sector.Frame.ToWorld(st.X + 0.5, st.Y + 0.5);
                int ix = (int)Math.Floor(wx), iy = (int)Math.Floor(wy);
                if (!world.InBounds(ix, iy) || world[ix, iy].Type != TileType.Road) continue;
                world[ix, iy].Road |= RoadFlags.Stub | RoadFlags.StubEnd;

                double heading = ((st.Angle + sector.Angle) % 360 + 360) % 360;
                var corners = new[] { (st.Area.X, st.Area.Y), (st.Area.Right, st.Area.Y), (st.Area.X, st.Area.Bottom), (st.Area.Right, st.Area.Bottom) }
                    .Select(p => sector.Frame.ToWorld(p.Item1, p.Item2)).ToList();
                int x0 = (int)Math.Floor(corners.Min(p => p.X)), y0 = (int)Math.Floor(corners.Min(p => p.Y));
                int x1 = (int)Math.Ceiling(corners.Max(p => p.X)), y1 = (int)Math.Ceiling(corners.Max(p => p.Y));
                var (exactX, exactY) = sector.Frame.ToWorld(st.ExactX, st.ExactY);
                world.Stubs.Add(new RoadStub(ix, iy, CompassOf(heading), st.Major, st.Width,
                                             new Rect(x0, y0, x1 - x0, y1 - y0), heading, false, exactX, exactY));
            }

            // Areas and districts, tagged with their sector (their rects are in
            // the sector's local grid; Sector.Frame turns them into map positions)
            foreach (Park p in local.Parks) { p.Sector = sector.Id; world.Parks.Add(p); }
            foreach (LargePlot p in local.LargePlots) { p.Sector = sector.Id; world.LargePlots.Add(p); }
            foreach (var (block, district) in local.BlockDistricts)
                world.SectorBlocks.Add((sector.Id, block, district));
            world.CityBlocks.AddRange(local.CityBlocks);

            // Exact geometry for 3D: streets and ground surfaces as rectangles on
            // the sector's own grid (so they come out crisp, not stepped)
            ExtractStreets(world, sector, local, boundaries);
            ExtractSurfaces(world, sector, local);
        }

        // Each street (as originally laid out) split into the stretches that
        // were kept, as exact rectangles. Junction squares belong to the road that
        // runs through them, so pieces never overlap.
        static void ExtractStreets(CityMap world, Sector sector, CityMap local, List<BoundaryRoad> boundaries)
        {
            foreach (RoadSegment road in local.Roads)
            {
                int start = road.Horizontal ? road.Area.X : road.Area.Y;
                int end = road.Horizontal ? road.Area.Right : road.Area.Bottom;
                int runStart = -1;
                bool runStub = false;
                for (int p = start; p <= end; p++)
                {
                    bool isRoad = false, isStub = false;
                    if (p < end)
                    {
                        var (cx, cy) = CentreTile(road, p);
                        isRoad = local[cx, cy].Type == TileType.Road;
                        isStub = (local[cx, cy].Road & RoadFlags.Stub) != 0;
                    }
                    if (runStart >= 0 && (!isRoad || isStub != runStub))
                    {
                        AddStreetPiece(world, sector, road, SubRect(road, runStart, p - 1), runStub, boundaries);
                        runStart = -1;
                    }
                    if (isRoad && runStart < 0) { runStart = p; runStub = isStub; }
                }
            }
        }

        static void AddStreetPiece(CityMap world, Sector sector, RoadSegment road, Rect area, bool stub, List<BoundaryRoad> boundaries)
        {
            var outline = new List<(double X, double Y)>
        {
            sector.Frame.ToWorld(area.X, area.Y),
            sector.Frame.ToWorld(area.Right, area.Y),
            sector.Frame.ToWorld(area.Right, area.Bottom),
            sector.Frame.ToWorld(area.X, area.Bottom),
        };

            // Keep it on its own side of each boundary road (where a street meets
            // an avenue it can reach a little into it, which is fine under the
            // avenue's surface, but it must not poke out the far side)
            for (int i = 0; i < boundaries.Count && outline.Count >= 3; i++)
            {
                BoundaryRoad b = boundaries[i];
                double sign = ((sector.Key >> i) & 1) == 1 ? 1 : -1;
                outline = ClipToHalfPlane(outline, (x, y) => sign * b.SignedDistance(x, y));
            }
            if (outline.Count < 3) return;

            world.Streets.Add(new StreetPiece
            {
                Sector = sector.Id,
                Area = area,
                AlongU = road.Horizontal,
                Major = road.Major,
                Stub = stub,
                Outline = outline
            });
        }

        // Sutherland-Hodgman: keeps the part of the polygon where side(x, y) >= 0
        static List<(double X, double Y)> ClipToHalfPlane(List<(double X, double Y)> poly, Func<double, double, double> side)
        {
            var result = new List<(double X, double Y)>();
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                double da = side(a.X, a.Y), db = side(b.X, b.Y);
                if (da >= 0) result.Add(a);
                if ((da >= 0) != (db >= 0))
                {
                    double t = da / (da - db);
                    result.Add((a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                }
            }
            return result;
        }

        // Ground surfaces (pavement, yards, gardens, parks, car parks) merged into
        // as few rectangles as possible on the sector's grid
        static void ExtractSurfaces(CityMap world, Sector sector, CityMap local)
        {
            var used = new bool[local.Width, local.Height];
            for (int y = 0; y < local.Height; y++)
                for (int x = 0; x < local.Width; x++)
                {
                    TileType type = local[x, y].Type;
                    if (used[x, y] || type is not (TileType.Sidewalk or TileType.Ground or TileType.Garden
                                                   or TileType.Park or TileType.CarPark)) continue;

                    int x2 = x;
                    while (x2 + 1 < local.Width && !used[x2 + 1, y] && local[x2 + 1, y].Type == type) x2++;

                    int y2 = y;
                    while (y2 + 1 < local.Height)
                    {
                        bool rowMatches = true;
                        for (int rx = x; rx <= x2 && rowMatches; rx++)
                            rowMatches = !used[rx, y2 + 1] && local[rx, y2 + 1].Type == type;
                        if (!rowMatches) break;
                        y2++;
                    }

                    for (int ux = x; ux <= x2; ux++)
                        for (int uy = y; uy <= y2; uy++)
                            used[ux, uy] = true;
                    world.Surfaces.Add(new SurfacePatch
                    {
                        Sector = sector.Id,
                        Type = type,
                        Area = new Rect(x, y, x2 - x + 1, y2 - y + 1)
                    });
                }
        }

        static Compass CompassOf(double headingDegrees) => ((headingDegrees % 360 + 360) % 360) switch
        {
            < 45 or >= 315 => Compass.East,
            < 135 => Compass.South,
            < 225 => Compass.West,
            _ => Compass.North
        };

        static void DrawBoundaryRoads(CityMap world, CitySettings s, List<BoundaryRoad> boundaries)
        {
            var drawn = new bool[world.Width, world.Height];
            foreach (BoundaryRoad b in boundaries)
                foreach (BoundaryPiece piece in b.Pieces)
                {
                    world.SignificantRoads.Add(piece.Road);
                    var tiles = RasteriseLine(world, piece.Road.Points, b.Half + s.SidewalkWidth, flatEnds: true);

                    foreach (var t in tiles)
                    {
                        if (t.Distance > b.Half) continue;
                        ref Cell c = ref world[t.X, t.Y];
                        if (drawn[t.X, t.Y])
                        {
                            c.Road |= RoadFlags.Angled;        // crossing another boundary road
                            continue;
                        }
                        drawn[t.X, t.Y] = true;
                        c.Type = TileType.Road;
                        c.Road = RoadFlags.Angled | RoadFlags.Major | (b.Main ? RoadFlags.Main : RoadFlags.None);
                        if (t.Distance <= 0.5) c.Road |= RoadFlags.CentreLine;
                        c.RoadAngle = (short)Math.Round(t.Angle);
                        c.Floors = 0;
                        c.BuildingId = -1;
                    }

                    // Pavement along both sides through the city. Where a sector's
                    // street runs into it, leave the junction open.
                    foreach (var t in tiles)
                    {
                        if (t.Distance <= b.Half || drawn[t.X, t.Y]) continue;
                        ref Cell c = ref world[t.X, t.Y];
                        if (c.Type == TileType.Road) continue;
                        if (c.Type != TileType.Empty || world.Shape.EdgeDistance(t.X + 0.5, t.Y + 0.5) <= 1)
                        {
                            c.Type = TileType.Sidewalk;
                            c.Floors = 0;
                            c.BuildingId = -1;
                        }
                    }
                }

            foreach (BoundaryRoad b in boundaries)
                foreach (BoundaryPiece piece in b.Pieces)
                {
                    if (piece.StubAtStart || piece.BlockedAtStart)
                        AddBoundaryStub(world, s, piece.Road, 0, b.Degrees + 180, atObstacle: piece.BlockedAtStart);
                    if (piece.StubAtEnd || piece.BlockedAtEnd)
                        AddBoundaryStub(world, s, piece.Road, 1, b.Degrees, atObstacle: piece.BlockedAtEnd);
                }
        }

        static void FillBoundaryGaps(CityMap world, CitySettings s, List<BoundaryRoad> boundaries)
        {
            if (boundaries.Count == 0) return;
            for (int x = 0; x < world.Width; x++)
                for (int y = 0; y < world.Height; y++)
                {
                    if (world[x, y].Type != TileType.Empty || world.BlockedNear[x, y]) continue;
                    double px = x + 0.5, py = y + 0.5;
                    if (world.Shape.EdgeDistance(px, py) > 0.95) continue;
                    double gap = boundaries.Min(b => Math.Abs(b.SignedDistance(px, py)) - b.Half);
                    if (gap <= s.SidewalkWidth + 8)
                        world[x, y].Type = TileType.Park;
                }

            int[] stepX = { 1, -1, 0, 0 }, stepY = { 0, 0, 1, -1 };
            for (int x = 0; x < world.Width; x++)
                for (int y = 0; y < world.Height; y++)
                {
                    if (world[x, y].Type != TileType.Park) continue;
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = x + stepX[i], ny = y + stepY[i];
                        if (world.InBounds(nx, ny) && world[nx, ny].Type == TileType.Road)
                        {
                            world[x, y].Type = TileType.Sidewalk;
                            break;
                        }
                    }
                }
        }

        // Walks from (x, y) along (dx, dy) until leaving the city, then `extra`
        // tiles further. Stops early at the map edge (and then reports no stub).
        static (double X, double Y, bool LeftCity) WalkToEdge(CityMap map, double x, double y, double dx, double dy, int extra)
        {
            double lastX = x, lastY = y;
            for (double t = 0; ; t += 0.5)
            {
                double cx = x + dx * t, cy = y + dy * t;
                if (cx < 1 || cy < 1 || cx > map.Width - 2 || cy > map.Height - 2)
                    return (lastX, lastY, false);
                lastX = cx; lastY = cy;
                if (map.EdgeAt(cx, cy) > 1)
                {
                    for (double u = 0.5; u <= extra; u += 0.5)
                    {
                        double sx = cx + dx * u, sy = cy + dy * u;
                        if (sx < 1 || sy < 1 || sx > map.Width - 2 || sy > map.Height - 2)
                            return (lastX, lastY, false);
                        lastX = sx; lastY = sy;
                    }
                    return (lastX, lastY, true);
                }
            }
        }

        // Records a stub at one end of a boundary road piece (0 = first point,
        // 1 = last): either where it leaves the city (the part outside is
        // flagged Stub) or where a blocked area cuts it short (AtObstacle).
        static void AddBoundaryStub(CityMap map, CitySettings s, SignificantRoad road, int end, double headingDegrees, bool atObstacle)
        {
            var (ex, ey) = end == 0 ? road.Points[0] : road.Points[^1];
            double reach = (atObstacle ? 1 : s.StubLength) + road.Width;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            int endX = -1, endY = -1;
            double bestToEnd = double.MaxValue;

            foreach (var t in RasteriseLine(map, road.Points, road.Width / 2.0, flatEnds: true))
            {
                if (map[t.X, t.Y].Type != TileType.Road) continue;
                double ddx = t.X + 0.5 - ex, ddy = t.Y + 0.5 - ey;
                double toEnd = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (toEnd > reach) continue;                                  // only this end
                if (!atObstacle && map.EdgeAt(t.X + 0.5, t.Y + 0.5) <= 1) continue;

                map[t.X, t.Y].Road |= RoadFlags.Stub;
                x0 = Math.Min(x0, t.X); y0 = Math.Min(y0, t.Y);
                x1 = Math.Max(x1, t.X); y1 = Math.Max(y1, t.Y);
                if (t.Distance <= 0.5 && toEnd < bestToEnd) { bestToEnd = toEnd; endX = t.X; endY = t.Y; }
            }
            if (endX < 0) return;

            map[endX, endY].Road |= RoadFlags.StubEnd;
            double heading = (headingDegrees % 360 + 360) % 360;
            map.Stubs.Add(new RoadStub(endX, endY, CompassOf(heading), true, road.Width,
                                       new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), heading, atObstacle, ex, ey));
        }

        // Every tile within reach of a polyline, with the distance from the tile
        // centre to the line and the line's direction there (degrees, 0-180,
        // 0 = left-right, 90 = up-down)
        static List<(int X, int Y, double Distance, double Angle)> RasteriseLine(
            CityMap map, List<(double X, double Y)> points, double reach, bool flatEnds = false)
        {
            var best = new Dictionary<(int, int), (double Distance, double Angle)>();
            for (int i = 1; i < points.Count; i++)
            {
                var (ax, ay) = points[i - 1];
                var (bx, by) = points[i];
                double angle = Math.Atan2(by - ay, bx - ax) * 180 / Math.PI;
                angle = (angle % 180 + 180) % 180;

                int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, bx) - reach - 1));
                int maxX = Math.Min(map.Width - 1, (int)Math.Ceiling(Math.Max(ax, bx) + reach + 1));
                int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, by) - reach - 1));
                int maxY = Math.Min(map.Height - 1, (int)Math.Ceiling(Math.Max(ay, by) + reach + 1));

                for (int x = minX; x <= maxX; x++)
                    for (int y = minY; y <= maxY; y++)
                    {
                        if (flatEnds)
                        {
                            // square ends: skip anything beyond either end of the line
                            double sx = bx - ax, sy = by - ay, lengthSq = sx * sx + sy * sy;
                            double along = lengthSq == 0 ? 0 : ((x + 0.5 - ax) * sx + (y + 0.5 - ay) * sy) / lengthSq;
                            if (along < 0 || along > 1) continue;
                        }
                        double d = DistanceToSegment(x + 0.5, y + 0.5, ax, ay, bx, by);
                        if (d > reach) continue;
                        if (!best.TryGetValue((x, y), out var current) || d < current.Distance)
                            best[(x, y)] = (d, angle);
                    }
            }
            return best.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value.Distance, kv.Value.Angle)).ToList();
        }

        static double DistanceToSegment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double lengthSq = dx * dx + dy * dy;
            double t = lengthSq == 0 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lengthSq, 0, 1);
            double cx = ax + t * dx - px, cy = ay + t * dy - py;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        // ---------------- Road layout ----------------

        static void DrawRoad(CityMap map, Rect area, bool horizontal, bool major, RoadFlags extra = RoadFlags.None)
        {
            int roadWidth = horizontal ? area.H : area.W;
            int centre = roadWidth / 2;
            bool hasCentreLine = roadWidth >= 3 && roadWidth % 2 == 1;

            for (int x = area.X; x < area.Right; x++)
                for (int y = area.Y; y < area.Bottom; y++)
                {
                    ref Cell c = ref map[x, y];
                    c.Type = TileType.Road;
                    c.Road |= (horizontal ? RoadFlags.Horizontal : RoadFlags.Vertical) | extra;
                    if (major) c.Road |= RoadFlags.Major;

                    int across = horizontal ? y - area.Y : x - area.X;
                    if (hasCentreLine && across == centre)
                        c.Road |= RoadFlags.CentreLine;
                }
        }

        static int RoadWidthAt(CitySettings s, int depth) =>
            depth <= s.MajorRoadDepth ? s.MajorRoadWidth : s.MinorRoadWidth;

        // Block sizes blend from small in the centre to big on the outskirts
        static (int Min, int Max) BlockSizeAt(CityMap map, CitySettings s, Rect r)
        {
            double t = Math.Clamp(map.EdgeAt(r.CentreX, r.CentreY), 0, 1);
            int min = (int)Math.Round(Lerp(s.MinBlockSizeCentre, s.MinBlockSizeEdge, t));
            int max = (int)Math.Round(Lerp(s.MaxBlockSizeCentre, s.MaxBlockSizeEdge, t));
            return (min, Math.Max(min, max));
        }

        // A valid road position between start and end (leaving room for a block
        // either side), lining the road's centre up with centreHint if it fits.
        // Null if there's no room.
        static int? CutPosition(int start, int end, int minBlock, int roadWidth, int? centreHint, Random rng)
        {
            int lo = start + minBlock, hi = end - minBlock - roadWidth;
            if (hi < lo) return null;
            if (centreHint is int c && c - roadWidth / 2 >= lo && c - roadWidth / 2 <= hi) return c - roadWidth / 2;
            return rng.Next(lo, hi + 1);
        }

        // Picks where the next roads on both sides of a new road should go, as a
        // centre line, so roads of different widths still line up
        static int? SharedCentre(int start, int end, int minBlock, int roadWidth, Random rng) =>
            CutPosition(start, end, minBlock, roadWidth, null, rng) is int p ? p + roadWidth / 2 : null;

        // Recursively cuts a region with roads. alignX / alignY are positions the
        // next vertical / horizontal road should use if it can, so roads on both
        // sides of an existing road line up and form crossroads instead of T's.
        static void SplitBlock(CityMap map, CitySettings s, Random rng, Rect r, int depth,
                               int? alignX = null, int? alignY = null)
        {
            if (map.Unusable(r)) return;
            bool major = depth <= s.MajorRoadDepth;
            int rw = RoadWidthAt(s, depth);
            int childRw = RoadWidthAt(s, depth + 1);
            var (minB, maxB) = BlockSizeAt(map, s, r);
            int minSpan = minB * 2 + rw;                      // room for 2 blocks + a road
            bool canCutX = r.W >= minSpan, canCutY = r.H >= minSpan;
            bool tooBig = r.W > maxB || r.H > maxB;

            if (!tooBig || (!canCutX && !canCutY))
            {
                map.Blocks.Add(r);
                return;
            }

            // Cut across the longer side
            bool cutX = canCutX && (!canCutY || r.W > r.H || (r.W == r.H && rng.Next(2) == 0));

            if (cutX) // vertical road
            {
                int pos = CutPosition(r.X, r.Right, minB, rw, alignX, rng) ?? r.X + minB;
                var road = new RoadSegment(new Rect(pos, r.Y, rw, r.H), horizontal: false, major);
                map.Roads.Add(road);
                DrawRoad(map, road.Area, horizontal: false, major);

                int? shareY = alignY ?? (rng.NextDouble() < s.CrossroadChance
                    ? SharedCentre(r.Y, r.Bottom, minB, childRw, rng) : null);
                SplitBlock(map, s, rng, new Rect(r.X, r.Y, pos - r.X, r.H), depth + 1, null, shareY);
                SplitBlock(map, s, rng, new Rect(pos + rw, r.Y, r.Right - pos - rw, r.H), depth + 1, null, shareY);
            }
            else // horizontal road
            {
                int pos = CutPosition(r.Y, r.Bottom, minB, rw, alignY, rng) ?? r.Y + minB;
                var road = new RoadSegment(new Rect(r.X, pos, r.W, rw), horizontal: true, major);
                map.Roads.Add(road);
                DrawRoad(map, road.Area, horizontal: true, major);

                int? shareX = alignX ?? (rng.NextDouble() < s.CrossroadChance
                    ? SharedCentre(r.X, r.Right, minB, childRw, rng) : null);
                SplitBlock(map, s, rng, new Rect(r.X, r.Y, r.W, pos - r.Y), depth + 1, shareX, null);
                SplitBlock(map, s, rng, new Rect(r.X, pos + rw, r.W, r.Bottom - pos - rw), depth + 1, shareX, null);
            }
        }

        // ---------------- Trimming roads to the city ----------------

        static bool TouchesMapEdge(CityMap map, Rect b) =>
            b.X == 0 || b.Y == 0 || b.Right == map.Width || b.Bottom == map.Height;

        // Marks the roads on all four sides of a block, full width, plus the
        // four corner junctions, so the block ends up fully ringed by road.
        // Walks only cross the road running alongside the block (checked by its
        // direction), so they stop at the far kerb instead of carrying on down
        // a road that continues straight ahead at a junction.
        static void KeepRoadsAround(CityMap map, Rect b, bool[,] keep)
        {
            const RoadFlags H = RoadFlags.Horizontal, V = RoadFlags.Vertical;

            for (int x = b.X; x < b.Right; x++)
            {
                MarkRun(map, keep, x, b.Y - 1, 0, -1, H);     // road above
                MarkRun(map, keep, x, b.Bottom, 0, 1, H);     // road below
            }
            for (int y = b.Y; y < b.Bottom; y++)
            {
                MarkRun(map, keep, b.X - 1, y, -1, 0, V);     // road left
                MarkRun(map, keep, b.Right, y, 1, 0, V);      // road right
            }

            // Corner junctions: as wide as the side road, as tall as the top/bottom road
            int left = RunLength(map, b.X - 1, b.Y, -1, 0, V);
            int right = RunLength(map, b.Right, b.Y, 1, 0, V);
            int up = RunLength(map, b.X, b.Y - 1, 0, -1, H);
            int down = RunLength(map, b.X, b.Bottom, 0, 1, H);
            MarkRoadRect(map, keep, new Rect(b.X - left, b.Y - up, left, up));
            MarkRoadRect(map, keep, new Rect(b.Right, b.Y - up, right, up));
            MarkRoadRect(map, keep, new Rect(b.X - left, b.Bottom, left, down));
            MarkRoadRect(map, keep, new Rect(b.Right, b.Bottom, right, down));
        }

        // Ribbon blocks only keep the road they face
        static void KeepRoadOnSide(CityMap map, Rect b, Compass side, bool[,] keep)
        {
            const RoadFlags H = RoadFlags.Horizontal, V = RoadFlags.Vertical;
            switch (side)
            {
                case Compass.North: for (int x = b.X; x < b.Right; x++) MarkRun(map, keep, x, b.Y - 1, 0, -1, H); break;
                case Compass.South: for (int x = b.X; x < b.Right; x++) MarkRun(map, keep, x, b.Bottom, 0, 1, H); break;
                case Compass.West: for (int y = b.Y; y < b.Bottom; y++) MarkRun(map, keep, b.X - 1, y, -1, 0, V); break;
                default: for (int y = b.Y; y < b.Bottom; y++) MarkRun(map, keep, b.Right, y, 1, 0, V); break;
            }
        }

        static bool IsRoadRunning(CityMap map, int x, int y, RoadFlags direction) =>
            map.InBounds(x, y) && map[x, y].Type == TileType.Road && (map[x, y].Road & direction) != 0;

        static int RunLength(CityMap map, int x, int y, int dx, int dy, RoadFlags direction)
        {
            int n = 0;
            while (IsRoadRunning(map, x, y, direction)) { n++; x += dx; y += dy; }
            return n;
        }

        static void MarkRun(CityMap map, bool[,] keep, int x, int y, int dx, int dy, RoadFlags direction)
        {
            while (IsRoadRunning(map, x, y, direction)) { keep[x, y] = true; x += dx; y += dy; }
        }

        static void MarkRoadRect(CityMap map, bool[,] keep, Rect r)
        {
            for (int x = r.X; x < r.Right; x++)
                for (int y = r.Y; y < r.Bottom; y++)
                    if (map.InBounds(x, y) && map[x, y].Type == TileType.Road)
                        keep[x, y] = true;
        }

        // A major road kept in two places (e.g. two lobes of the city) is kept
        // all the way between them, so it acts like a highway linking them.
        static void FillMajorRoadGaps(CityMap map, bool[,] keep)
        {
            foreach (RoadSegment road in map.Roads.Where(r => r.Major))
            {
                var (lo, hi) = KeptSpan(road, (x, y) => keep[x, y]);
                if (lo < 0) continue;

                // Fill each unkept stretch between lo and hi, unless it would
                // cross a blocked area (then the two parts stay separate)
                int p = lo;
                while (p <= hi)
                {
                    var (cx, cy) = CentreTile(road, p);
                    if (keep[cx, cy]) { p++; continue; }
                    int gapEnd = p;
                    while (gapEnd + 1 <= hi)
                    {
                        var (nx, ny) = CentreTile(road, gapEnd + 1);
                        if (keep[nx, ny]) break;
                        gapEnd++;
                    }
                    Rect gap = SubRect(road, p, gapEnd);
                    if (!AnyBlocked(map, gap))
                        for (int x = gap.X; x < gap.Right; x++)
                            for (int y = gap.Y; y < gap.Bottom; y++)
                                keep[x, y] = true;
                    p = gapEnd + 1;
                }
            }
        }

        // True if any local tile in the rect is in or too close to a blocked area
        static bool AnyBlocked(CityMap map, Rect r)
        {
            for (int x = r.X; x < r.Right; x++)
                for (int y = r.Y; y < r.Bottom; y++)
                    if (map.LocalBlocked(x, y)) return true;
            return false;
        }

        // Removes road networks cut off from the rest of the city. Keeps the
        // biggest network, plus any network that reaches the main road or an
        // avenue (a blocked area can split a sector in two, and each half that
        // meets an avenue still joins the city). Anything else, like a lone edge
        // block with a ring of road round it, is removed.
        static void RemoveDisconnectedRoads(CityMap map, CitySettings s)
        {
            var label = new int[map.Width, map.Height];
            var sizes = new List<int> { 0 };
            var meetsAvenue = new List<bool> { false };
            int[] stepX = { 1, -1, 0, 0 }, stepY = { 0, 0, 1, -1 };

            for (int sx = 0; sx < map.Width; sx++)
                for (int sy = 0; sy < map.Height; sy++)
                {
                    if (map[sx, sy].Type != TileType.Road || label[sx, sy] != 0) continue;
                    int id = sizes.Count, size = 0;
                    bool touches = false;
                    var queue = new Queue<(int X, int Y)>();
                    queue.Enqueue((sx, sy));
                    label[sx, sy] = id;
                    while (queue.Count > 0)
                    {
                        var (x, y) = queue.Dequeue();
                        size++;

                        // Does this road tile reach an avenue's road or pavement?
                        if (!touches)
                        {
                            var (wx, wy) = map.ToWorld(x + 0.5, y + 0.5);
                            touches = map.BoundaryGap(wx, wy) <= s.SidewalkWidth + 0.5;
                        }

                        for (int i = 0; i < 4; i++)
                        {
                            int nx = x + stepX[i], ny = y + stepY[i];
                            if (!map.InBounds(nx, ny) || label[nx, ny] != 0 || map[nx, ny].Type != TileType.Road) continue;
                            label[nx, ny] = id;
                            queue.Enqueue((nx, ny));
                        }
                    }
                    sizes.Add(size);
                    meetsAvenue.Add(touches);
                }
            if (sizes.Count == 1) return;

            int biggest = sizes.IndexOf(sizes.Max());
            for (int x = 0; x < map.Width; x++)
                for (int y = 0; y < map.Height; y++)
                {
                    int id = label[x, y];
                    if (map[x, y].Type == TileType.Road && id != biggest && !meetsAvenue[id])
                        map[x, y] = Cell.Countryside;
                }
        }

        static bool RoadOnAllSides(CityMap map, Rect b)
        {
            for (int x = b.X; x < b.Right; x++)
                if (map[x, b.Y - 1].Type != TileType.Road || map[x, b.Bottom].Type != TileType.Road) return false;
            for (int y = b.Y; y < b.Bottom; y++)
                if (map[b.X - 1, y].Type != TileType.Road || map[b.Right, y].Type != TileType.Road) return false;
            return true;
        }

        static bool HasRoadBeside(CityMap map, Rect b) =>
            map[b.X, b.Y - 1].Type == TileType.Road || map[b.X - 1, b.Y].Type == TileType.Road ||
            map[b.X, b.Bottom].Type == TileType.Road || map[b.Right, b.Y].Type == TileType.Road;

        // Which side of the block (if any) faces a major road
        static Compass? MajorRoadSide(CityMap map, Rect b)
        {
            bool IsMajor(int x, int y) =>
                map.InBounds(x, y) && map[x, y].Type == TileType.Road && (map[x, y].Road & RoadFlags.Major) != 0;

            int mx = b.X + b.W / 2, my = b.Y + b.H / 2;
            if (IsMajor(mx, b.Y - 1)) return Compass.North;
            if (IsMajor(mx, b.Bottom)) return Compass.South;
            if (IsMajor(b.X - 1, my)) return Compass.West;
            if (IsMajor(b.Right, my)) return Compass.East;
            return null;
        }

        // ---------------- Stubs ----------------

        // Where a major road originally carried on past the city, add a short
        // stub instead and record it, so it can be joined to an outside road later.
        static void AddStubs(CityMap map, CitySettings s)
        {
            foreach (RoadSegment road in map.Roads.Where(r => r.Major))
            {
                var (lo, hi) = KeptSpan(road, (x, y) => map[x, y].Type == TileType.Road);
                if (lo < 0) continue;

                int start = road.Horizontal ? road.Area.X : road.Area.Y;
                int end = (road.Horizontal ? road.Area.Right : road.Area.Bottom) - 1;

                if (hi < end) AddStub(map, road, hi + 1, Math.Min(end, hi + s.StubLength), +1);
                if (lo > start) AddStub(map, road, Math.Max(start, lo - s.StubLength), lo - 1, -1);
            }
        }

        static void AddStub(CityMap map, RoadSegment road, int from, int to, int dir)
        {
            if (to < from) return;
            Rect area = SubRect(road, from, to);

            // Only where the road really leaves the city: not where it was cut
            // short by a boundary road or runs on into the next sector
            var (x, y) = CentreTile(road, dir > 0 ? to : from);
            var (wx, wy) = map.ToWorld(x + 0.5, y + 0.5);
            if (map.Shape.EdgeDistance(wx, wy) <= 1 || !map.Allowed(wx, wy)) return;
            if (AnyBlocked(map, area)) return;

            DrawRoad(map, area, road.Horizontal, major: true,
                     RoadFlags.Stub | (road.Main ? RoadFlags.Main : RoadFlags.None));
            map[x, y].Road |= RoadFlags.StubEnd;

            Compass facing = road.Horizontal
                ? (dir > 0 ? Compass.East : Compass.West)
                : (dir > 0 ? Compass.South : Compass.North);
            double degrees = facing switch { Compass.East => 0, Compass.South => 90, Compass.West => 180, _ => 270 };
            var (exactX, exactY) = facing switch
            {
                Compass.East => (x + 1.0, y + 0.5),
                Compass.West => (x + 0.0, y + 0.5),
                Compass.South => (x + 0.5, y + 1.0),
                _ => (x + 0.5, y + 0.0)
            };
            map.Stubs.Add(new RoadStub(x, y, facing, road.Major, road.Width, area, degrees, false, exactX, exactY));
        }

        // ---------------- Road helpers ----------------

        // Centre-line tile at position p along the road
        static (int X, int Y) CentreTile(RoadSegment r, int p) =>
            r.Horizontal ? (p, r.Area.Y + r.Area.H / 2) : (r.Area.X + r.Area.W / 2, p);

        // First and last positions along the road whose centre tile passes the test
        static (int Lo, int Hi) KeptSpan(RoadSegment r, Func<int, int, bool> test)
        {
            int start = r.Horizontal ? r.Area.X : r.Area.Y;
            int end = r.Horizontal ? r.Area.Right : r.Area.Bottom;
            int lo = -1, hi = -1;
            for (int p = start; p < end; p++)
            {
                var (x, y) = CentreTile(r, p);
                if (!test(x, y)) continue;
                if (lo < 0) lo = p;
                hi = p;
            }
            return (lo, hi);
        }

        // Full-width piece of the road between positions lo and hi (inclusive)
        static Rect SubRect(RoadSegment r, int lo, int hi) =>
            r.Horizontal
                ? new Rect(lo, r.Area.Y, hi - lo + 1, r.Area.H)
                : new Rect(r.Area.X, lo, r.Area.W, hi - lo + 1);

        // ---------------- Districts & plot planning ----------------

        static District PickDistrict(CityMap map, CitySettings s, Rect b)
        {
            double cx = b.CentreX, cy = b.CentreY;
            double t = map.EdgeAt(cx, cy);
            double d = t + (map.DistrictAt(cx, cy) - 0.5) * 0.4;     // wobbly rings, not circles

            if (d < s.DowntownSize) return District.Downtown;
            if (d > 0.4 && map.IndustryAt(cx, cy) > 1 - s.IndustrialShare) return District.Industrial;

            // Shops line most major roads and the avenues (a different noise
            // sample decides which stretches)
            var (wx, wy) = map.ToWorld(cx, cy);
            bool facesAvenue = map.BoundaryGap(wx, wy) < Math.Max(b.W, b.H) / 2.0 + 4;
            if ((MajorRoadSide(map, b) is not null || facesAvenue) && map.DistrictAt(cx * 1.7 + 40, cy * 1.7) > 0.4)
                return District.Commercial;

            return d < s.InnerCitySize ? District.Residential : District.Suburban;
        }

        // Decide each plot's use up front, so empty blocks can be dropped
        // before any roads are kept
        static BlockPlan PlanBlock(CityMap map, CitySettings s, Random rng, Rect block)
        {
            var plan = new BlockPlan { Block = block, District = PickDistrict(map, s, block) };
            Rect inner = block.Shrink(s.SidewalkWidth);
            if (inner.W <= 0 || inner.H <= 0) return plan;

            LayOutParcels(map, s, rng, plan, inner);
            foreach (Parcel p in plan.Parcels)
            {
                if (p.Type == ParcelType.Plaza) continue;
                // Plots past the outline, and some on the outskirts, stay countryside
                double edge = map.EdgeAt(p.Area.CentreX, p.Area.CentreY);
                double fringe = Math.Clamp((edge - 0.65) / 0.35, 0, 1);
                if (edge > 1.05 || rng.NextDouble() < fringe * s.OutskirtsVacancy)
                    p.Type = ParcelType.Vacant;
            }
            return plan;
        }

        // Ribbon development grows outwards from the city one block at a time:
        // a block only gets houses if the block before it along the same side of
        // the same road has them, so there are never long empty stretches of road
        // leading out to an isolated group of houses. Each step outwards is a
        // little less likely, so the ribbons thin out and stop.
        static void AddRibbonDevelopment(CityMap map, CitySettings s, Random rng,
                                         List<BlockPlan> plans, List<(Rect Block, Compass Side)> candidates)
        {
            var remaining = candidates.OrderBy(c => map.EdgeAt(c.Block.CentreX, c.Block.CentreY)).ToList();
            bool grew = true;
            while (grew && remaining.Count > 0)
            {
                grew = false;
                foreach (var (block, side) in remaining.ToList())
                {
                    if (!plans.Any(p => NextAlongRoad(p.Block, block, side, s))) continue;
                    remaining.Remove((block, side));

                    double edge = map.EdgeAt(block.CentreX, block.CentreY);
                    if (rng.NextDouble() > 1 - (edge - 1) / s.RibbonReach) continue;   // the ribbon stops here

                    BlockPlan plan = PlanRibbonBlock(map, s, rng, block, side);
                    if (!plan.BuildsSomething) continue;
                    plans.Add(plan);
                    grew = true;
                }
            }
        }

        // True if b is the next block after a along the road on b's given side:
        // both blocks front the same road, with only a junction between them
        static bool NextAlongRoad(Rect a, Rect b, Compass side, CitySettings s)
        {
            int maxGap = Math.Max(s.MainRoadWidth, s.MajorRoadWidth);
            bool GapOk(int aStart, int aEnd, int bStart, int bEnd) =>
                (bStart >= aEnd && bStart - aEnd <= maxGap) || (aStart >= bEnd && aStart - bEnd <= maxGap);

            return side switch
            {
                Compass.West => a.X == b.X && GapOk(a.Y, a.Bottom, b.Y, b.Bottom),
                Compass.East => a.Right == b.Right && GapOk(a.Y, a.Bottom, b.Y, b.Bottom),
                Compass.North => a.Y == b.Y && GapOk(a.X, a.Right, b.X, b.Right),
                _ => a.Bottom == b.Bottom && GapOk(a.X, a.Right, b.X, b.Right),
            };
        }

        // Outside the outline: houses only along the side facing the major road.
        // Country roads have no pavement, so the plots run right up to the road.
        static BlockPlan PlanRibbonBlock(CityMap map, CitySettings s, Random rng, Rect block, Compass side)
        {
            var plan = new BlockPlan { Block = block, District = District.Suburban, Ribbon = true, RibbonSide = side };
            Rect inner = block;

            LayOutParcels(map, s, rng, plan, inner);
            foreach (Parcel p in plan.Parcels)
                if (p.Street != side || rng.NextDouble() < 0.25)
                    p.Type = ParcelType.Vacant;
            return plan;
        }

        static void LayOutParcels(CityMap map, CitySettings s, Random rng, BlockPlan plan, Rect inner)
        {
            void AddLots(List<Rect> lots, ParcelType type) =>
                plan.Parcels.AddRange(lots.Select(l => new Parcel { Area = l, Type = type }));
            void AddRing(int depth, int minW, int maxW, ParcelType type) =>
                plan.Parcels.AddRange(Perimeter(inner, depth, minW, maxW, rng)
                    .Select(u => new Parcel { Area = u.Area, Street = u.Street, Type = type }));

            switch (plan.District)
            {
                case District.Downtown:
                    {
                        // Big lots, no alleys: towers built right up to the pavement
                        var lots = new List<Rect>();
                        SplitLots(inner, 4, 9, 0.3, rng, lots, alley: 0);
                        foreach (Rect lot in lots)
                            plan.Parcels.Add(new Parcel
                            {
                                Area = lot,
                                Type = rng.NextDouble() < s.PlazaChance ? ParcelType.Plaza : ParcelType.Tower
                            });
                        break;
                    }

                case District.Commercial:
                    // Continuous row of shopfronts round the block, service yards behind
                    AddRing(rng.Next(3, 5), 2, 5, ParcelType.Shop);
                    break;

                case District.Residential:
                    // Terraced houses, or apartment blocks where the height field is high,
                    // round a shared back-garden courtyard
                    if (map.HeightAt(inner.CentreX, inner.CentreY) > 0.6)
                        AddRing(rng.Next(3, 5), 4, 7, ParcelType.Apartment);
                    else
                        AddRing(rng.Next(2, 4), 2, 3, ParcelType.Terrace);
                    break;

                case District.Suburban:
                    // Detached houses on garden plots facing the street
                    AddRing(rng.Next(4, 6), 3, 5, ParcelType.House);
                    break;

                case District.Industrial:
                    {
                        var lots = new List<Rect>();
                        SplitLots(inner, 4, 9, 0.1, rng, lots);
                        AddLots(lots, ParcelType.IndustrialUnit);
                        break;
                    }
            }
        }

        // Splits a ring of the given depth round the edge of r into units, each
        // facing the street on its outer side. Top and bottom rows own the corners.
        // Blocks too small for a ring get one or two back-to-back rows instead.
        static List<(Rect Area, Compass Street)> Perimeter(Rect r, int depth, int minW, int maxW, Random rng)
        {
            var units = new List<(Rect, Compass)>();

            if (r.W < 2 * depth + 1 || r.H < 2 * depth + 1)
            {
                bool alongX = r.W >= r.H;
                int length = alongX ? r.W : r.H, thickness = alongX ? r.H : r.W;
                bool twoRows = thickness >= 4;
                int firstDepth = twoRows ? thickness / 2 : thickness;

                foreach (var (lo, len) in SplitLength(length, minW, maxW, rng))
                    units.Add(alongX
                        ? (new Rect(r.X + lo, r.Y, len, firstDepth), Compass.North)
                        : (new Rect(r.X, r.Y + lo, firstDepth, len), Compass.West));

                if (twoRows)
                    foreach (var (lo, len) in SplitLength(length, minW, maxW, rng))
                        units.Add(alongX
                            ? (new Rect(r.X + lo, r.Y + firstDepth, len, thickness - firstDepth), Compass.South)
                            : (new Rect(r.X + firstDepth, r.Y + lo, thickness - firstDepth, len), Compass.East));
                return units;
            }

            foreach (var (lo, len) in SplitLength(r.W, minW, maxW, rng))
                units.Add((new Rect(r.X + lo, r.Y, len, depth), Compass.North));
            foreach (var (lo, len) in SplitLength(r.W, minW, maxW, rng))
                units.Add((new Rect(r.X + lo, r.Bottom - depth, len, depth), Compass.South));

            int sideLength = r.H - 2 * depth;
            foreach (var (lo, len) in SplitLength(sideLength, minW, maxW, rng))
                units.Add((new Rect(r.X, r.Y + depth + lo, depth, len), Compass.West));
            foreach (var (lo, len) in SplitLength(sideLength, minW, maxW, rng))
                units.Add((new Rect(r.Right - depth, r.Y + depth + lo, depth, len), Compass.East));

            return units;
        }

        // Cuts a length into pieces between minW and maxW long (as near as possible)
        static List<(int Start, int Length)> SplitLength(int total, int minW, int maxW, Random rng)
        {
            var parts = new List<(int, int)>();
            int pos = 0;
            while (pos < total)
            {
                int left = total - pos;
                int len = left <= maxW ? left : rng.Next(minW, maxW + 1);
                if (left - len > 0 && left - len < minW) len = Math.Max(1, left - minW);
                parts.Add((pos, len));
                pos += len;
            }
            return parts;
        }

        // ---------------- Filling blocks ----------------

        static void BuildBlock(CityMap map, CitySettings s, Random rng, BlockPlan plan)
        {
            int sw = s.SidewalkWidth;
            TileType fill = plan.District is District.Residential or District.Suburban
                ? TileType.Garden : TileType.Ground;

            // Ribbon blocks have no pavement: only their house plots get filled in
            if (!plan.Ribbon)
                FillWithRing(map, plan.Block, sw, TileType.Sidewalk, fill);

            foreach (Parcel p in plan.Parcels)
            {
                switch (p.Type)
                {
                    case ParcelType.Vacant:
                        if (!plan.Ribbon) ClearLot(map, p.Area);
                        break;

                    case ParcelType.Plaza:
                        FillRect(map, p.Area, TileType.Ground);
                        break;

                    case ParcelType.Tower:
                        PlaceTower(map, s, rng, p.Area);
                        break;

                    case ParcelType.Shop:
                        AddBuilding(map, p.Area, NoisyFloors(map, rng, p.Area, 2, 6, 0.15),
                                    BuildingShape.Rectangle, BuildingKind.Shop, plan.District);
                        break;

                    case ParcelType.Terrace:
                        AddBuilding(map, p.Area, NoisyFloors(map, rng, p.Area, 2, 4, 0.1),
                                    BuildingShape.Rectangle, BuildingKind.Terrace, plan.District);
                        break;

                    case ParcelType.Apartment:
                        AddBuilding(map, p.Area, NoisyFloors(map, rng, p.Area, 4, 12, 0.15),
                                    BuildingShape.Rectangle, BuildingKind.Apartment, plan.District);
                        break;

                    case ParcelType.House:
                        FillRect(map, p.Area, TileType.Garden);
                        PlaceHouse(map, rng, p, plan.District);
                        break;

                    case ParcelType.IndustrialUnit:
                        PlaceIndustrialUnit(map, rng, p.Area, plan.District);
                        break;
                }
            }
        }

        static void FillWithRing(CityMap map, Rect block, int ringWidth, TileType ring, TileType inside)
        {
            for (int x = block.X; x < block.Right; x++)
                for (int y = block.Y; y < block.Bottom; y++)
                {
                    bool edge = x < block.X + ringWidth || y < block.Y + ringWidth ||
                                x >= block.Right - ringWidth || y >= block.Bottom - ringWidth;
                    map[x, y].Type = edge ? ring : inside;
                }
        }

        static void FillRect(CityMap map, Rect r, TileType type)
        {
            for (int x = r.X; x < r.Right; x++)
                for (int y = r.Y; y < r.Bottom; y++)
                    map[x, y].Type = type;
        }

        static void ClearLot(CityMap map, Rect lot)
        {
            for (int x = lot.X; x < lot.Right; x++)
                for (int y = lot.Y; y < lot.Bottom; y++)
                    if (map[x, y].Type is TileType.Ground or TileType.Garden or TileType.CarPark)
                        map[x, y] = Cell.Countryside;
        }

        static void ClearRoad(CityMap map, Rect r)
        {
            for (int x = r.X; x < r.Right; x++)
                for (int y = r.Y; y < r.Bottom; y++)
                    map[x, y] = Cell.Countryside;
        }

        static bool NearRoad(CityMap map, int x, int y, int range)
        {
            for (int ox = -range; ox <= range; ox++)
                for (int oy = -range; oy <= range; oy++)
                {
                    int nx = x + ox, ny = y + oy;
                    if (map.InBounds(nx, ny) && map[nx, ny].Type == TileType.Road) return true;
                }
            return false;
        }

        // Park tiles over all its blocks and the old roads between them,
        // with a sidewalk wherever the park meets a road
        static void BuildPark(CityMap map, CitySettings s, Park park)
        {
            var areas = park.Blocks.Concat(park.Joins).ToList();
            foreach (Rect r in areas)
                FillRect(map, r, TileType.Park);
            foreach (Rect r in areas)
                for (int x = r.X; x < r.Right; x++)
                    for (int y = r.Y; y < r.Bottom; y++)
                        if (NearRoad(map, x, y, s.SidewalkWidth))
                            map[x, y].Type = TileType.Sidewalk;
        }

        static void SplitLots(Rect r, int minLot, int maxLot, double largeLotChance, Random rng,
                              List<Rect> lots, int alley = 1)
        {
            bool canCutX = r.W >= minLot * 2 + alley;
            bool canCutY = r.H >= minLot * 2 + alley;
            bool tooBig = r.W > maxLot || r.H > maxLot;
            bool keepBig = r.W <= maxLot * 2 && r.H <= maxLot * 2 &&
                           rng.NextDouble() < largeLotChance;

            if (!tooBig || keepBig || (!canCutX && !canCutY))
            {
                lots.Add(r);
                return;
            }

            bool cutX = canCutX && (!canCutY || r.W > r.H || (r.W == r.H && rng.Next(2) == 0));
            if (cutX)
            {
                int cut = rng.Next(r.X + minLot, r.Right - minLot - alley + 1);
                SplitLots(new Rect(r.X, r.Y, cut - r.X, r.H), minLot, maxLot, largeLotChance, rng, lots, alley);
                SplitLots(new Rect(cut + alley, r.Y, r.Right - cut - alley, r.H), minLot, maxLot, largeLotChance, rng, lots, alley);
            }
            else
            {
                int cut = rng.Next(r.Y + minLot, r.Bottom - minLot - alley + 1);
                SplitLots(new Rect(r.X, r.Y, r.W, cut - r.Y), minLot, maxLot, largeLotChance, rng, lots, alley);
                SplitLots(new Rect(r.X, cut + alley, r.W, r.Bottom - cut - alley), minLot, maxLot, largeLotChance, rng, lots, alley);
            }
        }

        // ---------------- Merging blocks (parks & large plots) ----------------

        // If b sits directly across one minor road from a, lined up exactly along
        // that road, returns the road strip between them; otherwise null.
        // (Major and main roads are never removed.)
        static Rect? SharedMinorRoad(CityMap map, Rect a, Rect b)
        {
            Rect strip;
            if (a.Y == b.Y && a.H == b.H && b.X >= a.Right) strip = new Rect(a.Right, a.Y, b.X - a.Right, a.H);
            else if (a.Y == b.Y && a.H == b.H && a.X >= b.Right) strip = new Rect(b.Right, a.Y, a.X - b.Right, a.H);
            else if (a.X == b.X && a.W == b.W && b.Y >= a.Bottom) strip = new Rect(a.X, a.Bottom, a.W, b.Y - a.Bottom);
            else if (a.X == b.X && a.W == b.W && a.Y >= b.Bottom) strip = new Rect(a.X, b.Bottom, a.W, a.Y - b.Bottom);
            else return null;

            if (strip.W <= 0 || strip.H <= 0) return null;
            for (int x = strip.X; x < strip.Right; x++)
                for (int y = strip.Y; y < strip.Bottom; y++)
                {
                    Cell c = map[x, y];
                    if (c.Type != TileType.Road || (c.Road & (RoadFlags.Major | RoadFlags.Main)) != 0)
                        return null;
                }
            return strip;
        }

        // Large plots: 2-4 blocks in a straight row, so the plot is a rectangle.
        // Mostly in industrial districts and on the outskirts.
        static void ChooseLargePlots(CityMap map, CitySettings s, Random rng, List<BlockPlan> plans, HashSet<Rect> merged)
        {
            if (s.LargePlotRatio <= 0 || plans.Count == 0) return;
            int target = (int)Math.Round(plans.Count / (s.LargePlotRatio + 1));
            var byBlock = plans.Where(p => !p.Ribbon).ToDictionary(p => p.Block);
            var blocks = byBlock.Keys.ToList();
            if (blocks.Count == 0) return;
            int placed = 0;

            for (int attempt = 0; attempt < 400 && placed < target; attempt++)
            {
                Rect seed = blocks[rng.Next(blocks.Count)];
                if (merged.Contains(seed)) continue;
                District district = byBlock[seed].District;
                double accept = district switch
                {
                    District.Industrial => 1.0,
                    District.Suburban => 0.5,
                    District.Downtown => 0.3,
                    _ => 0.25
                };
                if (rng.NextDouble() > accept) continue;

                int want = rng.Next(s.MinLargePlotBlocks, s.MaxLargePlotBlocks + 1);
                bool alongX = rng.Next(2) == 0;
                var row = GrowRow(map, blocks, merged, seed, want, alongX, rng);
                if (row.Count < s.MinLargePlotBlocks)
                    row = GrowRow(map, blocks, merged, seed, want, !alongX, rng);
                if (row.Count < s.MinLargePlotBlocks) continue;

                for (int i = 1; i < row.Count; i++)
                    if (SharedMinorRoad(map, row[i - 1], row[i]) is Rect join)
                        ClearRoad(map, join);

                int x0 = row.Min(b => b.X), y0 = row.Min(b => b.Y);
                int x1 = row.Max(b => b.Right), y1 = row.Max(b => b.Bottom);
                var plot = new LargePlot { Kind = PickPlotKind(rng, district), Area = new Rect(x0, y0, x1 - x0, y1 - y0) };
                plot.Blocks.AddRange(row);
                map.LargePlots.Add(plot);
                foreach (Rect b in row) merged.Add(b);
                placed += row.Count;
            }
        }

        // Extends a row of blocks from seed, along one axis, at either end
        static List<Rect> GrowRow(CityMap map, List<Rect> blocks, HashSet<Rect> merged, Rect seed, int want, bool alongX, Random rng)
        {
            var row = new List<Rect> { seed };
            bool grew = true;
            while (row.Count < want && grew)
            {
                grew = false;
                bool[] ends = rng.Next(2) == 0 ? new[] { true, false } : new[] { false, true };
                foreach (bool atEnd in ends)
                {
                    Rect from = atEnd ? row[^1] : row[0];
                    foreach (Rect b in blocks)
                    {
                        if (merged.Contains(b) || row.Contains(b)) continue;
                        bool lined = alongX ? b.Y == from.Y && b.H == from.H : b.X == from.X && b.W == from.W;
                        bool after = alongX ? b.X > from.X : b.Y > from.Y;
                        if (!lined || after != atEnd || SharedMinorRoad(map, from, b) is null) continue;

                        if (atEnd) row.Add(b); else row.Insert(0, b);
                        grew = true;
                        break;
                    }
                    if (grew) break;
                }
            }
            return row;
        }

        static PlotKind PickPlotKind(Random rng, District district)
        {
            // weights for Warehouse, IndustrialEstate, CarPark
            int[] weights = district switch
            {
                District.Industrial => new[] { 4, 4, 1 },
                District.Suburban => new[] { 3, 2, 1 },
                District.Downtown => new[] { 0, 0, 1 },
                _ => new[] { 1, 1, 3 }
            };
            int roll = rng.Next(weights.Sum());
            for (int i = 0; i < weights.Length; i++)
            {
                if (roll < weights[i]) return (PlotKind)i;
                roll -= weights[i];
            }
            return PlotKind.CarPark;
        }

        // Parks: 2-6 neighbouring blocks in any shape. Each block added removes
        // one road to the park so far, which keeps the road network connected.
        static void ChooseParks(CityMap map, CitySettings s, Random rng, List<BlockPlan> plans, HashSet<Rect> merged)
        {
            if (s.ParkRatio <= 0 || plans.Count == 0) return;
            int target = (int)Math.Round(plans.Count / (s.ParkRatio + 1));
            var byBlock = plans.Where(p => !p.Ribbon).ToDictionary(p => p.Block);
            var blocks = byBlock.Keys.ToList();
            if (blocks.Count == 0) return;
            int placed = 0;

            for (int attempt = 0; attempt < 400 && placed < target; attempt++)
            {
                Rect seed = blocks[rng.Next(blocks.Count)];
                if (merged.Contains(seed)) continue;
                double accept = byBlock[seed].District switch
                {
                    District.Residential or District.Suburban => 1.0,
                    District.Downtown => 0.6,
                    District.Commercial => 0.4,
                    _ => 0.2
                };
                if (rng.NextDouble() > accept) continue;

                int want = rng.Next(s.MinParkBlocks, s.MaxParkBlocks + 1);
                want = Math.Min(want, Math.Max(s.MinParkBlocks, target - placed));

                var members = new List<Rect> { seed };
                var joins = new List<Rect>();
                while (members.Count < want)
                {
                    var options = new List<(Rect Block, Rect Join, int Links)>();
                    foreach (Rect b in blocks)
                    {
                        if (merged.Contains(b) || members.Contains(b)) continue;
                        Rect? join = null;
                        int links = 0;
                        foreach (Rect m in members)
                            if (SharedMinorRoad(map, m, b) is Rect strip) { links++; join ??= strip; }
                        if (join is Rect j) options.Add((b, j, links));
                    }
                    if (options.Count == 0) break;

                    // Prefer blocks touching just one park block, so fewer bits of
                    // road are left poking into the park
                    int fewest = options.Min(o => o.Links);
                    var best = options.Where(o => o.Links == fewest).ToList();
                    var pick = best[rng.Next(best.Count)];
                    members.Add(pick.Block);
                    joins.Add(pick.Join);
                }
                if (members.Count < s.MinParkBlocks) continue;

                foreach (Rect j in joins) ClearRoad(map, j);
                var park = new Park();
                park.Blocks.AddRange(members);
                park.Joins.AddRange(joins);
                map.Parks.Add(park);
                foreach (Rect b in members) merged.Add(b);
                placed += members.Count;
            }
        }

        // ---------------- Large plots ----------------

        static void BuildLargePlot(CityMap map, CitySettings s, Random rng, LargePlot plot)
        {
            FillWithRing(map, plot.Area, s.SidewalkWidth, TileType.Sidewalk, TileType.Ground);
            Rect inner = plot.Area.Shrink(s.SidewalkWidth);
            if (inner.W <= 0 || inner.H <= 0) return;

            switch (plot.Kind)
            {
                case PlotKind.CarPark:
                    FillRect(map, inner, TileType.CarPark);
                    break;

                case PlotKind.Warehouse:
                    {
                        // One or two huge low sheds with a service yard round them
                        Rect yard = inner.Shrink(1);
                        if (yard.W <= 0 || yard.H <= 0) break;
                        bool alongX = yard.W >= yard.H;
                        int length = alongX ? yard.W : yard.H;
                        int sheds = length >= 20 ? 2 : 1;
                        const int gap = 2;
                        int shedLength = (length - gap * (sheds - 1)) / sheds;
                        for (int i = 0; i < sheds; i++)
                        {
                            int offset = i * (shedLength + gap);
                            Rect shed = alongX
                                ? new Rect(yard.X + offset, yard.Y, shedLength, yard.H)
                                : new Rect(yard.X, yard.Y + offset, yard.W, shedLength);
                            AddBuilding(map, shed, rng.Next(1, 4), BuildingShape.Rectangle, BuildingKind.Warehouse, District.Industrial);
                        }
                        break;
                    }

                case PlotKind.IndustrialEstate:
                    {
                        var units = new List<Rect>();
                        SplitLots(inner, 4, 9, 0.1, rng, units);
                        foreach (Rect unit in units)
                            PlaceIndustrialUnit(map, rng, unit, District.Industrial);
                        break;
                    }
            }
        }

        // A shed with a yard round it, or now and then a small car park
        static void PlaceIndustrialUnit(CityMap map, Random rng, Rect unit, District district)
        {
            if (rng.NextDouble() < 0.2)
            {
                FillRect(map, unit, TileType.CarPark);
                return;
            }
            Rect shed = unit.Shrink(1);
            if (shed.W < 2 || shed.H < 2) return;          // stays as yard
            AddBuilding(map, shed, rng.Next(1, 3), BuildingShape.Rectangle, BuildingKind.IndustrialUnit, district);
        }

        // ---------------- Buildings ----------------

        // Height from the smooth height field, so neighbouring buildings are
        // similar and the skyline rises and falls gradually; plus a little jitter
        static int NoisyFloors(CityMap map, Random rng, Rect area, double low, double high, double jitter)
        {
            double hf = map.HeightAt(area.CentreX, area.CentreY);
            double f = Lerp(low, high, hf) * (1 + (rng.NextDouble() * 2 - 1) * jitter);
            return Math.Max(1, (int)Math.Round(f));
        }

        // Downtown: height grows towards the core, with towers clustering
        // wherever the height field peaks
        static void PlaceTower(CityMap map, CitySettings s, Random rng, Rect lot)
        {
            double t = Math.Clamp(map.EdgeAt(lot.CentreX, lot.CentreY), 0, 1);
            double core = Math.Clamp(1 - t / (s.DowntownSize + 0.1), 0, 1);
            double hf = map.HeightAt(lot.CentreX, lot.CentreY);
            double f = (Lerp(6, 30, core) + hf * hf * 60 * core) * (1 + (rng.NextDouble() * 2 - 1) * 0.2);
            int floors = Math.Max(3, (int)Math.Round(f));

            bool tall = floors >= 20 && Math.Min(lot.W, lot.H) >= 4;
            BuildingShape shape = PickShape(rng, lot.W, lot.H, tall);
            int[,] footprint = BuildFootprint(shape, lot.W, lot.H, floors, rng);
            AddBuilding(map, lot, footprint, shape, BuildingKind.Office, District.Downtown);
        }

        // Detached house: 1-tile front garden, a gap on one side, back garden behind
        static void PlaceHouse(CityMap map, Random rng, Parcel p, District district)
        {
            Rect a = p.Area;
            bool frontAlongX = p.Street is Compass.North or Compass.South;
            int frontage = frontAlongX ? a.W : a.H;
            int plotDepth = frontAlongX ? a.H : a.W;
            int width = frontage - 1;
            int depth = Math.Min(3, plotDepth - 2);
            if (width < 2 || depth < 2) return;              // too small: just garden

            const int setback = 1;
            Rect house = p.Street switch
            {
                Compass.North => new Rect(a.X, a.Y + setback, width, depth),
                Compass.South => new Rect(a.X, a.Bottom - setback - depth, width, depth),
                Compass.West => new Rect(a.X + setback, a.Y, depth, width),
                _ => new Rect(a.Right - setback - depth, a.Y, depth, width),
            };
            AddBuilding(map, house, rng.NextDouble() < 0.6 ? 2 : 1, BuildingShape.Rectangle, BuildingKind.House, district);
        }

        // Solid rectangular building of one height
        static void AddBuilding(CityMap map, Rect area, int floors, BuildingShape shape, BuildingKind kind, District district)
        {
            var footprint = new int[area.W, area.H];
            for (int x = 0; x < area.W; x++)
                for (int y = 0; y < area.H; y++)
                    footprint[x, y] = floors;
            AddBuilding(map, area, footprint, shape, kind, district);
        }

        // Writes a building into the map; footprint holds floors per tile (0 = none)
        static void AddBuilding(CityMap map, Rect lot, int[,] footprint, BuildingShape shape, BuildingKind kind, District district)
        {
            var building = new Building { Id = map.Buildings.Count, Shape = shape, Kind = kind, District = district, Lot = lot };
            for (int lx = 0; lx < lot.W; lx++)
                for (int ly = 0; ly < lot.H; ly++)
                {
                    int f = footprint[lx, ly];
                    if (f <= 0) continue;

                    int x = lot.X + lx, y = lot.Y + ly;
                    ref Cell c = ref map[x, y];
                    c.Type = TileType.Building;
                    c.Floors = f;
                    c.BuildingId = building.Id;

                    building.Tiles.Add(new BuildingTile(x, y, f));
                    building.MaxFloors = Math.Max(building.MaxFloors, f);
                }

            if (building.Tiles.Count > 0)
                map.Buildings.Add(building);
        }

        static BuildingShape PickShape(Random rng, int w, int h, bool tall)
        {
            int min = Math.Min(w, h);
            var options = new List<(BuildingShape Shape, int Weight)>
        {
            (BuildingShape.Rectangle, tall ? 2 : 4)
        };
            if (min >= 3) options.Add((BuildingShape.LShape, 2));
            if (min >= 4 && !tall) options.Add((BuildingShape.UShape, 1));
            if (min >= 5 && !tall) options.Add((BuildingShape.Courtyard, 2));
            if (min >= 5) options.Add((BuildingShape.Cross, 1));
            if (min >= 5 && tall) options.Add((BuildingShape.Tiered, 4));

            int roll = rng.Next(options.Sum(o => o.Weight));
            foreach (var (shape, weight) in options)
            {
                if (roll < weight) return shape;
                roll -= weight;
            }
            return BuildingShape.Rectangle;
        }

        // Returns floors per tile within the lot (0 = no building on that tile)
        static int[,] BuildFootprint(BuildingShape shape, int w, int h, int floors, Random rng)
        {
            var m = new int[w, h];

            switch (shape)
            {
                case BuildingShape.LShape:
                    {
                        // Remove one corner
                        int cutW = rng.Next(Math.Max(1, w / 3), w - w / 3 + 1);
                        int cutH = rng.Next(Math.Max(1, h / 3), h - h / 3 + 1);
                        int corner = rng.Next(4);
                        for (int x = 0; x < w; x++)
                            for (int y = 0; y < h; y++)
                            {
                                bool inX = corner % 2 == 0 ? x < cutW : x >= w - cutW;
                                bool inY = corner < 2 ? y < cutH : y >= h - cutH;
                                m[x, y] = inX && inY ? 0 : floors;
                            }
                        break;
                    }

                case BuildingShape.UShape:
                    {
                        // Cut a notch into the middle of one side
                        int side = rng.Next(4);
                        int armX = Math.Max(1, w / 3), armY = Math.Max(1, h / 3);
                        int depthX = Math.Max(1, w / 2), depthY = Math.Max(1, h / 2);
                        for (int x = 0; x < w; x++)
                            for (int y = 0; y < h; y++)
                            {
                                bool notch = side switch
                                {
                                    0 => x >= armX && x < w - armX && y < depthY,        // opens north
                                    1 => x >= armX && x < w - armX && y >= h - depthY,   // opens south
                                    2 => y >= armY && y < h - armY && x < depthX,        // opens west
                                    _ => y >= armY && y < h - armY && x >= w - depthX,   // opens east
                                };
                                m[x, y] = notch ? 0 : floors;
                            }
                        break;
                    }

                case BuildingShape.Courtyard:
                    {
                        // Ring of building round an open centre
                        int wall = Math.Max(1, Math.Min(w, h) / 4);
                        for (int x = 0; x < w; x++)
                            for (int y = 0; y < h; y++)
                            {
                                bool hole = x >= wall && x < w - wall && y >= wall && y < h - wall;
                                m[x, y] = hole ? 0 : floors;
                            }
                        break;
                    }

                case BuildingShape.Cross:
                    {
                        int armW = Math.Max(1, (w + 1) / 3);
                        if ((w - armW) % 2 != 0) armW++;              // keep it centred
                        int armH = Math.Max(1, (h + 1) / 3);
                        if ((h - armH) % 2 != 0) armH++;
                        int x0 = (w - armW) / 2, y0 = (h - armH) / 2;
                        for (int x = 0; x < w; x++)
                            for (int y = 0; y < h; y++)
                            {
                                bool inArm = (x >= x0 && x < x0 + armW) || (y >= y0 && y < y0 + armH);
                                m[x, y] = inArm ? floors : 0;
                            }
                        break;
                    }

                case BuildingShape.Tiered:
                    {
                        // Low podium with a tower rising from the middle
                        // (very tall ones on big lots get an extra middle step)
                        int min = Math.Min(w, h);
                        int inset = Math.Max(1, min / 4);
                        bool threeTiers = floors >= 45 && min >= 9;
                        int podium = Math.Min(floors - 1, rng.Next(2, 7));
                        int middle = Math.Max(podium + 1, (int)(floors * 0.6));
                        for (int x = 0; x < w; x++)
                            for (int y = 0; y < h; y++)
                            {
                                int ring = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y));
                                m[x, y] = ring < inset ? podium
                                        : threeTiers && ring < inset * 2 ? middle
                                        : floors;
                            }
                        break;
                    }

                default: // Rectangle
                    for (int x = 0; x < w; x++)
                        for (int y = 0; y < h; y++)
                            m[x, y] = floors;
                    break;
            }

            return m;
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
    }
}
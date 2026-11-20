using UnityEngine;

[RequireComponent (typeof(Terrain))]
public class terrainGen : MonoBehaviour
{
    
    [Header("Size settings")]
    [Tooltip("Must be a valid output of the equation of (2^n + 1), where N is an int (e.g. 129, 257, 513, 1025)")]
    public int resolution = 1025;
    public float width = 500f;
    public float heightCap = 100f;

    [Header("Perlin Noise Settings")]
    public int seed = 0;
    public float noiseScale = 150f;

    [Range(1, 10)] 
    public int octaves = 5;

    [Range(0f, 1f)] 
    public float persistence = 0.5f;
    
    [Range(1, 10)] 
    public float lacunarity = 2f; // frequency of growth per octave    

    [Header("Mountain Settings")]
    [Range(0f, 1f)]
    [Tooltip("0 = No mountains, 1 = Many mountains")]
    public float mountainAmount = 0.3f;
    public float mountainScale = 300f;
    [Range(0f, 1f)]
    public float mountainSharpness = 0.3f;
    [Range(1, 10)] 
    public int mountainOctaves = 5;
    public float mountainHeight = 1f; // multiplier for ridge height
    [Range(1f, 3f)]
    public float ridgeSharpness = 1f; // lower = rounder peaks
    [Range(0f, 1f)]
    public float mountainPersistence = 0.4f; // lower = less detail
    [Range(0, 10)] 
    public int smoothingPasses = 2; // softens the final terrain

    [Header("Offset")]
    public Vector2 offset; //offset the perlin noise

    [Header("Terrain Shape")]
    public AnimationCurve heightCurve = AnimationCurve.Linear(0, 0, 1, 1);

    void Start()
    {
        generateMap();
    }

    private void OnValidate()
    {
        resolution = Mathf.Max(33, resolution); // minimum map resolution
        octaves = Mathf.Max(1, octaves); // minimum one octave
    }
    
    public void generateMap() // creates the height map and applies it to the terrain data 
    {
        Terrain terrain = GetComponent<Terrain>();
        TerrainData tData = terrain.terrainData;

        float[,] height = generateHeightMap(resolution); // generates a grid of normalised heights (0,1)

        tData.heightmapResolution = resolution;
        tData.size = new Vector3(width, heightCap, width);
        tData.SetHeights(0, 0, height);
    }

    float[,] generateHeightMap(int size) // retruns a size x size grid of the heights (0,1) using octaves
    {
        float[,] map = new float[size, size];

        for (int i = 0; i < size; i++)
        {
             for (int j = 0; j < size; j++)
            {
                float amplitude = 1f;
                float frequency = 1f;
                float height = 0f;
                float maxPossible = 0f;

                for (int k = 0; k < octaves; k++) // each octave creates a more detailed terrain
                {
                    // sampled position from noiseScale and frequency, shifted by the seed
                    float x = (j / noiseScale) * frequency + seed;
                    float y = (i / noiseScale) * frequency + seed;

                    height += Mathf.PerlinNoise(x, y) * amplitude;
                    maxPossible += amplitude;

                    // octaves are made smaller (persistence) and more detailed (lacunarity)
                    amplitude *= persistence;
                    frequency *= lacunarity;
                }

                float baseHeight = height / maxPossible;

                // mountains
                float mx = (j + offset.x) / mountainScale;
                float my = (i + offset.y) / mountainScale;

                float mask = mountainMask(mx, my); 
                float ridges = ridgedNoise(mx * 2f, my * 2f) * mountainHeight;

                float lowLand = baseHeight * 0.2f;
                float finalHeight = lowLand + ridges * mask * 0.8f;
                finalHeight = heightCurve.Evaluate(Mathf.Clamp01(finalHeight));
                map[i, j] = Mathf.Clamp01(finalHeight);
            }
        }
        smoothMap(map, smoothingPasses);
        return map;
    }

    float mountainMask(float mx, float my)
    {
        float n = Mathf.PerlinNoise(mx + seed + 5000f, my + seed + 5000f);
        float threshold = Mathf.Lerp(1f + mountainSharpness, -mountainSharpness, mountainAmount);

        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(threshold - mountainSharpness, threshold + mountainSharpness, n));
    }

    float ridgedNoise(float mx, float my)
    {
        float amplitude = 1f;
        float frequency = 1f;
        float sum = 0f;
        float maxPossible = 0f;
        float weight = 1f;

        for (int i = 0; i < mountainOctaves; i++)
        {
            float n = Mathf.PerlinNoise(mx * frequency + seed + 1000f, my * frequency + seed + 1000f);
            n = Mathf.Clamp01(n);
            n = 1f - Mathf.Abs(n * 2f - 1f); // molds top of mountain into a ridge
            n = Mathf.Pow(n, ridgeSharpness);
            n *= weight; // more detail
            weight = Mathf.Clamp01(n * 2f);

            sum += n * amplitude;
            maxPossible += amplitude;
            amplitude *= mountainPersistence;
            frequency *= lacunarity;
        }
        return sum / maxPossible;
    }

    void smoothMap (float[,] map, int passes) //smooths the terrarain so there are no jagged peaks
    {
        int size = map.GetLength(0);
        float[,] grid = new float[size, size];

        for (int i = 0; i < passes; i++)
        {
            for (int j = 0; j < size; j++)
            {
                for (int k = 0; k < size; k++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int l = -1; l <= 1; l++)
                    {
                        for (int m = -1; m <= 1; m++)
                        {
                            int n = j + l;
                            int o = k + m;
                            if (n < 0 || o < 0 || n >= size || o >= size) continue;
                            sum += map[n, o];
                            count++;
                        } 
                    }
                    grid[j, k] = sum / count;
                }
            }
            System.Array.Copy(grid, map, grid.Length);
        }
    }
}
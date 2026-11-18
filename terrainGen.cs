using UnityEditor.EditorTools;
using UnityEngine;

[RequireComponent (typeof(Terrain))]
public class terrainGen : MonoBehaviour
{
    
    [Header("Size settings")]
    [Tooltip("Must be a valid output of the equation of (2^n + 1), where N is an int (e.g. 129, 257, 513, 1025)")]
    public int resolution = 513;
    public float width = 250f;
    public float heightCap = 15f;

    [Header("Perlin Noise Settings")]
    public int seed = 0;
    public float noiseScale = 150f;

    [Range(1, 10)] 
    public int octaves = 5;

    [Range(0f, 1f)] 
    public float persistence = 0.5f;
    
    [Range(1, 10)] 
    public float lacunarity = 2f; // frequency of growth per octave    

    [Header("Offset")]
    public Vector2 offset; //offset the perlin noise

    [Header("Terrain Shape")]
    public AnimationCurve heightCurve = AnimationCurve.Linear(0, 0, 1, 1);

    // Start is called once before the first execution of Update after the MonoBehaviour is created
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

                map[i, j] = height / maxPossible; // divides by the max possible to keep results within (0,1)
            }
        }

        return map;
    }
}
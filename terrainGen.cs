using System;
using System.Runtime.ExceptionServices;
using Unity.Mathematics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering;

public class terrainGen : MonoBehaviour
{
    
    [Header("Size settings")]
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
    public AnimationCurve heightCurve = AnimationCurve.Linear(0, 0, 1, 1); // changes perlin noise values (0-1) to final implemented heights (0-1), default setting is a straight line

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        generateMap();
    }

    /// Called when the script is loaded or a value is changed in the
    /// inspector (Called in the editor only).
    private void OnValidate()
    {
        resolution = Mathf.Max(33, resolution); // minimum map resolution
        octaves = Mathf.Max(1, octaves); // minimum one octave
        noiseScale = Mathf.Max(0.001f, noiseScale); // prevents a negative scale on the noiseScale
    }
    
    public void generateMap()
    {
        Terrain terrain = GetComponent<Terrain>();
        TerrainData tData = terrain.terrainData;

        float[,] height = generateHeightMap(resolution);

        tData.heightmapResolution = resolution;
        tData.size = new Vector3(width, heightCap, width);
        tData.SetHeights(0, 0, height);
    }

    float[,] generateHeightMap(int size)
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

                for (int k = 0; k < octaves; k++)
                {
                    float x = (j / noiseScale) * frequency + seed;
                    float y = (i / noiseScale) * frequency + seed;

                    height += Mathf.PerlinNoise(x, y) * amplitude;
                    maxPossible += amplitude;
                    amplitude *= persistence;
                    frequency *= lacunarity;
                }

                map[i, j] = height / maxPossible;
            }
        }

        return map;
    }
}
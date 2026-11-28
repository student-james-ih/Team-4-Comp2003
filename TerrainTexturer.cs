using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class TerrainTexturer : MonoBehaviour
{
    [Header("Component References")]
    [SerializeField] private terrainGen terrainGen;

    [SerializeField] public Terrain terrain;

    [Header("Assets")]
    [SerializeField] private Texture2D baseTexture;
    [SerializeField] private Texture2D snowTexture;
    [SerializeField] private Texture2D steepStoneTexture;

    [Header("Settings")]
    private int snowHeight = 40;

    // Calculated
    private TerrainData data;
    private float[,] heightMap;
    private float[,,] alphaMap;

    private Dictionary<string, int> layers = new();

    private int brushRadius = 1;

    public void Paint()
    {
        if (terrain == null)
        {
            Debug.LogError("Painter - no terrain assigned");
            return;
        }

        data = terrain.terrainData;
        data.terrainLayers = new TerrainLayer[0];

        AddLayer("base", baseTexture, (10, 10), true);
        AddLayer("snow", snowTexture, (10, 10));
        AddLayer("steep stone", steepStoneTexture, (10, 10));

        int heightRes = data.heightmapResolution;
        heightMap = data.GetHeights(0, 0, heightRes, heightRes);
        int alphaRes = data.alphamapResolution;
        alphaMap = data.GetAlphamaps(0, 0, alphaRes, alphaRes);

        brushRadius = Mathf.RoundToInt((float)alphaRes / heightRes * 1.1f);
        brushRadius = Mathf.Clamp(brushRadius, 1, brushRadius);

        PaintSteepRock();
        PaintSnow();

        data.SetAlphamaps(0, 0, alphaMap);
    }

    private void PaintSteepRock()
    {
        for (int i = 0; i < heightMap.GetLength(0); i++)
        {
            for (int j = 0; j < heightMap.GetLength(1); j++)
            {
                float mx = (j + terrainGen.offset.x) / terrainGen.mountainScale;
                float my = (i + terrainGen.offset.y) / terrainGen.mountainScale;

                var mtnMsk = terrainGen.mountainMask(mx, my);

                if (mtnMsk > 0.1f)
                {
                    (int x, int y) = HeightMapToAlphaMap(i, j);

                    PaintAlphaMap(layers["steep stone"], x, y, brushRadius, 1);
                }
            }
        }
    }

    private void PaintSnow()
    {
        for (int i = 0; i < heightMap.GetLength(0); i++)
        {
            for (int j = 0;  j < heightMap.GetLength(1); j++)
            {
                if (heightMap[i, j] * data.size.y > snowHeight)
                {
                    (int x, int y) = HeightMapToAlphaMap(i, j);

                    PaintAlphaMap(layers["snow"], x, y, brushRadius, 1);
                }
            }
        }
    }

    private void AddLayer(string name, Texture2D texture, (int x, int y) tileSize, bool resetLayers = false)
    {
        var layer = new TerrainLayer
        {
            diffuseTexture = texture,
            tileSize = new Vector2(tileSize.x, tileSize.y)
        };

        if (resetLayers)
        {
            data.terrainLayers = new TerrainLayer[] { layer };
            layers = new();
        }
        else
        {
            data.terrainLayers = data.terrainLayers.Append(layer).ToArray();
        }

        layers[name] = layers.Count;
    }

    private void PaintAlphaMap(int layerIndex, int x, int y, int radius, float value)
    {
        int res = data.alphamapResolution;

        for (int i = Mathf.Max(x-radius, 0); i < Mathf.Min(x+radius, res); i++)
        {
            for (int j = Mathf.Max(y-radius, 0); j < Mathf.Min(y+radius, res); j++)
            {
                alphaMap[i, j, layerIndex] = value;

                float dampening = 1 - value;

                for (int l = 0; l < layers.Count; l++)
                {
                    if (l == layerIndex) continue;

                    alphaMap[i, j, l] *= dampening;
                }
            }
        }
    }

    private (int, int) HeightMapToAlphaMap(int heightX, int heightY)
    {
        float fractionX = (float)heightX / (data.heightmapResolution - 1);
        float fractionY = (float)heightY / (data.heightmapResolution - 1);

        int alphaRes = data.alphamapResolution;
        int alphaX = (int)(fractionX * (alphaRes - 1));
        int alphaY = (int)(fractionY * (alphaRes - 1));

        alphaX = Mathf.Clamp(alphaX, 0, alphaRes - 1);
        alphaY = Mathf.Clamp(alphaY, 0, alphaRes - 1);

        return (alphaX, alphaY);
    }
}

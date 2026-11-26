using UnityEngine;

public class TerrainTexturer : MonoBehaviour
{
    public Terrain terrain;

    public Texture2D baseTexture;

    private TerrainData data;

    public void Paint()
    {
        if (terrain == null)
        {
            Debug.LogError("Painter - no terrain assigned");
        }

        data = terrain.terrainData;

        AddBaseLayer();
    }

    private void AddBaseLayer()
    {
        var layer = new TerrainLayer
        {
            diffuseTexture = baseTexture,
            tileSize = new Vector2(10, 10)
        };

        data.terrainLayers = new TerrainLayer[] { layer };
    }
}

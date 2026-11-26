using UnityEngine;

public class TerrainTools
{
    public static float GetTerrainHeight(Terrain terrain, float[,] heights, float worldX, float worldZ)
    {
        var data = terrain.terrainData;
        var pos = terrain.GetPosition();
        return GetTerrainHeightThreadSafe(
            heights, data.size.x, data.size.y, (pos.x, pos.z), worldX, worldZ);
    }

    public static float GetTerrainHeightThreadSafe(
        float[,] heights, float terrainSize, float terrainHeight,
        (float x, float y) terrainOrigin,
        float worldX, float worldZ)
    {
        int res = heights.GetLength(0);

        float localX = worldX - terrainOrigin.x;
        float resX = (localX / terrainSize) * (res - 1);
        if (resX < 0) resX = 0;

        float localZ = worldZ - terrainOrigin.y;
        float resZ = (localZ / terrainSize) * (res - 1);
        if (resZ < 0) resZ = 0;

        int xA = (int)(resX);
        int zA = (int)(resZ);

        int xB = Mathf.Min(xA + 1, res - 1);
        int zB = Mathf.Min(zA + 1, res - 1);

        // heights is indexed [z, x]

        float hAA = heights[zA, xA];
        float hBA = heights[zB, xA];
        float hAB = heights[zA, xB];
        float hBB = heights[zB, xB];

        float lerpX1 = Mathf.Lerp(hAA, hAB, resX - xA);
        float lerpX2 = Mathf.Lerp(hBA, hBB, resX - xA);
        float lerpZ = Mathf.Lerp(lerpX1, lerpX2, resZ - zA);

        return lerpZ * terrainHeight;
    }
}

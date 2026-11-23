using UnityEngine;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace CityGen
{

    // This script coordinates the city blueprint and 3d generation under one call point

    public class CityBuilder : MonoBehaviour
    {
        [SerializeField] private City3DGen gen;

        [SerializeField] private CitySettings settings;

        [SerializeField] private int tileSize = 1;

        void Awake()
        {
            gen.ApplySettings(tileSize);
        }

        public async Task Build(Terrain terrain)
        {
            var data = terrain.terrainData;
            var res = data.heightmapResolution;

            Vector3 terrOrigin = terrain.transform.position;
            Vector3 terrSize = data.size;

            Vector3 cityCentre = new Vector3(250, 0, 250);
            cityCentre.y = terrain.SampleHeight(cityCentre) + terrOrigin.y;
            Vector3 cityOrigin = cityCentre - new Vector3(
                settings.Width * tileSize * 0.5f,
                0,
                settings.Width * tileSize * 0.5f);

            // Creating non-unity vars for worker thread (unity API can only be used on main thread)
            (float x, float z) terrBoundMin = (terrOrigin.x, terrOrigin.z);
            (float x, float z) terrBoundMax = (terrOrigin.x + terrSize.x, terrOrigin.z + terrSize.z);
            (float x, float y) cityOrigin2D = (cityOrigin.x, cityOrigin.z);

            var heights = data.GetHeights(0, 0, res, res);

            // This is called by BlueprintGenerator during Generate (called on worker thread)
            settings.IsBlocked = (x, y) =>
            {
                float worldX = cityOrigin2D.x + (x + 0.5f) * tileSize;
                float worldZ = cityOrigin2D.y + (y + 0.5f) * tileSize;

                if (worldX < terrBoundMin.x ||
                    worldX > terrBoundMax.x ||
                    worldZ < terrBoundMin.z ||
                    worldZ > terrBoundMax.z)
                    return true;

                if (GetTerrainHeight(heights, terrSize.x, terrSize.y, terrBoundMin, worldX, worldZ) > 30)
                    return true;

                return false;
            };

            // Push blueprint generation onto worker thread so it doesn't lag the simulator
            CityMap blueprint = await Task.Run(() => 
                BlueprintGenerator.Generate(settings));

            gen.Consume(blueprint, cityCentre);
        }

        private static float GetTerrainHeight(
            float[,] heights, float terrainSize, float terrainHeight,
            (float x, float y) terrainOrigin, 
            float worldX, float worldZ)
        {
            int res = heights.GetLength(0);

            float localX = worldX - terrainOrigin.x;
            float resX = (localX / terrainSize) * (res-1);
            if (resX < 0) resX = 0;

            float localZ = worldZ - terrainOrigin.y;
            float resZ = (localZ / terrainSize) * (res-1);
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
}

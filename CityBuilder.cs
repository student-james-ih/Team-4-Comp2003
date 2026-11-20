using UnityEngine;

namespace CityGen
{

    // This script coordinates the city blueprint and 3d generation under one call point

    public class CityBuilder : MonoBehaviour
    {
        [SerializeField] private City3DGen gen;

        [SerializeField] private CitySettings settings;

        void Start()
        {
            var blueprint = BlueprintGenerator.Generate(settings);
            gen.Consume(blueprint);
        }
    }
}
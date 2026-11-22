using UnityEngine;
using System.Threading.Tasks;

namespace CityGen
{

    // This script coordinates the city blueprint and 3d generation under one call point

    public class CityBuilder : MonoBehaviour
    {
        [SerializeField] private City3DGen gen;

        [SerializeField] private CitySettings settings;



        async void Start()
        {
            CityMap blueprint = null;
            await Task.Run(() =>
            {
                blueprint = BlueprintGenerator.Generate(settings);
            });
            gen.Consume(blueprint);
        }
    }
}

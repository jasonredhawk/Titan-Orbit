using UnityEngine;

namespace TitanOrbit.Simulation
{
    public static class PlanetPopulationMath
    {
        /// <summary>Max population from legacy Planet.GetMaxPopulationForPlanet.</summary>
        public static int GetMaxPopulation(float planetSize, int planetLevel)
        {
            planetSize = Mathf.Max(0.25f, planetSize);
            int level = Mathf.Max(1, planetLevel);
            return Mathf.RoundToInt(planetSize * Mathf.Pow(level, 1.5f));
        }
    }
}

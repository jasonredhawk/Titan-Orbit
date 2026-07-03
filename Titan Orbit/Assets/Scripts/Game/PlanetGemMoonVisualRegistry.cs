using System.Collections.Generic;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>Lookup gem-moon visuals by planet id for ship landing presentation.</summary>
    public static class PlanetGemMoonVisualRegistry
    {
        static readonly Dictionary<int, PlanetGemMoonVisualProxy> ByPlanetId = new Dictionary<int, PlanetGemMoonVisualProxy>();

        public static void Register(PlanetGemMoonVisualProxy proxy)
        {
            if (proxy == null || proxy.PlanetId <= 0)
                return;
            ByPlanetId[proxy.PlanetId] = proxy;
        }

        public static void Unregister(PlanetGemMoonVisualProxy proxy)
        {
            if (proxy == null || proxy.PlanetId <= 0)
                return;
            if (ByPlanetId.TryGetValue(proxy.PlanetId, out var existing) && existing == proxy)
                ByPlanetId.Remove(proxy.PlanetId);
        }

        public static bool TryGetMoon(int planetId, out PlanetGemMoonVisualProxy proxy) =>
            ByPlanetId.TryGetValue(planetId, out proxy);
    }
}

using System.Collections.Generic;
using TitanOrbit.Simulation;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Static lookup of <see cref="PlanetGemMoonVisualProxy"/> by planet id — used by moon dock
    /// cinematics and UI that need moon world position without scanning the scene each frame.
    /// </summary>
    public static class PlanetGemMoonVisualRegistry
    {
        /// <summary>PlanetId → live moon proxy; updated on proxy OnEnable/OnDisable.</summary>
        static readonly Dictionary<int, PlanetGemMoonVisualProxy> ByPlanetId = new Dictionary<int, PlanetGemMoonVisualProxy>();

        /// <summary>Registers or replaces moon proxy for a planet id.</summary>
        public static void Register(PlanetGemMoonVisualProxy proxy)
        {
            // --- Register ---
            if (proxy == null || proxy.PlanetId <= 0)
                return;
            ByPlanetId[proxy.PlanetId] = proxy;
        }

        public static void Unregister(PlanetGemMoonVisualProxy proxy)
        {
            // --- Unregister ---
            if (proxy == null || proxy.PlanetId <= 0)
                return;
            if (ByPlanetId.TryGetValue(proxy.PlanetId, out var existing) && existing == proxy)
                ByPlanetId.Remove(proxy.PlanetId);
        }

        /// <summary>How many moon visuals are currently registered.</summary>
        public static int Count => ByPlanetId.Count;

        public static bool TryGetMoon(int planetId, out PlanetGemMoonVisualProxy proxy) =>
            ByPlanetId.TryGetValue(planetId, out proxy);
    }
}

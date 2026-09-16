using System.Collections.Generic;
using TitanOrbit.Core;
using TitanOrbit.Generation;
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

        /// <summary>
        /// Live moon pose for the planet's gem moon. Registry lookup only — no ECS gather.
        /// </summary>
        public static bool TryGetMoonWorldPosition(int planetId, out Vector3 worldPos)
        {
            worldPos = default;
            if (planetId <= 0 || !TryGetMoon(planetId, out PlanetGemMoonVisualProxy moon) || moon == null)
                return false;
            worldPos = moon.MoonWorldPosition;
            return true;
        }

        /// <summary>
        /// Closest registered gem moon to <paramref name="aim"/> on the torus.
        /// Optional team / home filters match comms "Orange Moon". Falls back to any moon
        /// when the filter matches none. Map size from <see cref="ToroidalMap"/>.
        /// </summary>
        public static bool TryFindClosestMoon(
            Vector3 aim,
            TeamId teamFilter,
            bool homeOnly,
            out int planetId,
            out Vector3 worldPos,
            bool allowFallback = true,
            TeamId excludeTeam = TeamId.None)
        {
            if (TryFindClosestMoonFiltered(aim, teamFilter, homeOnly, excludeTeam, out planetId, out worldPos))
                return true;
            if (allowFallback && excludeTeam == TeamId.None && (teamFilter != TeamId.None || homeOnly))
                return TryFindClosestMoonFiltered(aim, TeamId.None, homeOnly: false, excludeTeam, out planetId, out worldPos);
            return false;
        }

        static bool TryFindClosestMoonFiltered(
            Vector3 aim,
            TeamId teamFilter,
            bool homeOnly,
            TeamId excludeTeam,
            out int planetId,
            out Vector3 worldPos)
        {
            planetId = 0;
            worldPos = default;
            float best = float.MaxValue;
            bool found = false;

            foreach (var kv in ByPlanetId)
            {
                PlanetGemMoonVisualProxy moon = kv.Value;
                if (moon == null || moon.PlanetId <= 0)
                    continue;
                if (homeOnly && !moon.IsHome)
                    continue;
                if (teamFilter != TeamId.None && moon.Team != teamFilter)
                    continue;
                if (excludeTeam != TeamId.None && moon.Team == excludeTeam)
                    continue;

                Vector3 pos = moon.MoonWorldPosition;
                float d = ToroidalMap.ToroidalDistance(aim, pos);
                if (d >= best)
                    continue;

                best = d;
                planetId = moon.PlanetId;
                worldPos = pos;
                found = true;
            }

            return found;
        }
    }
}

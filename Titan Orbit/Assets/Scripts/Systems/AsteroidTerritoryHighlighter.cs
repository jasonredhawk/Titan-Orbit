using Unity.Netcode;
using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Periodically re-evaluates whether each asteroid is neutral or inside a team's moving triangle
    /// (gem-moon vertices). Server syncs <see cref="Asteroid.TerritoryTeam"/>; clients tint from that value.
    /// </summary>
    public class AsteroidTerritoryHighlighter : MonoBehaviour
    {
        [SerializeField] private float refreshInterval = 0.25f;
        private float lastRefresh = -999f;

        private void Update()
        {
            if (Time.time - lastRefresh < refreshInterval)
                return;

            lastRefresh = Time.time;

            var nm = NetworkManager.Singleton;
            bool isServer = nm == null || nm.IsServer;
            if (!isServer)
                return;

            var conn = PlanetConnectionSystem.Instance;
            var asteroids = Asteroid.AllAsteroids;
            if (asteroids == null || asteroids.Count == 0)
                return;

            bool hasTriangles = conn != null && conn.CurrentTriangles != null && conn.CurrentTriangles.Count > 0;

            foreach (var asteroid in asteroids)
            {
                if (asteroid == null || asteroid.IsDestroyed)
                    continue;

                Vector3 canonicalPos = ToroidalMap.WrapPosition(asteroid.transform.position);
                TeamManager.Team team = hasTriangles
                    ? conn.GetTeamAtPosition(canonicalPos)
                    : TeamManager.Team.None;
                asteroid.ServerRefreshTerritoryTeam(team);
            }
        }
    }
}

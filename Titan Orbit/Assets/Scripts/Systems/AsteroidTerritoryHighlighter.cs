using UnityEngine;
using TitanOrbit.Core;
using TitanOrbit.Entities;
using TitanOrbit.Generation;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Periodically tints asteroids that are inside team triangles, using PlanetConnectionSystem data.
    /// Uses canonical (wrapped) position so asteroids stay correct on a toroidal map when display copies move.
    /// Visual-only; does not affect gameplay values.
    /// </summary>
    public class AsteroidTerritoryHighlighter : MonoBehaviour
    {
        [SerializeField] private float refreshInterval = 1f;
        private float lastRefresh = -999f;

        private void Update()
        {
            if (Time.time - lastRefresh < refreshInterval)
                return;

            lastRefresh = Time.time;
            var conn = PlanetConnectionSystem.Instance;
            if (conn == null)
                return;

            Asteroid[] asteroids = FindObjectsOfType<Asteroid>();
            if (asteroids == null || asteroids.Length == 0)
                return;

            foreach (var asteroid in asteroids)
            {
                if (asteroid == null || asteroid.IsDestroyed)
                    continue;

                Vector3 canonicalPos = ToroidalMap.WrapPosition(asteroid.transform.position);
                TeamManager.Team team = conn.GetTeamAtPosition(canonicalPos);
                asteroid.SetTerritoryHighlight(team);
            }
        }
    }
}


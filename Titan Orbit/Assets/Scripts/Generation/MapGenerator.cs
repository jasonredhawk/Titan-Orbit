using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Generates procedural maps with seed-based randomization
    /// </summary>
    public class MapGenerator : NetworkBehaviour
    {
        [Header("Map Settings")]
        [SerializeField] private int seed = 0;
        [SerializeField] private float mapWidth = 1000f;
        [SerializeField] private float mapHeight = 1000f;

        [Header("Home Planet Settings")]
        [SerializeField] private GameObject homePlanetPrefab;
        [SerializeField] private float homePlanetDistance = 300f;

        [Header("Neutral Planet Settings")]
        [SerializeField] private GameObject planetPrefab;
        [SerializeField] private int numberOfPlanets = 20;
        [SerializeField] private float minPlanetSize = 0.5f;
        [SerializeField] private float maxPlanetSize = 2f;

        [Header("Asteroid Settings")]
        [SerializeField] private GameObject asteroidPrefab;
        [SerializeField] private int numberOfAsteroids = 100;
        [SerializeField] private float minAsteroidSize = 0.3f;
        [SerializeField] private float maxAsteroidSize = 1.5f;

        private System.Random random;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                GenerateMap();
            }
        }

        private void GenerateMap()
        {
            // Set seed
            if (seed == 0)
            {
                seed = System.Environment.TickCount;
            }
            random = new System.Random(seed);

            // Set map size for toroidal system
            ToroidalMap.SetMapSize(mapWidth, mapHeight);

            // Generate home planets (equilateral triangle)
            GenerateHomePlanets();

            // Generate neutral planets
            GenerateNeutralPlanets();

            // Generate asteroids
            GenerateAsteroids();
        }

        private void GenerateHomePlanets()
        {
            if (homePlanetPrefab == null) return;

            // Place home planets in equilateral triangle
            Vector3[] homePlanetPositions = new Vector3[3];
            
            // Center of map
            Vector3 center = Vector3.zero;
            
            // Equilateral triangle positions
            float angleStep = 120f * Mathf.Deg2Rad;
            for (int i = 0; i < 3; i++)
            {
                float angle = i * angleStep;
                Vector3 position = center + new Vector3(
                    Mathf.Cos(angle) * homePlanetDistance,
                    0f,
                    Mathf.Sin(angle) * homePlanetDistance
                );

                GameObject homePlanetObj = Instantiate(homePlanetPrefab, position, Quaternion.identity);
                NetworkObject netObj = homePlanetObj.GetComponent<NetworkObject>();
                if (netObj != null)
                {
                    netObj.Spawn();
                }

                HomePlanet homePlanet = homePlanetObj.GetComponent<HomePlanet>();
                if (homePlanet != null)
                {
                    // Assign teams
                    TeamManager.Team team = (TeamManager.Team)(i + 1); // TeamA, TeamB, TeamC
                    // Note: Team assignment would need to be set via NetworkVariable or ServerRpc
                }
            }
        }

        private void GenerateNeutralPlanets()
        {
            if (planetPrefab == null) return;

            for (int i = 0; i < numberOfPlanets; i++)
            {
                Vector3 position = GetRandomPosition();
                float size = GetRandomFloat(minPlanetSize, maxPlanetSize);

                GameObject planetObj = Instantiate(planetPrefab, position, Quaternion.identity);
                planetObj.transform.localScale = Vector3.one * size;

                Planet planet = planetObj.GetComponent<Planet>();
                if (planet != null)
                {
                    // Size is set via transform, planet will use it
                }

                NetworkObject netObj = planetObj.GetComponent<NetworkObject>();
                if (netObj != null)
                {
                    netObj.Spawn();
                }
            }
        }

        private void GenerateAsteroids()
        {
            if (asteroidPrefab == null) return;

            for (int i = 0; i < numberOfAsteroids; i++)
            {
                Vector3 position = GetRandomPosition();
                float size = GetRandomFloat(minAsteroidSize, maxAsteroidSize);

                GameObject asteroidObj = Instantiate(asteroidPrefab, position, Quaternion.identity);
                asteroidObj.transform.localScale = Vector3.one * size;

                Asteroid asteroid = asteroidObj.GetComponent<Asteroid>();
                if (asteroid != null)
                {
                    // Size is set via transform
                }

                NetworkObject netObj = asteroidObj.GetComponent<NetworkObject>();
                if (netObj != null)
                {
                    netObj.Spawn();
                }
            }
        }

        private Vector3 GetRandomPosition()
        {
            // Avoid spawning too close to home planets
            Vector3 position;
            int attempts = 0;
            do
            {
                position = new Vector3(
                    GetRandomFloat(-mapWidth / 2f, mapWidth / 2f),
                    0f,
                    GetRandomFloat(-mapHeight / 2f, mapHeight / 2f)
                );
                attempts++;
            } while (IsTooCloseToHomePlanets(position) && attempts < 50);

            return position;
        }

        private bool IsTooCloseToHomePlanets(Vector3 position)
        {
            float minDistance = homePlanetDistance * 0.5f;
            
            // Check distance to each home planet position
            for (int i = 0; i < 3; i++)
            {
                float angle = i * 120f * Mathf.Deg2Rad;
                Vector3 homePos = new Vector3(
                    Mathf.Cos(angle) * homePlanetDistance,
                    0f,
                    Mathf.Sin(angle) * homePlanetDistance
                );

                if (Vector3.Distance(position, homePos) < minDistance)
                {
                    return true;
                }
            }

            return false;
        }

        private float GetRandomFloat(float min, float max)
        {
            return min + (float)random.NextDouble() * (max - min);
        }
    }
}

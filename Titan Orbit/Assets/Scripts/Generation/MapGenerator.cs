using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
using TitanOrbit.Core;

namespace TitanOrbit.Generation
{
    /// <summary>
    /// Generates procedural maps with seed-based randomization
    /// Uses parent containers for organization. Asteroids are clustered and never overlap.
    /// </summary>
    public class MapGenerator : NetworkBehaviour
    {
        [Header("Map Settings")]
        [SerializeField] private int seed = 0;
        [SerializeField] private float mapWidth = 300f;
        [SerializeField] private float mapHeight = 300f;

        [Header("Home Planet Settings")]
        [SerializeField] private GameObject homePlanetPrefab;
        [SerializeField] private float homePlanetDistance = 80f;

        [Header("Neutral Planet Settings")]
        [SerializeField] private GameObject planetPrefab;
        [SerializeField] private int numberOfPlanets = 20;
        [SerializeField] private float minPlanetSize = 0.5f;
        [SerializeField] private float maxPlanetSize = 2f;

        [Header("Asteroid Settings")]
        [SerializeField] private GameObject asteroidPrefab;
        [SerializeField] private int numberOfAsteroids = 400;
        [SerializeField] private int asteroidClusters = 25;
        [SerializeField] private float minAsteroidSize = 0.3f;
        [SerializeField] private float maxAsteroidSize = 1.5f;
        [SerializeField] private float minAsteroidSpacing = 1.5f;

        [Header("Parent Containers")]
        [SerializeField] private Transform planetsParent;
        [SerializeField] private Transform asteroidsParent;
        [SerializeField] private Transform homePlanetsParent;

        private System.Random random;
        private System.Collections.Generic.List<Vector3> asteroidPositions = new System.Collections.Generic.List<Vector3>();
        private System.Collections.Generic.List<Vector3> planetPositions = new System.Collections.Generic.List<Vector3>();

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                EnsureParents();
                GenerateMap();
            }
        }

        private void EnsureParents()
        {
            if (planetsParent == null)
            {
                var go = new GameObject("Planets");
                go.transform.SetParent(transform);
                planetsParent = go.transform;
            }
            if (asteroidsParent == null)
            {
                var go = new GameObject("Asteroids");
                go.transform.SetParent(transform);
                asteroidsParent = go.transform;
            }
            if (homePlanetsParent == null)
            {
                var go = new GameObject("HomePlanets");
                go.transform.SetParent(transform);
                homePlanetsParent = go.transform;
            }
        }

        private void GenerateMap()
        {
            if (seed == 0) seed = System.Environment.TickCount;
            random = new System.Random(seed);

            ToroidalMap.SetMapSize(mapWidth, mapHeight);
            asteroidPositions.Clear();
            planetPositions.Clear();

            GenerateHomePlanets();
            GenerateNeutralPlanets();
            GenerateAsteroids();
        }

        private void GenerateHomePlanets()
        {
            if (homePlanetPrefab == null) return;

            float angleStep = 120f * Mathf.Deg2Rad;
            for (int i = 0; i < 3; i++)
            {
                float angle = i * angleStep;
                Vector3 position = new Vector3(
                    Mathf.Cos(angle) * homePlanetDistance,
                    0f,
                    Mathf.Sin(angle) * homePlanetDistance
                );

                GameObject homePlanetObj = Instantiate(homePlanetPrefab, position, Quaternion.identity);
                NetworkObject netObj = homePlanetObj.GetComponent<NetworkObject>();
                if (netObj != null) netObj.Spawn();
                // Note: Cannot parent NetworkObjects to non-NetworkObject parents in Netcode

                planetPositions.Add(position);
            }
        }

        private void GenerateNeutralPlanets()
        {
            if (planetPrefab == null) return;

            float minDist = 8f;
            for (int i = 0; i < numberOfPlanets; i++)
            {
                Vector3 position = GetRandomPositionAvoiding(minDist, planetPositions, asteroidPositions);
                planetPositions.Add(position);
                float size = GetRandomFloat(minPlanetSize, maxPlanetSize);

                GameObject planetObj = Instantiate(planetPrefab, position, Quaternion.identity);
                planetObj.transform.localScale = Vector3.one * size;
                NetworkObject netObj = planetObj.GetComponent<NetworkObject>();
                if (netObj != null) netObj.Spawn();
            }
        }

        private void GenerateAsteroids()
        {
            if (asteroidPrefab == null) return;

            // Create cluster centers
            Vector3[] clusterCenters = new Vector3[asteroidClusters];
            for (int c = 0; c < asteroidClusters; c++)
            {
                clusterCenters[c] = GetRandomPositionAvoiding(15f, planetPositions, new System.Collections.Generic.List<Vector3>());
            }

            int perCluster = Mathf.CeilToInt((float)numberOfAsteroids / asteroidClusters);
            for (int c = 0; c < asteroidClusters; c++)
            {
                Vector3 center = clusterCenters[c];
                for (int i = 0; i < perCluster && asteroidPositions.Count < numberOfAsteroids; i++)
                {
                    Vector3 position = GetPositionInCluster(center);
                    if (IsTooCloseToAny(position, minAsteroidSpacing, asteroidPositions)) continue;
                    if (IsTooCloseToAny(position, 5f, planetPositions)) continue;

                    asteroidPositions.Add(position);
                    float size = GetRandomFloat(minAsteroidSize, maxAsteroidSize);
                    Vector3 scale = new Vector3(
                        size * (0.8f + (float)random.NextDouble() * 0.4f),
                        size * (0.9f + (float)random.NextDouble() * 0.2f),
                        size * (0.85f + (float)random.NextDouble() * 0.3f)
                    );

                    GameObject asteroidObj = Instantiate(asteroidPrefab, position, Quaternion.Euler(0, GetRandomFloat(0, 360f), 0));
                    asteroidObj.transform.localScale = scale;
                    NetworkObject netObj = asteroidObj.GetComponent<NetworkObject>();
                    if (netObj != null) netObj.Spawn();
                }
            }
        }

        private Vector3 GetPositionInCluster(Vector3 center)
        {
            float radius = 12f + (float)random.NextDouble() * 8f;
            float angle = (float)random.NextDouble() * Mathf.PI * 2f;
            return center + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);
        }

        private bool IsTooCloseToAny(Vector3 pos, float minDist, System.Collections.Generic.List<Vector3> positions)
        {
            foreach (var p in positions)
            {
                if (Vector3.Distance(pos, p) < minDist) return true;
            }
            return false;
        }

        private Vector3 GetRandomPositionAvoiding(float minDist, System.Collections.Generic.List<Vector3> avoid1, System.Collections.Generic.List<Vector3> avoid2)
        {
            for (int attempts = 0; attempts < 100; attempts++)
            {
                Vector3 pos = new Vector3(
                    GetRandomFloat(-mapWidth / 2f, mapWidth / 2f),
                    0f,
                    GetRandomFloat(-mapHeight / 2f, mapHeight / 2f)
                );
                if (!IsTooCloseToHomePlanets(pos) && !IsTooCloseToAny(pos, minDist, avoid1) && !IsTooCloseToAny(pos, minDist, avoid2))
                    return pos;
            }
            return new Vector3(GetRandomFloat(-mapWidth / 2f, mapWidth / 2f), 0, GetRandomFloat(-mapHeight / 2f, mapHeight / 2f));
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

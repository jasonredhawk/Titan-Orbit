using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using TitanOrbit.Entities;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Respawns asteroids by despawning and spawning fresh instances - avoids state corruption.
    /// </summary>
    public class AsteroidRespawnManager : NetworkBehaviour
    {
        public static AsteroidRespawnManager Instance { get; private set; }

        [SerializeField] private GameObject asteroidPrefab;
        [SerializeField] private float defaultRespawnTime = 30f;

        private struct PendingRespawn
        {
            public Vector3 position;
            public Vector3 scale;
            public float respawnAt;
        }
        private List<PendingRespawn> pending = new List<PendingRespawn>();

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        public void ScheduleRespawn(Vector3 position, Vector3 scale, float respawnTime = 30f)
        {
            if (asteroidPrefab == null)
            {
                Debug.LogWarning("AsteroidRespawnManager: asteroidPrefab is null. Assign it in the Inspector or ensure MapGenerator sets it.");
                return;
            }
            pending.Add(new PendingRespawn
            {
                position = position,
                scale = scale,
                respawnAt = Time.time + respawnTime
            });
        }

        private void Update()
        {
            if (!IsServer) return;
            if (asteroidPrefab == null) return;
            if (!NetworkManager.Singleton || !NetworkManager.Singleton.IsListening) return;

            float now = Time.time;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (now >= pending[i].respawnAt)
                {
                    var p = pending[i];
                    SpawnAsteroid(p.position, p.scale);
                    pending.RemoveAt(i);
                }
            }
        }

        private void SpawnAsteroid(Vector3 position, Vector3 scale)
        {
            // Ensure Y position is locked to 0
            position.y = 0f;
            var obj = Instantiate(asteroidPrefab, position, Quaternion.identity);
            if (obj == null) return;
            obj.transform.localScale = scale;
            var no = obj.GetComponent<NetworkObject>();
            if (no != null)
            {
                no.Spawn();
            }
            else
            {
                Debug.LogWarning("AsteroidRespawnManager: Spawned asteroid has no NetworkObject.");
            }
        }

        public void SetPrefab(GameObject prefab) => asteroidPrefab = prefab;
    }
}

using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Recycles people transport NetworkObjects to avoid Instantiate/Despawn during orbit load/unload.
    /// Pre-spawns a fixed number and toggles active/in pool via <see cref="PeopleTransportProjectile.IsInPool"/>; no Despawn.
    /// Attach to same GameObject as <see cref="GemSpawner"/> (or ensure it runs on server).
    /// </summary>
    public class PeoplePool : MonoBehaviour
    {
        public static PeoplePool Instance { get; private set; }

        [SerializeField] private GameObject peopleTransportPrefab;
        [SerializeField] private int poolSize = 40;

        private readonly List<PeopleTransportProjectile> pool = new List<PeopleTransportProjectile>();
        private bool poolCreated;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(this);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (poolCreated) return;
            bool havePrefab = peopleTransportPrefab != null
                || (GemSpawner.Instance != null && GemSpawner.Instance.GetRuntimePeopleTransportPrefabForPool() != null);
            if (!havePrefab || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || !NetworkManager.Singleton.IsServer)
                return;

            CreatePool();
            poolCreated = true;
        }

        private void CreatePool()
        {
            pool.Clear();
            GameObject prefabToUse = peopleTransportPrefab;
            if (GemSpawner.Instance != null)
            {
                GameObject fromSpawner = GemSpawner.Instance.GetRuntimePeopleTransportPrefabForPool();
                if (fromSpawner != null) prefabToUse = fromSpawner;
            }
            if (prefabToUse == null) return;

            for (int i = 0; i < poolSize; i++)
            {
                GameObject go = Instantiate(prefabToUse, Vector3.zero, Quaternion.identity);
                NetworkObject no = go.GetComponent<NetworkObject>();
                if (no == null) { Destroy(go); continue; }
                no.Spawn();
                PeopleTransportProjectile projectile = go.GetComponent<PeopleTransportProjectile>();
                if (projectile != null)
                {
                    projectile.ServerReturnToPool();
                    pool.Add(projectile);
                }
            }
        }

        /// <summary>Returns an available projectile from the pool, or null if none. Caller must Initialize, then ServerActivateFromPool, then ServerFinishPooledSpawn.</summary>
        public PeopleTransportProjectile GetNext()
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return null;
            for (int i = 0; i < pool.Count; i++)
            {
                PeopleTransportProjectile p = pool[i];
                if (p != null && p.IsInPool) return p;
            }
            return null;
        }

        /// <summary>Returns true if the projectile was returned to the pool. False if not from this pool or pool not ready.</summary>
        public bool ReturnToPool(PeopleTransportProjectile projectile)
        {
            if (projectile == null || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return false;
            if (!pool.Contains(projectile)) return false;
            projectile.ServerReturnToPool();
            return true;
        }

        /// <summary>For <see cref="GemSpawner"/> to supply the same resolved prefab used for spawning.</summary>
        public void SetPrefab(GameObject prefab)
        {
            if (peopleTransportPrefab == null) peopleTransportPrefab = prefab;
        }
    }

}

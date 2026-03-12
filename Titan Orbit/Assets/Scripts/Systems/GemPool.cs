using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Recycles gem NetworkObjects to avoid Instantiate/Despawn when many asteroids are destroyed.
    /// Pre-spawns a fixed number of gems and toggles them active/in pool via isInPool; no Despawn.
    /// Attach to same GameObject as GemSpawner (or ensure it runs on server).
    /// </summary>
    public class GemPool : MonoBehaviour
    {
        public static GemPool Instance { get; private set; }

        [SerializeField] private GameObject gemPrefab;
        [SerializeField] private int poolSize = 60;

        private readonly List<Gem> pool = new List<Gem>();
        private bool poolCreated;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (poolCreated) return;
            if (gemPrefab == null || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening || !NetworkManager.Singleton.IsServer)
                return;

            CreatePool();
            poolCreated = true;
        }

        private void CreatePool()
        {
            pool.Clear();
            for (int i = 0; i < poolSize; i++)
            {
                GameObject go = Instantiate(gemPrefab, Vector3.zero, Quaternion.identity);
                NetworkObject no = go.GetComponent<NetworkObject>();
                if (no == null) { Destroy(go); continue; }
                no.Spawn();
                Gem gem = go.GetComponent<Gem>();
                if (gem != null)
                {
                    gem.ServerReturnToPool();
                    pool.Add(gem);
                }
            }
        }

        /// <summary>Returns an available gem from the pool, or null if none. Caller must set position, velocity, Initialize, then ServerActivateFromPool.</summary>
        public Gem GetNext()
        {
            if (!NetworkManager.Singleton.IsServer) return null;
            for (int i = 0; i < pool.Count; i++)
            {
                Gem g = pool[i];
                if (g != null && g.IsInPool) return g;
            }
            return null;
        }

        /// <summary>Returns true if the gem was returned to the pool (recycled). False if not from this pool or pool not ready.</summary>
        public bool ReturnToPool(Gem gem)
        {
            if (gem == null || !NetworkManager.Singleton.IsServer) return false;
            if (!pool.Contains(gem)) return false;
            gem.ServerReturnToPool();
            return true;
        }

        /// <summary>For external spawners that need the prefab (e.g. GemSpawner uses pool first, else needs prefab to Instantiate).</summary>
        public void SetPrefab(GameObject prefab)
        {
            if (gemPrefab == null) gemPrefab = prefab;
        }
    }
}

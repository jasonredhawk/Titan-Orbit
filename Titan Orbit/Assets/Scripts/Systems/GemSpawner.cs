using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Entities;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TitanOrbit.Systems
{
    /// <summary>
    /// Spawns gem pickups when asteroids are destroyed.
    /// Gems explode outward, slow down, and stop for a visual effect.
    /// </summary>
    public class GemSpawner : NetworkBehaviour
    {
        public static GemSpawner Instance { get; private set; }

        [SerializeField] private GameObject gemPrefab;
        [SerializeField] private float explosionSpeed = 4f;
        [SerializeField] private float explosionRadius = 1.5f;

        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private GameObject GetGemPrefab()
        {
            if (gemPrefab != null) return gemPrefab;
#if UNITY_EDITOR
            gemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Gem.prefab");
            return gemPrefab;
#else
            return null;
#endif
        }

        [ServerRpc(RequireOwnership = false)]
        public void SpawnGemsServerRpc(Vector3 asteroidCenter, float totalValue)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null) return;

            int count = Mathf.Max(1, Mathf.CeilToInt(totalValue / 10f));
            float valuePerGem = totalValue / count;

            for (int i = 0; i < count; i++)
            {
                // Random direction in XZ plane, slightly outward
                Vector2 dir2 = Random.insideUnitCircle.normalized;
                if (dir2.sqrMagnitude < 0.01f) dir2 = Vector2.up;
                Vector3 dir = new Vector3(dir2.x, 0f, dir2.y);
                Vector3 pos = asteroidCenter + dir * explosionRadius * Random.Range(0.3f, 1f);

                GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
                Rigidbody rb = gemObj.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.linearVelocity = dir * explosionSpeed * Random.Range(0.8f, 1.2f);
                }

                NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
                if (netObj != null)
                {
                    netObj.Spawn();
                    Gem gem = gemObj.GetComponent<Gem>();
                    if (gem != null) gem.Initialize(valuePerGem);
                }
            }
        }
    }
}

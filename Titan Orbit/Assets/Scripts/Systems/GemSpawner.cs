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
        public void SpawnGemsServerRpc(Vector3 asteroidCenter, float totalValue, float asteroidSize = 1f)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null) return;

            // Define gem size categories
            // Small gems: 0.5x size, 0.5x value multiplier
            // Medium gems: 1.0x size, 1.0x value multiplier  
            // Large gems: 1.5x size, 1.5x value multiplier
            
            // Calculate gem distribution based on asteroid size/value
            // Smaller asteroids -> more small gems, fewer large gems
            // Larger asteroids -> more large gems, fewer small gems
            
            float normalizedSize = Mathf.Clamp01((asteroidSize - 0.3f) / 2f); // Normalize asteroid size (assuming 0.3-2.3 range)
            
            // Determine gem size distribution probabilities
            // Small asteroids (low normalizedSize) -> high chance of small gems
            // Large asteroids (high normalizedSize) -> high chance of large gems
            float smallGemChance = 1f - normalizedSize * 0.6f; // 100% -> 40% as size increases
            float largeGemChance = normalizedSize * 0.6f; // 0% -> 60% as size increases
            float mediumGemChance = 1f - smallGemChance - largeGemChance; // Remaining
            
            // Calculate target gem count (fewer gems for larger sizes, but each gem is worth more)
            int baseCount = Mathf.Max(1, Mathf.CeilToInt(totalValue / 10f));
            int gemCount = baseCount;
            
            // Distribute gems across size categories
            int smallCount = Mathf.RoundToInt(gemCount * smallGemChance);
            int largeCount = Mathf.RoundToInt(gemCount * largeGemChance);
            int mediumCount = gemCount - smallCount - largeCount;
            
            // Ensure we have at least one gem
            if (smallCount + mediumCount + largeCount == 0)
            {
                mediumCount = 1;
            }
            
            // Calculate value per gem size category
            // We need: smallCount * (valuePerSmallGem) + mediumCount * (valuePerMediumGem) + largeCount * (valuePerLargeGem) = totalValue
            // Where: valuePerSmallGem = baseValue * 0.5, valuePerMediumGem = baseValue * 1.0, valuePerLargeGem = baseValue * 1.5
            
            float totalSizeMultiplier = smallCount * 0.5f + mediumCount * 1.0f + largeCount * 1.5f;
            float baseValuePerGem = totalSizeMultiplier > 0 ? totalValue / totalSizeMultiplier : totalValue;
            
            // Spawn small gems
            for (int i = 0; i < smallCount; i++)
            {
                SpawnGem(prefab, asteroidCenter, baseValuePerGem * 0.5f, 0.5f);
            }
            
            // Spawn medium gems
            for (int i = 0; i < mediumCount; i++)
            {
                SpawnGem(prefab, asteroidCenter, baseValuePerGem * 1.0f, 1.0f);
            }
            
            // Spawn large gems
            for (int i = 0; i < largeCount; i++)
            {
                SpawnGem(prefab, asteroidCenter, baseValuePerGem * 1.5f, 1.5f);
            }
        }
        
        private void SpawnGem(GameObject prefab, Vector3 asteroidCenter, float gemValue, float sizeMultiplier)
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
                if (gem != null) gem.Initialize(gemValue, sizeMultiplier);
            }
        }
    }
}

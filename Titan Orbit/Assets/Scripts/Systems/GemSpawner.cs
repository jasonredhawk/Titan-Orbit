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
        public void SpawnGemsServerRpc(Vector3 asteroidCenter, float totalValue, float asteroidSize = 1f, float asteroidPhysicalSize = 0.5f)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null) return;

            // asteroidSize is normalized (1-20); asteroidPhysicalSize is world scale (~0.3-1.5) for gem visual scale
            // totalValue should be between 1 and 50 based on normalized size
            
            // Smallest asteroids (size ~1, totalValue ~1) produce exactly 1 tiny gem
            if (asteroidSize <= 1.5f && totalValue <= 2f)
            {
                SpawnGem(prefab, asteroidCenter, Mathf.Max(1f, totalValue), 0.3f, asteroidPhysicalSize);
                return;
            }

            // Normalize asteroid size to 0-1 range for distribution logic
            float normalizedSize = Mathf.Clamp01((asteroidSize - 1f) / (20f - 1f));
            
            // Determine gem count and distribution based on asteroid size
            // Small asteroids: multiple small gems
            // Medium asteroids: mix of small/medium gems
            // Large asteroids: fewer large gems (max value 50 per gem)
            
            int gemCount;
            float minGemValue, maxGemValue;
            
            if (normalizedSize < 0.3f) // Small asteroids (size 1-6.7)
            {
                gemCount = Random.Range(2, 5);
                minGemValue = 1f;
                maxGemValue = 5f;
            }
            else if (normalizedSize < 0.7f) // Medium asteroids (size 6.7-14.3)
            {
                gemCount = Random.Range(2, 5);
                minGemValue = 1f;
                maxGemValue = 25f;
            }
            else // Large asteroids (size 14.3-20)
            {
                // Large asteroids produce fewer, more valuable gems
                // Max gem value is 50
                if (normalizedSize >= 0.9f) // Very large asteroids (size 18.1-20)
                {
                    gemCount = Random.Range(1, 4);
                    minGemValue = 20f;
                    maxGemValue = 50f; // Max value per gem
                }
                else // Large but not largest (size 14.3-18.1)
                {
                    gemCount = Random.Range(2, 4);
                    minGemValue = 10f;
                    maxGemValue = 40f;
                }
            }
            
            // Distribute totalValue across gems
            float remainingValue = totalValue;
            for (int i = 0; i < gemCount; i++)
            {
                bool isLast = (i == gemCount - 1);
                
                float gemValue;
                if (isLast)
                {
                    // Last gem gets remaining value, clamped to max 50
                    gemValue = Mathf.Clamp(remainingValue, minGemValue, Mathf.Min(maxGemValue, 50f));
                }
                else
                {
                    // Distribute value proportionally
                    float avgValuePerGem = remainingValue / (gemCount - i);
                    gemValue = Mathf.Clamp(avgValuePerGem * Random.Range(0.7f, 1.3f), minGemValue, Mathf.Min(maxGemValue, 50f));
                }
                
                // Clamp gem value to 1-50 range (hard cap at 50)
                gemValue = Mathf.Clamp(gemValue, 1f, 50f);
                
                // Calculate size multiplier based on value
                // Value 1-10: size 0.3-0.6
                // Value 11-25: size 0.6-1.0
                // Value 26-40: size 1.0-1.4
                // Value 41-50: size 1.4-2.0
                float sizeMultiplier;
                if (gemValue <= 10f)
                {
                    sizeMultiplier = Mathf.Lerp(0.3f, 0.6f, gemValue / 10f);
                }
                else if (gemValue <= 25f)
                {
                    sizeMultiplier = Mathf.Lerp(0.6f, 1.0f, (gemValue - 10f) / 15f);
                }
                else if (gemValue <= 40f)
                {
                    sizeMultiplier = Mathf.Lerp(1.0f, 1.4f, (gemValue - 25f) / 15f);
                }
                else
                {
                    sizeMultiplier = Mathf.Lerp(1.4f, 2.0f, (gemValue - 40f) / 10f);
                }
                
                // Add some random variation to size
                sizeMultiplier *= Random.Range(0.9f, 1.1f);
                
                SpawnGem(prefab, asteroidCenter, gemValue, sizeMultiplier, asteroidPhysicalSize);
                remainingValue -= gemValue;
                
                if (remainingValue <= 0) break;
            }
        }
        
        /// <summary>Spawns gems expelled from a ship when bullets hit after health is zero. Victim ship cannot collect for 3 sec.</summary>
        [ServerRpc(RequireOwnership = false)]
        public void SpawnGemsFromShipServerRpc(Vector3 shipPosition, float totalValue, ulong expelledByShipId)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null || totalValue <= 0f) return;

            // Spawn as one or a few gems (simpler than asteroid distribution)
            float remaining = totalValue;
            int maxGems = Mathf.Min(5, Mathf.CeilToInt(totalValue / 2f));
            if (maxGems < 1) maxGems = 1;
            for (int i = 0; i < maxGems && remaining > 0.1f; i++)
            {
                float gemValue = (i == maxGems - 1) ? remaining : Mathf.Min(remaining, Random.Range(2f, Mathf.Min(remaining, 25f)));
                gemValue = Mathf.Clamp(gemValue, 1f, 50f);
                float sizeMult = Mathf.Lerp(0.4f, 1.2f, Mathf.Clamp01(gemValue / 25f));
                SpawnGemFromShip(prefab, shipPosition, gemValue, sizeMult, expelledByShipId);
                remaining -= gemValue;
            }
        }

        private void SpawnGemFromShip(GameObject prefab, Vector3 shipCenter, float gemValue, float sizeMultiplier, ulong expelledByShipId)
        {
            Vector2 dir2 = Random.insideUnitCircle.normalized;
            if (dir2.sqrMagnitude < 0.01f) dir2 = Vector2.up;
            Vector3 dir = new Vector3(dir2.x, 0f, dir2.y);
            Vector3 pos = shipCenter + dir * explosionRadius * Random.Range(0.3f, 1f);

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
                if (gem != null) gem.InitializeFromShip(gemValue, sizeMultiplier, expelledByShipId);
            }
        }

        private void SpawnGem(GameObject prefab, Vector3 asteroidCenter, float gemValue, float sizeMultiplier, float asteroidPhysicalSize)
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
                if (gem != null) gem.Initialize(gemValue, sizeMultiplier, asteroidPhysicalSize);
            }
        }
    }
}

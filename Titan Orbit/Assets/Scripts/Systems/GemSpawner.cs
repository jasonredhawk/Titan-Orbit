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

            // asteroidSize and totalValue both 1-50 (volume-proportional); asteroidPhysicalSize is world scale for gem cap
            if (asteroidSize <= 1.5f && totalValue <= 2f)
            {
                SpawnGem(prefab, asteroidCenter, Mathf.Max(1f, totalValue), 0.3f, asteroidPhysicalSize);
                return;
            }

            float normalizedSize = Mathf.Clamp01((asteroidSize - 1f) / (50f - 1f));
            
            int gemCount;
            float minGemValue, maxGemValue;
            
            if (normalizedSize < 0.3f)
            {
                gemCount = Random.Range(2, 5);
                minGemValue = 1f;
                maxGemValue = 15f;
            }
            else if (normalizedSize < 0.7f)
            {
                gemCount = Random.Range(2, 5);
                minGemValue = 1f;
                maxGemValue = 35f;
            }
            else
            {
                if (normalizedSize >= 0.9f)
                {
                    gemCount = Random.Range(1, 4);
                    minGemValue = 20f;
                    maxGemValue = 50f;
                }
                else
                {
                    gemCount = Random.Range(2, 4);
                    minGemValue = 10f;
                    maxGemValue = 45f;
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

        /// <summary>Spawns a gem expelled from ship toward planet for deposit. Gem flies to planet and is absorbed on contact. Size scales with amount (deposit rate).</summary>
        public void SpawnDepositGem(Vector3 shipPosition, Vector3 planetPosition, float amount, ulong planetNetworkObjectId, TitanOrbit.Core.TeamManager.Team depositingTeam, ulong depositingClientId)
        {
            GameObject prefab = GetGemPrefab();
            if (prefab == null || amount <= 0f) return;

            Vector3 dir = (planetPosition - shipPosition);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
            else dir.Normalize();

            // Spawn at ship position (inside orbit zone) - gem will fly toward planet and absorb on planet body contact only
            Vector3 pos = shipPosition;
            float depositSpeed = 8f;
            float sizeMult = Mathf.Lerp(0.5f, 1.8f, Mathf.Clamp01(amount / 10f));

            GameObject gemObj = Instantiate(prefab, pos, Quaternion.identity);
            Rigidbody rb = gemObj.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = dir * depositSpeed;
                rb.angularVelocity = new Vector3(Random.Range(-2f, 2f), Random.Range(-2f, 2f), Random.Range(-2f, 2f));
            }

            NetworkObject netObj = gemObj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                netObj.Spawn();
                Gem gem = gemObj.GetComponent<Gem>();
                if (gem != null) gem.InitializeForDeposit(amount, sizeMult, planetNetworkObjectId, depositingTeam, depositingClientId);
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
                rb.angularVelocity = new Vector3(
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f));
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
                rb.angularVelocity = new Vector3(
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f),
                    Random.Range(-1.5f, 1.5f));
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
